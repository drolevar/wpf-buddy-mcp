using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WpfBuddy.Mcp.Server.Services;

/// <summary>
/// Client for communicating with the in-process probe via named pipe.
/// </summary>
public sealed class ProbeClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string? _pipeName;

    // The probe handles one request at a time over a line-framed pipe, so only one
    // SendAsync may be in flight: concurrent calls would interleave writes/reads and
    // mis-correlate responses. Serialize the write+read pair.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // UTF-8 WITHOUT a BOM. A BOM makes StreamWriter.AutoFlush=true flush the preamble
    // in its setter (FlushFileBuffers), which blocks until the peer reads it; since
    // both ends do this before reading, it deadlocks ("connected, then nothing").
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // File log of probe IPC traffic (override location with WPFBUDDY_PROBE_LOG).
    // By default we log only metadata (method, direction, payload size): request/response
    // payloads carry live ViewModel values (PII), which the MCP spec says logs MUST NOT
    // contain. Set WPFBUDDY_PROBE_LOG_VALUES=1 to include full payloads for local debugging.
    private static readonly string LogPath =
        Environment.GetEnvironmentVariable("WPFBUDDY_PROBE_LOG")
        ?? Path.Combine(Path.GetTempPath(), "wpfbuddy-mcp-probe.log");
    private static readonly bool LogValues =
        Environment.GetEnvironmentVariable("WPFBUDDY_PROBE_LOG_VALUES") is "1" or "true";

    private static void Log(string direction, string message) =>
        LogFileWriter.Append(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{direction}] {message}");

    public bool IsConnected => _pipe?.IsConnected == true;
    public string? PipeName => _pipeName;

    public async Task<bool> ConnectAsync(int processId, int timeoutMs = 3000)
    {
        _pipeName = $"wpfbuddy-mcp-probe-{processId}";
        return await ConnectToPipeAsync(_pipeName, timeoutMs);
    }

    public async Task<bool> ConnectAsync(string pipeName, int timeoutMs = 3000)
    {
        _pipeName = pipeName;
        return await ConnectToPipeAsync(pipeName, timeoutMs);
    }

    private async Task<bool> ConnectToPipeAsync(string pipeName, int timeoutMs)
    {
        try
        {
            Log("CONNECT", $"attempting to connect to pipe '{pipeName}' (timeout {timeoutMs}ms)");
            Disconnect();
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            _pipe = pipe;

            // NamedPipeClientStream.ConnectAsync does NOT reliably honor its timeout or
            // cancellation token on Windows (the native connect can block uninterruptibly).
            // Enforce a hard timeout externally with WaitAsync so this method always returns;
            // on timeout, dispose the pipe to abandon the stuck connect.
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await pipe.ConnectAsync(cts.Token).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            }
            catch (TimeoutException)
            {
                Log("CONNECT", $"ConnectAsync did not return within {timeoutMs}ms; abandoning pipe '{pipeName}'");
                cts.Cancel();
                try { pipe.Dispose(); } catch { /* unblock the stuck connect */ }
                Disconnect();
                return false;
            }

            _reader = new StreamReader(pipe, Utf8NoBom);
            _writer = new StreamWriter(pipe, Utf8NoBom) { AutoFlush = true };
            Log("CONNECT", $"connected to pipe '{pipeName}'");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log("CONNECT", $"timed out connecting to pipe '{pipeName}' after {timeoutMs}ms");
            Disconnect();
            return false;
        }
        catch (Exception ex)
        {
            Log("CONNECT", $"failed to connect to pipe '{pipeName}': {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public async Task<ProbeResponse?> SendAsync(string method, Dictionary<string, string>? parameters = null, int timeoutMs = 10000)
    {
        await _gate.WaitAsync();
        try
        {
            // One-shot reconnect if the pipe dropped but we still know where it was.
            if (!IsConnected && _pipeName is not null)
            {
                Log("RECONNECT", $"not connected; attempting one-shot reconnect to '{_pipeName}'");
                await ConnectToPipeAsync(_pipeName, 3000);
            }

            if (!IsConnected || _writer is null || _reader is null)
            {
                Log("SEND", $"'{method}' rejected: not connected to probe");
                return new ProbeResponse { Error = "Not connected to probe." };
            }

            // Capture into locals so a concurrent Disconnect() can't null them mid-call.
            var writer = _writer;
            var reader = _reader;
            try
            {
                var request = JsonSerializer.Serialize(new { method, parameters });
                Log("SEND", LogValues ? request : $"{method} ({request.Length} chars)");
                await writer.WriteLineAsync(request);

                using var cts = new CancellationTokenSource(timeoutMs);
                var responseLine = await reader.ReadLineAsync(cts.Token);
                if (responseLine is null)
                {
                    Log("RECV", $"'{method}': null (probe closed the pipe)");
                    Disconnect();
                    return new ProbeResponse { Error = "No response from probe." };
                }
                Log("RECV", LogValues ? responseLine : $"{method} -> {responseLine.Length} chars");
                return JsonSerializer.Deserialize<ProbeResponse>(responseLine);
            }
            catch (OperationCanceledException)
            {
                // A timed-out request leaves the line-framed channel desynchronized — every
                // later read would return a stale response. Poison the channel so the next
                // call reconnects instead.
                Log("RECV", $"'{method}': timed out after {timeoutMs}ms; poisoning channel");
                Disconnect();
                return new ProbeResponse { Error = "Probe request timed out." };
            }
            catch (Exception ex)
            {
                Log("RECV", $"'{method}': exception: {ex.Message}; poisoning channel");
                Disconnect();
                return new ProbeResponse { Error = ex.Message };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Names of probe pipes currently published on this machine (wpfbuddy-mcp-probe-*).</summary>
    public static IReadOnlyList<string> EnumerateProbePipes()
    {
        try
        {
            return Directory.GetFiles(@"\\.\pipe\")
                .Select(Path.GetFileName)
                .Where(n => n is not null && n.StartsWith("wpfbuddy-mcp-probe-", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void Disconnect()
    {
        // Dispose the writer first (it flushes) while the pipe is still open, and guard each:
        // the reader and writer both own the shared pipe, so disposing one closes it — flushing
        // the other over a closed pipe would throw ObjectDisposedException.
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public void Dispose()
    {
        Disconnect();
        _gate.Dispose();
    }
}

public class ProbeResponse
{
    public string? Result { get; set; }
    public string? Data { get; set; }
    public string? Error { get; set; }
}
