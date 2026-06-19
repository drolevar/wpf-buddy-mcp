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
            var thread = new Thread(() => text = System.Windows.Forms.Clipboard.GetText());
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return JsonSerializer.Serialize(new { text = text ?? "", hasContent = !string.IsNullOrEmpty(text) }, JsonOptions.Default);
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
            var thread = new Thread(() => System.Windows.Forms.Clipboard.SetText(text));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return JsonSerializer.Serialize(new { result = "clipboard_set", length = text.Length }, JsonOptions.Default);
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
            var thread = new Thread(() => System.Windows.Forms.Clipboard.Clear());
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
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
                appTheme = appsUseLightTheme?.Equals(0) == true ? "dark" : "light",
                systemTheme = systemUsesLightTheme?.Equals(0) == true ? "dark" : "light"
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
