using System.Diagnostics.Tracing;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
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

    // Probe IPC payloads carry live ViewModel values; emit them to the EventSource only
    // when explicitly opted in for local debugging.
    private static readonly bool LogValues =
        Environment.GetEnvironmentVariable("WPFBUDDY_PROBE_LOG_VALUES") is "1" or "true";

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

        ProbeEventSource.Log.Starting(pipeName);

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

    private NamedPipeServerStream CreateServerPipe()
    {
        // Restrict the pipe to the current user so other local users/processes can't
        // connect and drive the target app's ViewModel commands.
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User;
        if (user is not null)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
#if NETFRAMEWORK
        return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
#else
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
#endif
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = CreateServerPipe();
                await pipe.WaitForConnectionAsync(ct);
                
                ProbeEventSource.Log.Connected(_pipeName);

                using var reader = new StreamReader(pipe, Utf8NoBom);
                using var writer = new StreamWriter(pipe, Utf8NoBom) { AutoFlush = true };

                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;

                    ProbeEventSource.Log.Request(line.Length);
                    if (LogValues) ProbeEventSource.Log.Payload("REQ", line);

                    var response = await ProcessRequest(line);
                    
                    ProbeEventSource.Log.Response(response.Length);
                    if (LogValues) ProbeEventSource.Log.Payload("RESP", response);
                    
                    await writer.WriteLineAsync(response);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ProbeEventSource.Log.Error(ex.Message);
                // Tear down and recreate the server pipe on error.
                try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
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
                "info" => GetInfo(),
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
        var app = Application.Current;
        if (app?.Dispatcher is null)
            return JsonSerializer.Serialize(new ProbeResponse
            {
                Error = "No WPF Application/Dispatcher available — is the probe running inside a WPF app, started after App init?"
            });

        try
        {
            string result = "";
            var op = app.Dispatcher.InvokeAsync(() => result = action());
            // Bound the UI-thread work so a hung dispatcher surfaces as an error, not a hang.
            if (await Task.WhenAny(op.Task, Task.Delay(TimeSpan.FromSeconds(10))) != op.Task)
                return JsonSerializer.Serialize(new ProbeResponse { Error = "UI thread unresponsive (dispatcher invoke timed out)." });
            await op.Task;
            return result;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new ProbeResponse { Error = ex.Message });
        }
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

        // R2-29: scope to the requested element's subtree when an automationId/name is supplied, so
        // the bindings are element-specific; fall back to window-wide when no such element is found.
        var automationId = request.Parameters?.GetValueOrDefault("automationId");
        var name = request.Parameters?.GetValueOrDefault("name");
        DependencyObject root = window;
        bool scoped = false;
        if (!string.IsNullOrEmpty(automationId) || !string.IsNullOrEmpty(name))
        {
            var target = FindElement(window, automationId, name);
            if (target is not null)
            {
                root = target;
                scoped = true;
            }
        }

        var bindings = new List<object>();
        CollectBindings(root, bindings);

        return JsonSerializer.Serialize(new ProbeResponse { Data = JsonSerializer.Serialize(new { scoped, count = bindings.Count, bindings = bindings.Take(50) }) });
    }

    // Depth-first search of the visual tree for a FrameworkElement matching the given AutomationId
    // (AutomationProperties.AutomationId) or x:Name. Used to element-scope binding collection (R2-29).
    private static DependencyObject? FindElement(DependencyObject root, string? automationId, string? name)
    {
        if (root is FrameworkElement fe)
        {
            if (!string.IsNullOrEmpty(automationId) &&
                string.Equals(System.Windows.Automation.AutomationProperties.GetAutomationId(fe), automationId, StringComparison.Ordinal))
                return fe;
            if (!string.IsNullOrEmpty(name) && string.Equals(fe.Name, name, StringComparison.Ordinal))
                return fe;
        }

        var childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            var found = FindElement(System.Windows.Media.VisualTreeHelper.GetChild(root, i), automationId, name);
            if (found is not null) return found;
        }
        return null;
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

    private string GetInfo()
    {
        var name = typeof(ProbeHost).Assembly.GetName();
        return JsonSerializer.Serialize(new ProbeResponse
        {
            Data = JsonSerializer.Serialize(new
            {
                version = name.Version?.ToString(),
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                pipeName = _pipeName
            })
        });
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

/// <summary>
/// Structured logging sink for the probe. Works identically on net48 and net8.0-windows,
/// is zero-cost when no listener is attached, and is consumable via ETW/PerfView without a
/// debugger. Replaces Debug.WriteLine (compiled out in Release). Logs metadata only by
/// default; full payloads only when WPFBUDDY_PROBE_LOG_VALUES is set.
/// </summary>
[EventSource(Name = "WpfBuddy-Mcp-Probe")]
internal sealed class ProbeEventSource : EventSource
{
    public static readonly ProbeEventSource Log = new();
    private ProbeEventSource() { }

    [Event(1, Level = EventLevel.Informational, Message = "Probe starting on pipe {0}")]
    public void Starting(string pipeName) => WriteEvent(1, pipeName);

    [Event(2, Level = EventLevel.Informational, Message = "Client connected on pipe {0}")]
    public void Connected(string pipeName) => WriteEvent(2, pipeName);

    [Event(3, Level = EventLevel.Verbose, Message = "Request received ({0} chars)")]
    public void Request(int length) => WriteEvent(3, length);

    [Event(4, Level = EventLevel.Verbose, Message = "Response sent ({0} chars)")]
    public void Response(int length) => WriteEvent(4, length);

    [Event(5, Level = EventLevel.Error, Message = "Listen loop error: {0}")]
    public void Error(string message) => WriteEvent(5, message);

    [Event(6, Level = EventLevel.Verbose, Message = "{0} payload: {1}")]
    public void Payload(string direction, string json) => WriteEvent(6, direction, json);
}
