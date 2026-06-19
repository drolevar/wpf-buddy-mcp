using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class SessionTools
{
    private readonly SessionManager _session;
    private readonly AuditLog _audit;

    public SessionTools(SessionManager session, AuditLog audit)
    {
        _session = session;
        _audit = audit;
    }

    [McpServerTool(Name = "wpf_list_apps", ReadOnly = true), Description("List candidate WPF/Windows desktop processes and top-level windows.")]
    public string ListApps()
    {
        _audit.Record("wpf_list_apps");
        var allProcesses = Process.GetProcesses();
        try
        {
            var processes = allProcesses
                .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle))
                .Select(p => new AppInfo
                {
                    ProcessId = p.Id,
                    ProcessName = p.ProcessName,
                    MainWindowTitle = p.MainWindowTitle,
                    ExecutablePath = GetSafeExecutablePath(p)
                })
                .OrderBy(a => a.ProcessName)
                .ToList();

            return JsonSerializer.Serialize(processes, JsonOptions.Default);
        }
        finally
        {
            foreach (var p in allProcesses) p.Dispose();
        }
    }

    [McpServerTool(Name = "wpf_launch_app", Destructive = false), Description("Launch app from executable path with optional args and working directory.")]
    public string LaunchApp(
        [Description("Required. Full path to the executable to launch (e.g. C:\\Apps\\MyApp.exe).")] string executablePath,
        [Description("Optional command-line arguments to pass to the process.")] string? arguments = null,
        [Description("Optional working directory for the launched process; defaults to the executable's directory.")] string? workingDirectory = null)
    {
        _audit.Record("wpf_launch_app", parameters: new() { ["path"] = executablePath, ["args"] = arguments });
        _session.Launch(executablePath, arguments, workingDirectory);
        var status = _session.GetStatus();
        return JsonSerializer.Serialize(status, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_attach", Destructive = false), Description("Attach to a running process/window by PID, process name, or window title.")]
    public string Attach(
        [Description("Process ID (PID) of the target app; takes precedence when provided. Supply this or processName.")] int? processId = null,
        [Description("Process name to match (without .exe); used when processId is omitted. Supply this or processId.")] string? processName = null)
    {
        _audit.Record("wpf_attach", parameters: new() { ["pid"] = processId, ["name"] = processName });

        if (processId.HasValue)
        {
            _session.AttachByPid(processId.Value);
        }
        else if (!string.IsNullOrEmpty(processName))
        {
            _session.AttachByName(processName);
        }
        else
        {
            throw new McpException("Provide processId or processName.");
        }

        var status = _session.GetStatus();
        return JsonSerializer.Serialize(status, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_detach", Destructive = false, Idempotent = true), Description("Detach from current app session.")]
    public string Detach()
    {
        _audit.Record("wpf_detach");
        _session.Detach();
        return JsonSerializer.Serialize(new { result = "detached" }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_session_status", ReadOnly = true), Description("Return current attachment, window handle, PID, app state, active window.")]
    public string SessionStatus()
    {
        var status = _session.GetStatus();
        return JsonSerializer.Serialize(status, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_focus_window", Destructive = false, Idempotent = true), Description("Bring attached app/window to foreground.")]
    public string FocusWindow()
    {
        if (!_session.IsAttached || _session.ActiveWindow is null)
            throw new McpException("No window attached.");

        _audit.Record("wpf_focus_window");
        _session.ActiveWindow.SetForeground();
        return JsonSerializer.Serialize(new { result = "focused" }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_list_windows", ReadOnly = true), Description("List top-level, modal, popup, owned, and child windows for attached process.")]
    public string ListWindows()
    {
        if (!_session.IsAttached || _session.Automation is null)
            throw new McpException("No app attached.");

        _audit.Record("wpf_list_windows");
        var windows = _session.Application!.GetAllTopLevelWindows(_session.Automation);
        var result = windows.Select(w => new
        {
            title = w.Title,
            automationId = w.Properties.AutomationId.ValueOrDefault,
            handle = w.Properties.NativeWindowHandle.ValueOrDefault.ToString(),
            isModal = w.IsModal
        }).ToList();

        return JsonSerializer.Serialize(result, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_select_window", Destructive = false, Idempotent = true), Description("Switch active target window within attached process by title or automation id.")]
    public string SelectWindow(
        [Description("Window title to match (case-insensitive substring); used when automationId is omitted.")] string? title = null,
        [Description("AutomationId of the target window (exact match); takes precedence over title.")] string? automationId = null)
    {
        if (!_session.IsAttached || _session.Automation is null)
            throw new McpException("No app attached.");

        _audit.Record("wpf_select_window", parameters: new() { ["title"] = title, ["automationId"] = automationId });
        var windows = _session.Application!.GetAllTopLevelWindows(_session.Automation);

        var target = windows.FirstOrDefault(w =>
            (!string.IsNullOrEmpty(title) && w.Title?.Contains(title, StringComparison.OrdinalIgnoreCase) == true) ||
            (!string.IsNullOrEmpty(automationId) && w.Properties.AutomationId.ValueOrDefault == automationId));

        if (target is null)
            throw new McpException("Window not found.");

        _session.SetActiveWindow(target);
        return JsonSerializer.Serialize(new { result = "selected", title = target.Title }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_close_window", Destructive = true), Description("Close a window through normal close command.")]
    public string CloseWindow(
        [Description("Window title to match (case-insensitive substring); used when automationId is omitted. If neither matches, the active window is closed.")] string? title = null,
        [Description("AutomationId of the target window (exact match); takes precedence over title. If neither matches, the active window is closed.")] string? automationId = null)
    {
        if (!_session.IsAttached || _session.Automation is null)
            throw new McpException("No app attached.");

        _audit.Record("wpf_close_window", parameters: new() { ["title"] = title, ["automationId"] = automationId });
        var windows = _session.Application!.GetAllTopLevelWindows(_session.Automation);
        var target = windows.FirstOrDefault(w =>
            (!string.IsNullOrEmpty(title) && w.Title?.Contains(title, StringComparison.OrdinalIgnoreCase) == true) ||
            (!string.IsNullOrEmpty(automationId) && w.Properties.AutomationId.ValueOrDefault == automationId));

        if (target is null && _session.ActiveWindow is not null)
            target = _session.ActiveWindow;

        if (target is null)
            throw new McpException("Window not found.");

        target.Close();
        return JsonSerializer.Serialize(new { result = "closed" }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_window_state", ReadOnly = true), Description("Get minimized/maximized/normal/focused/modal state.")]
    public string GetWindowState()
    {
        if (!_session.IsAttached || _session.ActiveWindow is null)
            throw new McpException("No window attached.");

        var window = _session.ActiveWindow;
        var result = new
        {
            title = window.Title,
            isModal = window.IsModal,
            isFocused = window.Properties.HasKeyboardFocus.ValueOrDefault,
            windowVisualState = GetVisualState(window),
            bounds = new
            {
                x = window.BoundingRectangle.X,
                y = window.BoundingRectangle.Y,
                width = window.BoundingRectangle.Width,
                height = window.BoundingRectangle.Height
            }
        };
        return JsonSerializer.Serialize(result, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_set_window_state", Destructive = false, Idempotent = true), Description("Minimize, maximize, or restore a window.")]
    public string SetWindowState(
        [Description("Required. Target window state. Allowed values: minimize/minimized, maximize/maximized, restore/normal.")] string state)
    {
        if (!_session.IsAttached || _session.ActiveWindow is null)
            throw new McpException("No window attached.");

        _audit.Record("wpf_set_window_state", parameters: new() { ["state"] = state });
        var window = _session.ActiveWindow;

        if (!window.Patterns.Window.IsSupported)
            throw new McpException("Window pattern not supported.");

        switch (state.ToLowerInvariant())
        {
            case "minimize" or "minimized":
                window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Minimized);
                break;
            case "maximize" or "maximized":
                window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
                break;
            case "restore" or "normal":
                window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Normal);
                break;
            default:
                throw new McpException($"Unknown state: {state}. Use minimize, maximize, or restore.");
        }

        return JsonSerializer.Serialize(new { result = "state_changed", state }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_app_metadata", ReadOnly = true), Description("Get app version, executable path, process bitness, framework if detectable.")]
    public string GetAppMetadata()
    {
        if (!_session.IsAttached || _session.Application is null)
            throw new McpException("No app attached.");

        try
        {
            using var process = Process.GetProcessById(_session.Application.ProcessId);
            var module = process.MainModule;
            var is64Bit = !IsWow64Process(process);
            var result = new
            {
                processId = process.Id,
                processName = process.ProcessName,
                executablePath = module?.FileName,
                fileVersion = module?.FileVersionInfo.FileVersion,
                productVersion = module?.FileVersionInfo.ProductVersion,
                productName = module?.FileVersionInfo.ProductName,
                company = module?.FileVersionInfo.CompanyName,
                is64Bit,
                workingSet = process.WorkingSet64,
                startTime = process.StartTime.ToUniversalTime()
            };
            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static bool IsWow64Process(Process process)
    {
        if (!Environment.Is64BitOperatingSystem) return false;
        try
        {
            NativeMethods.IsWow64Process(process.Handle, out bool isWow64);
            return isWow64;
        }
        catch { return false; }
    }

    [McpServerTool(Name = "wpf_restart_app", Destructive = true), Description("Close and relaunch app with previous settings.")]
    public string RestartApp()
    {
        if (!_session.IsAttached || _session.Application is null)
            throw new McpException("No app attached.");

        _audit.Record("wpf_restart_app");

        try
        {
            var process = Process.GetProcessById(_session.Application.ProcessId);
            var exePath = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                throw new McpException("Cannot determine executable path.");

            _session.Application.Close();
            Thread.Sleep(1000);

            _session.Launch(exePath);
            var status = _session.GetStatus();
            return JsonSerializer.Serialize(status, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "wpf_kill_app", Destructive = true, Idempotent = true), Description("Force-kill attached process. Use with caution.")]
    public string KillApp()
    {
        if (!_session.IsAttached || _session.Application is null)
            throw new McpException("No app attached.");

        _audit.Record("wpf_kill_app");

        try
        {
            _session.Application.Kill();
            _session.Detach();
            return JsonSerializer.Serialize(new { result = "killed" }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static string GetVisualState(FlaUI.Core.AutomationElements.Window window)
    {
        try
        {
            if (window.Patterns.Window.IsSupported)
            {
                var state = window.Patterns.Window.Pattern.WindowVisualState.ValueOrDefault;
                return state.ToString();
            }
        }
        catch { }
        return "Unknown";
    }

    private static string? GetSafeExecutablePath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; }
    }
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWow64Process(IntPtr processHandle, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);
}
