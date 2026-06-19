using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace WpfBuddy.Mcp.Probe;

/// <summary>
/// In-process probe that runs inside the target WPF application.
/// Communicates with the MCP server via named pipe IPC.
/// Install by calling ProbeHost.Start() in the WPF app's startup.
/// </summary>
public sealed class ProbeHost : IDisposable
{
    // UTF-8 WITHOUT a BOM. With a BOM, StreamWriter.AutoFlush=true flushes the
    // preamble in its setter, calling the pipe's FlushFileBuffers, which blocks
    // until the peer reads those bytes. Since both ends construct their writer the
    // same way before reading, that mutually deadlocks ("connected, then nothing").
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private static ProbeHost? _instance;

    public static ProbeHost? Instance => _instance;
    public string PipeName => _pipeName;
    public bool IsRunning => _listenTask is not null && !_listenTask.IsCompleted;

    private ProbeHost(string pipeName)
    {
        _pipeName = pipeName;
    }

    /// <summary>
    /// Start the probe in the current WPF application process.
    /// Call this from App.xaml.cs OnStartup or a similar entry point.
    /// </summary>
    public static ProbeHost Start(string? pipeName = null)
    {
        pipeName ??= $"wpfbuddy-mcp-probe-{System.Diagnostics.Process.GetCurrentProcess().Id}";

        Debug.WriteLine($"Starting ProbeHost with pipe name: {pipeName}");

        var host = new ProbeHost(pipeName);
        host.StartListening();
        _instance = host;
        return host;
    }

    private void StartListening()
    {
        _cts = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                
                Debug.WriteLine($"Pipe connected: {_pipeName}");

                using var reader = new StreamReader(pipe, Utf8NoBom);
                using var writer = new StreamWriter(pipe, Utf8NoBom) { AutoFlush = true };

                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;

                    Debug.WriteLine($"Received request: {line}");

                    var response = await ProcessRequest(line);
                    
                    Debug.WriteLine($"Sending response: {response}");
                    
                    await writer.WriteLineAsync(response);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Reconnect on error
                await Task.Delay(100, ct);
            }
        }
    }

    private static readonly JsonSerializerOptions RequestOptions = new() { PropertyNameCaseInsensitive = true };

    private async Task<string> ProcessRequest(string requestJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ProbeRequest>(requestJson, RequestOptions);
            if (request is null)
                return JsonSerializer.Serialize(new ProbeResponse { Error = "Invalid request" });

            return request.Method switch
            {
                "ping" => JsonSerializer.Serialize(new ProbeResponse { Result = "pong" }),
                "get_datacontext" => await RunOnDispatcher(() => GetDataContext(request)),
                "get_viewmodel_properties" => await RunOnDispatcher(() => GetViewModelProperties(request)),
                "get_binding_errors" => await RunOnDispatcher(() => GetBindingErrors()),
                "get_bindings" => await RunOnDispatcher(() => GetBindings(request)),
                "get_command_state" => await RunOnDispatcher(() => GetCommandState(request)),
                "get_validation_state" => await RunOnDispatcher(() => GetValidationState(request)),
                "execute_command" => await RunOnDispatcher(() => ExecuteCommand(request)),
                "get_dispatcher_status" => await RunOnDispatcher(() => GetDispatcherStatus()),
                _ => JsonSerializer.Serialize(new ProbeResponse { Error = $"Unknown method: {request.Method}" })
            };
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new ProbeResponse { Error = ex.Message });
        }
    }

    private async Task<string> RunOnDispatcher(Func<string> action)
    {
        string result = "";
        await Application.Current.Dispatcher.InvokeAsync(() => result = action());
        return result;
    }

    private string GetDataContext(ProbeRequest request)
    {
        var window = GetTargetWindow(request);
        if (window is null)
            return JsonSerializer.Serialize(new ProbeResponse { Error = "Window not found" });

        var dc = window.DataContext;
        if (dc is null)
            return JsonSerializer.Serialize(new ProbeResponse { Result = "null" });

        var type = dc.GetType();
        var props = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => new { name = p.Name, type = p.PropertyType.Name, value = SafeGetValue(p, dc) })
            .ToList();

        return JsonSerializer.Serialize(new ProbeResponse
        {
            Data = JsonSerializer.Serialize(new
            {
                typeName = type.FullName,
                properties = props
            })
        });
    }

    private string GetViewModelProperties(ProbeRequest request)
    {
        var window = GetTargetWindow(request);
        var dc = window?.DataContext;
        if (dc is null)
            return JsonSerializer.Serialize(new ProbeResponse { Error = "No DataContext" });

        var type = dc.GetType();
        var props = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => new
            {
                name = p.Name,
                type = p.PropertyType.Name,
                canRead = p.CanRead,
                canWrite = p.CanWrite,
                value = SafeGetValue(p, dc)
            })
            .ToList();

        return JsonSerializer.Serialize(new ProbeResponse { Data = JsonSerializer.Serialize(props) });
    }

    private string GetBindingErrors()
    {
        // PresentationTraceSources.DataBindingSource listeners don't store messages.
        // We capture errors by checking validation on all windows instead.
        var errors = new List<object>();
        foreach (var window in Application.Current.Windows.OfType<Window>())
        {
            var windowErrors = System.Windows.Controls.Validation.GetErrors(window);
            foreach (var error in windowErrors)
            {
                errors.Add(new
                {
                    window = window.Title,
                    message = error.ErrorContent?.ToString(),
                    bindingPath = (error.BindingInError as System.Windows.Data.BindingExpression)?.ParentBinding.Path.Path
                });
            }
        }

        return JsonSerializer.Serialize(new ProbeResponse { Data = JsonSerializer.Serialize(new { errorCount = errors.Count, errors }) });
    }

    private string GetBindings(ProbeRequest request)
    {
        var window = GetTargetWindow(request);
        if (window is null)
            return JsonSerializer.Serialize(new ProbeResponse { Error = "Window not found" });

        var bindings = new List<object>();
        CollectBindings(window, bindings);

        return JsonSerializer.Serialize(new ProbeResponse { Data = JsonSerializer.Serialize(new { count = bindings.Count, bindings = bindings.Take(50) }) });
    }

    private string GetCommandState(ProbeRequest request)
    {
        var window = GetTargetWindow(request);
        var dc = window?.DataContext;
        if (dc is null)
            return JsonSerializer.Serialize(new ProbeResponse { Error = "No DataContext" });

        var type = dc.GetType();
        var commands = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p =>
            {
                var cmd = p.GetValue(dc) as System.Windows.Input.ICommand;
                return new { name = p.Name, canExecute = cmd?.CanExecute(null) ?? false };
            })
            .ToList();

        return JsonSerializer.Serialize(new ProbeResponse { Data = JsonSerializer.Serialize(commands) });
    }

    private string GetValidationState(ProbeRequest request)
    {
        var window = GetTargetWindow(request);
        if (window is null)
            return JsonSerializer.Serialize(new ProbeResponse { Error = "Window not found" });

        var errors = System.Windows.Controls.Validation.GetErrors(window);
        var errorList = errors.Select(e => new
        {
            message = e.ErrorContent?.ToString(),
            bindingPath = (e.BindingInError as System.Windows.Data.BindingExpression)?.ParentBinding.Path.Path
        }).ToList();

        return JsonSerializer.Serialize(new ProbeResponse { Data = JsonSerializer.Serialize(new { hasErrors = errorList.Count > 0, errors = errorList }) });
    }

    private string ExecuteCommand(ProbeRequest request)
    {
        var window = GetTargetWindow(request);
        var dc = window?.DataContext;
        if (dc is null)
            return JsonSerializer.Serialize(new ProbeResponse { Error = "No DataContext" });

        var commandName = request.Parameters?.GetValueOrDefault("commandName");
        if (string.IsNullOrEmpty(commandName))
            return JsonSerializer.Serialize(new ProbeResponse { Error = "commandName required" });

        var prop = dc.GetType().GetProperty(commandName);
        var cmd = prop?.GetValue(dc) as System.Windows.Input.ICommand;
        if (cmd is null)
            return JsonSerializer.Serialize(new ProbeResponse { Error = $"Command '{commandName}' not found" });

        if (!cmd.CanExecute(null))
            return JsonSerializer.Serialize(new ProbeResponse { Error = $"Command '{commandName}' cannot execute" });

        cmd.Execute(request.Parameters?.GetValueOrDefault("parameter"));
        return JsonSerializer.Serialize(new ProbeResponse { Result = "executed" });
    }

    private string GetDispatcherStatus()
    {
        var dispatcher = Application.Current.Dispatcher;
        return JsonSerializer.Serialize(new ProbeResponse
        {
            Data = JsonSerializer.Serialize(new
            {
                hasShutdownStarted = dispatcher.HasShutdownStarted,
                hasShutdownFinished = dispatcher.HasShutdownFinished,
                thread = dispatcher.Thread.Name ?? $"Thread-{dispatcher.Thread.ManagedThreadId}"
            })
        });
    }

    private static Window? GetTargetWindow(ProbeRequest request)
    {
        var title = request.Parameters?.GetValueOrDefault("windowTitle");
        if (!string.IsNullOrEmpty(title))
            return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        return Application.Current.MainWindow;
    }

    private static void CollectBindings(DependencyObject obj, List<object> bindings, int depth = 0)
    {
        if (depth > 5 || bindings.Count >= 50) return;

        var localValueEnumerator = obj.GetLocalValueEnumerator();
        while (localValueEnumerator.MoveNext())
        {
            var entry = localValueEnumerator.Current;
            var binding = System.Windows.Data.BindingOperations.GetBinding(obj, entry.Property);
            if (binding is not null)
            {
                bindings.Add(new
                {
                    property = entry.Property.Name,
                    path = binding.Path?.Path,
                    mode = binding.Mode.ToString(),
                    elementType = obj.GetType().Name
                });
            }
        }

        var childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj);
        for (int i = 0; i < childCount && bindings.Count < 50; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(obj, i);
            CollectBindings(child, bindings, depth + 1);
        }
    }

    private static string? SafeGetValue(System.Reflection.PropertyInfo prop, object obj)
    {
        try
        {
            var val = prop.GetValue(obj);
            if (val is null) return "null";
            if (val is string s) return s.Length > 100 ? s[..100] + "..." : s;
            if (val.GetType().IsPrimitive || val is decimal || val is DateTime) return val.ToString();
            if (val is System.Collections.ICollection col)
                return $"[Collection: {col.Count} items]";
            if (val is System.Collections.IEnumerable)
                return $"[{val.GetType().Name}]";
            return $"[{val.GetType().Name}]";
        }
        catch { return "[error]"; }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listenTask?.Wait(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
        _instance = null;
    }
}

internal static class DictionaryExtensions
{
    public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key)
        where TKey : notnull
        => dictionary.TryGetValue(key, out var value) ? value : default;
}

internal static class StreamReaderExtensions
{
    /// <summary>
    /// net48 polyfill for StreamReader.ReadLineAsync(CancellationToken) (added in .NET 7).
    /// The underlying read is not cancelable, so when the token fires this stops awaiting
    /// and throws OperationCanceledException; the in-flight read completes in the background.
    /// </summary>
    public static async Task<string?> ReadLineAsync(this StreamReader reader, CancellationToken ct)
    {
        var readTask = reader.ReadLineAsync();
        if (!ct.CanBeCanceled)
            return await readTask;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), tcs))
        {
            if (await Task.WhenAny(readTask, tcs.Task) != readTask)
                ct.ThrowIfCancellationRequested();
        }
        return await readTask;
    }
}

public class ProbeRequest
{
    public string Method { get; set; } = "";
    public Dictionary<string, string>? Parameters { get; set; }
}

public class ProbeResponse
{
    public string? Result { get; set; }
    public string? Data { get; set; }
    public string? Error { get; set; }
}
