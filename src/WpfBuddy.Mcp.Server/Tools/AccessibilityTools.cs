using System.ComponentModel;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class AccessibilityTools
{
    private readonly UiaAdapter _uia;
    private readonly SessionManager _session;
    private readonly AuditLog _audit;

    public AccessibilityTools(UiaAdapter uia, SessionManager session, AuditLog audit)
    {
        _uia = uia;
        _session = session;
        _audit = audit;
    }

    [McpServerTool(Name = "wpf_accessibility_snapshot", ReadOnly = true), Description("Return accessibility-oriented UI tree with accessibility metadata.")]
    public string AccessibilitySnapshot([Description("Maximum depth of the UI tree to traverse from the active window (number of descendant levels). Optional; defaults to 4.")] int maxDepth = 4)
    {
        _audit.Record("wpf_accessibility_snapshot");
        var window = _session.ActiveWindow;
        if (window is null)
            return Error("No window attached.");

        var issues = new List<object>();
        int elementCount = 0;
        var tree = BuildAccessibilityTree(window, maxDepth, 0, issues, ref elementCount);

        return JsonSerializer.Serialize(new
        {
            windowTitle = window.Title,
            elementCount,
            issueCount = issues.Count,
            issues = issues.Take(50).ToList(),
            tree
        }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_check_missing_names", ReadOnly = true), Description("Find controls missing accessible names.")]
    public string CheckMissingNames()
    {
        _audit.Record("wpf_check_missing_names");
        var allElements = _uia.QueryElements();
        var missing = allElements.Where(e =>
            string.IsNullOrEmpty(e.Name) &&
            IsInteractiveControlType(e.ControlType)).ToList();

        return JsonSerializer.Serialize(new
        {
            totalInteractive = allElements.Count(e => IsInteractiveControlType(e.ControlType)),
            missingNameCount = missing.Count,
            elements = missing.Select(e => new
            {
                e.AutomationId,
                e.ControlType,
                e.ClassName,
                e.Bounds
            }).Take(50).ToList()
        }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_check_missing_help_text", ReadOnly = true), Description("Find controls missing helpful descriptions/HelpText.")]
    public string CheckMissingHelpText()
    {
        _audit.Record("wpf_check_missing_help_text");
        var window = _session.ActiveWindow;
        if (window is null)
            return Error("No window attached.");

        var allElements = window.FindAll(TreeScope.Descendants, FlaUI.Core.Conditions.TrueCondition.Default);
        var missing = new List<object>();

        foreach (var el in allElements)
        {
            var ct = el.Properties.ControlType.ValueOrDefault;
            if (ct == ControlType.Edit || ct == ControlType.ComboBox || ct == ControlType.Button)
            {
                var helpText = el.Properties.HelpText.ValueOrDefault;
                if (string.IsNullOrEmpty(helpText))
                {
                    missing.Add(new
                    {
                        automationId = el.Properties.AutomationId.ValueOrDefault,
                        name = el.Properties.Name.ValueOrDefault,
                        controlType = ct.ToString()
                    });
                }
            }
        }

        return JsonSerializer.Serialize(new { missingHelpTextCount = missing.Count, elements = missing.Take(50).ToList() }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_check_tab_order", ReadOnly = true), Description("Analyze keyboard navigation/tab order.")]
    public string CheckTabOrder()
    {
        _audit.Record("wpf_check_tab_order");
        var window = _session.ActiveWindow;
        if (window is null)
            return Error("No window attached.");

        var allElements = window.FindAll(TreeScope.Descendants, FlaUI.Core.Conditions.TrueCondition.Default);
        var focusable = new List<object>();

        foreach (var el in allElements)
        {
            if (el.Properties.IsKeyboardFocusable.ValueOrDefault && !el.Properties.IsOffscreen.ValueOrDefault)
            {
                focusable.Add(new
                {
                    automationId = el.Properties.AutomationId.ValueOrDefault,
                    name = el.Properties.Name.ValueOrDefault,
                    controlType = el.Properties.ControlType.ValueOrDefault.ToString(),
                    bounds = new { x = el.BoundingRectangle.X, y = el.BoundingRectangle.Y }
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            focusableCount = focusable.Count,
            tabOrder = focusable
        }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_check_keyboard_access", ReadOnly = true), Description("Find controls unreachable by keyboard.")]
    public string CheckKeyboardAccess()
    {
        _audit.Record("wpf_check_keyboard_access");
        var window = _session.ActiveWindow;
        if (window is null)
            return Error("No window attached.");

        var allElements = window.FindAll(TreeScope.Descendants, FlaUI.Core.Conditions.TrueCondition.Default);
        var unreachable = new List<object>();

        foreach (var el in allElements)
        {
            var ct = el.Properties.ControlType.ValueOrDefault;
            if (IsInteractiveControlType(ct.ToString()) &&
                !el.Properties.IsKeyboardFocusable.ValueOrDefault &&
                !el.Properties.IsOffscreen.ValueOrDefault &&
                el.Properties.IsEnabled.ValueOrDefault)
            {
                unreachable.Add(new
                {
                    automationId = el.Properties.AutomationId.ValueOrDefault,
                    name = el.Properties.Name.ValueOrDefault,
                    controlType = ct.ToString()
                });
            }
        }

        return JsonSerializer.Serialize(new { unreachableCount = unreachable.Count, elements = unreachable.Take(50).ToList() }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_check_control_patterns", ReadOnly = true), Description("Ensure controls expose expected UIA patterns.")]
    public string CheckControlPatterns()
    {
        _audit.Record("wpf_check_control_patterns");
        var window = _session.ActiveWindow;
        if (window is null)
            return Error("No window attached.");

        var allElements = window.FindAll(TreeScope.Descendants, FlaUI.Core.Conditions.TrueCondition.Default);
        var issues = new List<object>();

        foreach (var el in allElements)
        {
            var ct = el.Properties.ControlType.ValueOrDefault;
            var patternIssues = new List<string>();

            switch (ct)
            {
                case ControlType.Button:
                    if (!el.Patterns.Invoke.IsSupported)
                        patternIssues.Add("Button should support Invoke pattern");
                    break;
                case ControlType.CheckBox:
                    if (!el.Patterns.Toggle.IsSupported)
                        patternIssues.Add("CheckBox should support Toggle pattern");
                    break;
                case ControlType.ComboBox:
                    if (!el.Patterns.ExpandCollapse.IsSupported && !el.Patterns.Selection.IsSupported)
                        patternIssues.Add("ComboBox should support ExpandCollapse or Selection pattern");
                    break;
                case ControlType.Edit:
                    if (!el.Patterns.Value.IsSupported)
                        patternIssues.Add("Edit should support Value pattern");
                    break;
                case ControlType.Slider:
                    if (!el.Patterns.RangeValue.IsSupported)
                        patternIssues.Add("Slider should support RangeValue pattern");
                    break;
            }

            if (patternIssues.Count > 0)
            {
                issues.Add(new
                {
                    automationId = el.Properties.AutomationId.ValueOrDefault,
                    name = el.Properties.Name.ValueOrDefault,
                    controlType = ct.ToString(),
                    issues = patternIssues
                });
            }
        }

        return JsonSerializer.Serialize(new { issueCount = issues.Count, issues = issues.Take(50).ToList() }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_check_custom_controls", ReadOnly = true), Description("Flag custom controls with poor automation peer exposure.")]
    public string CheckCustomControls()
    {
        _audit.Record("wpf_check_custom_controls");
        var window = _session.ActiveWindow;
        if (window is null)
            return Error("No window attached.");

        var allElements = window.FindAll(TreeScope.Descendants, FlaUI.Core.Conditions.TrueCondition.Default);
        var customControls = new List<object>();

        foreach (var el in allElements)
        {
            var ct = el.Properties.ControlType.ValueOrDefault;
            var className = el.Properties.ClassName.ValueOrDefault ?? "";

            // Custom controls often have ControlType.Custom or non-standard class names
            if (ct == ControlType.Custom ||
                (ct == ControlType.Pane && !className.StartsWith("Window") && !string.IsNullOrEmpty(className)))
            {
                var patterns = new List<string>();
                try
                {
                    if (el.Patterns.Invoke.IsSupported) patterns.Add("Invoke");
                    if (el.Patterns.Value.IsSupported) patterns.Add("Value");
                    if (el.Patterns.Toggle.IsSupported) patterns.Add("Toggle");
                    if (el.Patterns.Selection.IsSupported) patterns.Add("Selection");
                    if (el.Patterns.ExpandCollapse.IsSupported) patterns.Add("ExpandCollapse");
                }
                catch { }

                customControls.Add(new
                {
                    automationId = el.Properties.AutomationId.ValueOrDefault,
                    name = el.Properties.Name.ValueOrDefault,
                    className,
                    controlType = ct.ToString(),
                    patterns,
                    hasAutomationId = !string.IsNullOrEmpty(el.Properties.AutomationId.ValueOrDefault),
                    hasName = !string.IsNullOrEmpty(el.Properties.Name.ValueOrDefault),
                    isKeyboardFocusable = el.Properties.IsKeyboardFocusable.ValueOrDefault
                });
            }
        }

        return JsonSerializer.Serialize(new { customControlCount = customControls.Count, controls = customControls.Take(30).ToList() }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_generate_accessibility_report", ReadOnly = true), Description("Generate comprehensive accessibility/testability report.")]
    public string GenerateAccessibilityReport()
    {
        _audit.Record("wpf_generate_accessibility_report");
        var window = _session.ActiveWindow;
        if (window is null)
            return Error("No window attached.");

        var allElements = window.FindAll(TreeScope.Descendants, FlaUI.Core.Conditions.TrueCondition.Default);
        int totalElements = allElements.Length;
        int interactive = 0;
        int missingNames = 0;
        int missingAutomationIds = 0;
        int notFocusable = 0;
        int offscreen = 0;

        foreach (var el in allElements)
        {
            var ct = el.Properties.ControlType.ValueOrDefault.ToString();
            if (IsInteractiveControlType(ct))
            {
                interactive++;
                if (string.IsNullOrEmpty(el.Properties.Name.ValueOrDefault))
                    missingNames++;
                if (string.IsNullOrEmpty(el.Properties.AutomationId.ValueOrDefault))
                    missingAutomationIds++;
                if (!el.Properties.IsKeyboardFocusable.ValueOrDefault && el.Properties.IsEnabled.ValueOrDefault)
                    notFocusable++;
            }
            if (el.Properties.IsOffscreen.ValueOrDefault)
                offscreen++;
        }

        var score = 100;
        if (interactive > 0)
        {
            score -= (int)(50.0 * missingAutomationIds / interactive);
            score -= (int)(30.0 * missingNames / interactive);
            score -= (int)(20.0 * notFocusable / interactive);
        }
        score = Math.Max(0, Math.Min(100, score));

        var report = new
        {
            windowTitle = window.Title,
            score,
            grade = score >= 90 ? "A" : score >= 75 ? "B" : score >= 60 ? "C" : score >= 40 ? "D" : "F",
            summary = new
            {
                totalElements,
                interactiveElements = interactive,
                offscreenElements = offscreen,
                missingAccessibleNames = missingNames,
                missingAutomationIds,
                notKeyboardFocusable = notFocusable
            },
            recommendations = GenerateRecommendations(missingAutomationIds, missingNames, notFocusable, interactive)
        };

        return JsonSerializer.Serialize(report, JsonOptions.Default);
    }

    private List<object> BuildAccessibilityTree(AutomationElement element, int maxDepth, int currentDepth, List<object> issues, ref int elementCount)
    {
        var result = new List<object>();
        if (currentDepth >= maxDepth) return result;

        var children = element.FindAll(TreeScope.Children, FlaUI.Core.Conditions.TrueCondition.Default);
        foreach (var child in children)
        {
            elementCount++;
            var ct = child.Properties.ControlType.ValueOrDefault;
            var automationId = child.Properties.AutomationId.ValueOrDefault;
            var name = child.Properties.Name.ValueOrDefault;

            // Minimal real check: actionable elements missing both an accessible name and an automation id.
            if (IsInteractiveControlType(ct.ToString())
                && string.IsNullOrEmpty(name)
                && string.IsNullOrEmpty(automationId))
            {
                issues.Add(new
                {
                    controlType = ct.ToString(),
                    issue = "Actionable element has neither an accessible name nor an AutomationId."
                });
            }

            var childNodes = BuildAccessibilityTree(child, maxDepth, currentDepth + 1, issues, ref elementCount);
            var node = new
            {
                automationId,
                name,
                controlType = ct.ToString(),
                isKeyboardFocusable = child.Properties.IsKeyboardFocusable.ValueOrDefault,
                helpText = child.Properties.HelpText.ValueOrDefault,
                accessKey = child.Properties.AccessKey.ValueOrDefault,
                isEnabled = child.Properties.IsEnabled.ValueOrDefault,
                isOffscreen = child.Properties.IsOffscreen.ValueOrDefault,
                children = childNodes
            };
            result.Add(node);
        }
        return result;
    }

    private static List<string> GenerateRecommendations(int missingIds, int missingNames, int notFocusable, int total)
    {
        var recs = new List<string>();
        if (missingIds > 0)
            recs.Add($"Add AutomationProperties.AutomationId to {missingIds} interactive elements.");
        if (missingNames > 0)
            recs.Add($"Add AutomationProperties.Name to {missingNames} interactive elements.");
        if (notFocusable > 0)
            recs.Add($"Review {notFocusable} enabled interactive elements that are not keyboard focusable.");
        if (recs.Count == 0)
            recs.Add("Good accessibility coverage. Consider adding HelpText for complex controls.");
        return recs;
    }

    private static bool IsInteractiveControlType(string? controlType)
    {
        if (string.IsNullOrEmpty(controlType)) return false;
        return controlType is "Button" or "TextBox" or "Edit" or "ComboBox" or "CheckBox"
            or "RadioButton" or "MenuItem" or "Tab" or "TabItem" or "ListItem"
            or "DataItem" or "TreeItem" or "Slider" or "Hyperlink" or "Custom";
    }

    private static string Error(string message) =>
        throw ToolError.Fail(message);
}
