namespace WpfBuddy.Mcp.Server.Services;

/// <summary>
/// Single source of truth for which UIA control types count as "interactive/actionable" when scoring
/// automation/accessibility quality (R2-6). Diagnostics, Accessibility, and Reporting previously each
/// kept their own near-identical list, which drifted (e.g. one included "Custom"); they all delegate
/// here now so the lists can't diverge again.
/// </summary>
public static class ControlTypeCatalog
{
    public static bool IsInteractive(string? controlType)
    {
        if (string.IsNullOrEmpty(controlType)) return false;
        return controlType is "Button" or "TextBox" or "Edit" or "ComboBox" or "CheckBox"
            or "RadioButton" or "MenuItem" or "Tab" or "TabItem" or "ListItem"
            or "DataItem" or "TreeItem" or "Slider" or "Hyperlink";
    }
}
