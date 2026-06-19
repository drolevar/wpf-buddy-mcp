using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using WpfBuddy.Mcp.Server.Models;
using Application = FlaUI.Core.Application;

namespace WpfBuddy.Mcp.Server.Services;

public sealed class SessionManager : IDisposable
{
    private readonly object _lock = new();
    private Application? _application;
    private UIA3Automation? _automation;
    private Window? _activeWindow;
    private string _sessionId = string.Empty;
    private DateTime? _attachedAtUtc;

    /// <summary>Raised after any attach/detach/launch so transient singletons can reset stale per-session state.</summary>
    public event Action? SessionChanged;

    public bool IsAttached { get { lock (_lock) return _application is not null && _automation is not null; } }
    public string SessionId { get { lock (_lock) return _sessionId; } }
    public Application? Application { get { lock (_lock) return _application; } }
    public UIA3Automation? Automation { get { lock (_lock) return _automation; } }

    public Window? ActiveWindow
    {
        get
        {
            lock (_lock)
            {
                if (_activeWindow is null && _application is not null && _automation is not null)
                {
                    _activeWindow = _application.GetMainWindow(_automation, TimeSpan.FromSeconds(5));
                }
                return _activeWindow;
            }
        }
        set { lock (_lock) _activeWindow = value; }
    }

    public SessionInfo GetStatus()
    {
        lock (_lock)
        {
            return new SessionInfo
            {
                SessionId = _sessionId,
                ProcessId = _application?.ProcessId,
                ProcessName = GetProcessName(),
                MainWindowTitle = ActiveWindow?.Title,
                MainWindowHandle = ActiveWindow?.Properties.NativeWindowHandle.ValueOrDefault.ToString(),
                IsAttached = _application is not null && _automation is not null,
                AttachedAtUtc = _attachedAtUtc
            };
        }
    }

    public void AttachByPid(int processId)
    {
        lock (_lock)
        {
            DetachInternal();
            var process = Process.GetProcessById(processId);
            _application = Application.Attach(process);
            _automation = new UIA3Automation();
            _sessionId = Guid.NewGuid().ToString("N")[..12];
            _attachedAtUtc = DateTime.UtcNow;
            _activeWindow = null;
        }
        SessionChanged?.Invoke();
    }

    public void AttachByName(string processName)
    {
        lock (_lock)
        {
            DetachInternal();
            var process = Process.GetProcessesByName(processName).FirstOrDefault()
                ?? throw new InvalidOperationException($"Process '{processName}' not found.");
            _application = Application.Attach(process);
            _automation = new UIA3Automation();
            _sessionId = Guid.NewGuid().ToString("N")[..12];
            _attachedAtUtc = DateTime.UtcNow;
            _activeWindow = null;
        }
        SessionChanged?.Invoke();
    }

    public void Launch(string executablePath, string? arguments = null, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new FileNotFoundException($"Executable not found: '{executablePath}'.");
        lock (_lock)
        {
            DetachInternal();
            var processStartInfo = new ProcessStartInfo(executablePath)
            {
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executablePath) ?? string.Empty
            };
            _application = Application.Launch(processStartInfo);
            _automation = new UIA3Automation();
            _sessionId = Guid.NewGuid().ToString("N")[..12];
            _attachedAtUtc = DateTime.UtcNow;
            _activeWindow = null;
        }
        SessionChanged?.Invoke();
    }

    public void Detach()
    {
        lock (_lock) DetachInternal();
        SessionChanged?.Invoke();
    }

    private void DetachInternal()
    {
        _activeWindow = null;
        _automation?.Dispose();
        _automation = null;
        // Dispose releases the FlaUI process handle (it does NOT close/kill the attached app —
        // Close/Kill are separate methods); prevents a process-handle leak across sessions.
        try { _application?.Dispose(); } catch { }
        _application = null;
        _sessionId = string.Empty;
        _attachedAtUtc = null;
    }

    public void SetActiveWindow(Window window)
    {
        lock (_lock) _activeWindow = window;
    }

    /// <summary>
    /// Re-resolve the application's currently-active top-level window (e.g. a modal dialog opened on
    /// top of the main window) and cache it as the active window. The autonomous explorer calls this
    /// each step so it inspects the window actually on screen instead of a stale cached main window
    /// (R2-20). Prefers the OS foreground window when it belongs to this app, then a modal dialog,
    /// and finally falls back to (re)resolving the main window.
    /// </summary>
    public Window? RefreshActiveWindow()
    {
        lock (_lock)
        {
            if (_application is null || _automation is null) return _activeWindow;
            try
            {
                var windows = _application.GetAllTopLevelWindows(_automation);
                if (windows is { Length: > 0 })
                {
                    var foreground = GetForegroundWindow();
                    Window? chosen = windows.FirstOrDefault(w =>
                    {
                        try { return w.Properties.NativeWindowHandle.ValueOrDefault == foreground; }
                        catch { return false; }
                    });
                    chosen ??= windows.FirstOrDefault(IsModalWindow);
                    if (chosen is not null)
                    {
                        _activeWindow = chosen;
                        return _activeWindow;
                    }
                }
            }
            catch { }

            // Fall back to (re)resolving the main window.
            try { _activeWindow = _application.GetMainWindow(_automation, TimeSpan.FromSeconds(2)); }
            catch { }
            return _activeWindow;
        }
    }

    private static bool IsModalWindow(Window w)
    {
        try { return w.Patterns.Window.PatternOrDefault?.IsModal.ValueOrDefault == true; }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private string? GetProcessName()
    {
        if (_application is null) return null;
        try
        {
            using var process = Process.GetProcessById(_application.ProcessId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock) DetachInternal();
    }
}
