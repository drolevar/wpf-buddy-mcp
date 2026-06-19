using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class ClipboardTools
{
    private readonly AuditLog _audit;

    public ClipboardTools(AuditLog audit)
    {
        _audit = audit;
    }

    [McpServerTool(Name = "wpf_get_clipboard", ReadOnly = true), Description("Get current clipboard text content.")]
    public string GetClipboard()
    {
        _audit.Record("wpf_get_clipboard");
        try
        {
            string? text = null;
            Exception? threadEx = null;
            var thread = new Thread(() =>
            {
                try { text = System.Windows.Forms.Clipboard.GetText(); }
                catch (Exception ex) { threadEx = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (threadEx != null)
                return JsonSerializer.Serialize(new { error = threadEx.Message }, JsonOptions.Default);
            const int maxLen = 100000;
            var fullText = text ?? "";
            var truncated = fullText.Length > maxLen;
            var returnText = truncated ? fullText.Substring(0, maxLen) : fullText;
            return JsonSerializer.Serialize(new { text = returnText, hasContent = !string.IsNullOrEmpty(fullText), truncated, fullLength = fullText.Length }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_set_clipboard", Destructive = true, Idempotent = true), Description("Set clipboard text content.")]
    public string SetClipboard([Description("Text to write to the system clipboard; overwrites any existing clipboard content.")] string text)
    {
        _audit.Record("wpf_set_clipboard");
        try
        {
            Exception? threadEx = null;
            var thread = new Thread(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(text))
                        System.Windows.Forms.Clipboard.Clear();
                    else
                        System.Windows.Forms.Clipboard.SetText(text);
                }
                catch (Exception ex) { threadEx = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (threadEx != null)
                return JsonSerializer.Serialize(new { error = threadEx.Message }, JsonOptions.Default);
            return JsonSerializer.Serialize(new { result = "clipboard_set", length = text?.Length ?? 0 }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_clear_clipboard", Destructive = true, Idempotent = true), Description("Clear clipboard contents.")]
    public string ClearClipboard()
    {
        _audit.Record("wpf_clear_clipboard");
        try
        {
            Exception? threadEx = null;
            var thread = new Thread(() =>
            {
                try { System.Windows.Forms.Clipboard.Clear(); }
                catch (Exception ex) { threadEx = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (threadEx != null)
                return JsonSerializer.Serialize(new { error = threadEx.Message }, JsonOptions.Default);
            return JsonSerializer.Serialize(new { result = "clipboard_cleared" }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_get_current_culture", ReadOnly = true), Description("Get current thread culture info.")]
    public string GetCurrentCulture()
    {
        _audit.Record("wpf_get_current_culture");
        var culture = Thread.CurrentThread.CurrentCulture;
        var uiCulture = Thread.CurrentThread.CurrentUICulture;
        return JsonSerializer.Serialize(new
        {
            culture = culture.Name,
            displayName = culture.DisplayName,
            uiCulture = uiCulture.Name,
            uiDisplayName = uiCulture.DisplayName
        }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_theme", ReadOnly = true), Description("Detect current Windows theme (dark/light) from registry.")]
    public string GetTheme()
    {
        _audit.Record("wpf_get_theme");
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var appsUseLightTheme = key?.GetValue("AppsUseLightTheme");
            var systemUsesLightTheme = key?.GetValue("SystemUsesLightTheme");

            return JsonSerializer.Serialize(new
            {
                appTheme = appsUseLightTheme == null ? "unknown" : (appsUseLightTheme.Equals(0) ? "dark" : "light"),
                systemTheme = systemUsesLightTheme == null ? "unknown" : (systemUsesLightTheme.Equals(0) ? "dark" : "light")
            }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_get_screen_info", ReadOnly = true), Description("Get screen resolution and DPI info.")]
    public string GetScreenInfo()
    {
        _audit.Record("wpf_get_screen_info");
        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            return JsonSerializer.Serialize(new
            {
                primaryScreen = new
                {
                    width = screen?.Bounds.Width,
                    height = screen?.Bounds.Height,
                    workingAreaWidth = screen?.WorkingArea.Width,
                    workingAreaHeight = screen?.WorkingArea.Height
                },
                screenCount = System.Windows.Forms.Screen.AllScreens.Length
            }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }
}
