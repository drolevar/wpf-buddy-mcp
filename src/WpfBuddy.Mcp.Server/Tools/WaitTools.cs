using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class WaitTools
{
    private readonly UiaAdapter _uia;
    private readonly SessionManager _session;
    private readonly AuditLog _audit;

    public WaitTools(UiaAdapter uia, SessionManager session, AuditLog audit)
    {
        _uia = uia;
        _session = session;
        _audit = audit;
    }

    [McpServerTool(Name = "wpf_wait_for_element", ReadOnly = true), Description("Wait until element exists (up to timeout ms).")]
    public string WaitForElement(
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000,
        [Description("AutomationId of the target element. Preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null,
        [Description("Optional control type filter, e.g. Button, Edit, Text, CheckBox, ComboBox.")] string? controlType = null)
    {
        _audit.Record("wpf_wait_for_element");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name, ControlType = controlType };
        var result = PollUntil(() => _uia.FindElement(criteria) is not null, timeoutMs);
        return result
            ? Ok("element_found")
            : Error($"Element not found within {timeoutMs}ms.");
    }

    [McpServerTool(Name = "wpf_wait_for_absent", ReadOnly = true), Description("Wait until element disappears (up to timeout ms).")]
    public string WaitForAbsent(
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000,
        [Description("AutomationId of the target element. Preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_wait_for_absent");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var result = PollUntil(() => _uia.FindElement(criteria) is null, timeoutMs);
        return result
            ? Ok("element_absent")
            : Error($"Element still present after {timeoutMs}ms.");
    }

    [McpServerTool(Name = "wpf_wait_for_enabled", ReadOnly = true), Description("Wait until element is enabled (up to timeout ms).")]
    public string WaitForEnabled(
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000,
        [Description("AutomationId of the target element. Preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_wait_for_enabled");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var result = PollUntil(() =>
        {
            var el = _uia.FindElement(criteria);
            return el?.Properties.IsEnabled.ValueOrDefault == true;
        }, timeoutMs);

        return result
            ? Ok("element_enabled")
            : Error($"Element not enabled within {timeoutMs}ms.");
    }

    [McpServerTool(Name = "wpf_wait_for_text", ReadOnly = true), Description("Wait until element text equals or contains expected value.")]
    public string WaitForText(
        [Description("The text to wait for. Required.")] string expectedText,
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000,
        [Description("AutomationId of the target element. Preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null,
        [Description("Matching mode: false (default) requires the text to equal expectedText (case-insensitive); true matches if the text contains expectedText.")] bool contains = false)
    {
        _audit.Record("wpf_wait_for_text");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var result = PollUntil(() =>
        {
            var el = _uia.FindElement(criteria);
            if (el is null) return false;
            var text = el.Properties.Name.ValueOrDefault ?? string.Empty;
            string? value = null;
            try
            {
                if (el.Patterns.Value.IsSupported)
                    value = el.Patterns.Value.Pattern.Value.ValueOrDefault;
            }
            catch { }

            var fullText = value ?? text;
            return contains
                ? fullText.Contains(expectedText, StringComparison.OrdinalIgnoreCase)
                : fullText.Equals(expectedText, StringComparison.OrdinalIgnoreCase);
        }, timeoutMs);

        return result
            ? Ok("text_matched")
            : Error($"Text did not match within {timeoutMs}ms.");
    }

    [McpServerTool(Name = "wpf_wait_for", ReadOnly = true), Description("Generic wait: wait for selector to exist and/or have specific state.")]
    public string WaitFor(
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000,
        [Description("AutomationId of the target element. Preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null,
        [Description("Optional control type filter, e.g. Button, Edit, Text, CheckBox, ComboBox.")] string? controlType = null,
        [Description("Optional state condition: when set, wait until the element's enabled state matches this value (true=enabled, false=disabled). Null skips this check.")] bool? enabled = null,
        [Description("Optional state condition: when set, wait until the element's visibility matches this value (true=visible/on-screen, false=offscreen). Null skips this check.")] bool? visible = null)
    {
        _audit.Record("wpf_wait_for");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name, ControlType = controlType };
        var result = PollUntil(() =>
        {
            var el = _uia.FindElement(criteria);
            if (el is null) return false;
            if (enabled.HasValue && el.Properties.IsEnabled.ValueOrDefault != enabled.Value) return false;
            if (visible.HasValue && el.Properties.IsOffscreen.ValueOrDefault == visible.Value) return false;
            return true;
        }, timeoutMs);

        return result
            ? Ok("condition_met")
            : Error($"Condition not met within {timeoutMs}ms.");
    }

    [McpServerTool(Name = "wpf_wait_until_snapshot_stable", ReadOnly = true), Description("Wait until UI tree stops changing for stabilityMs.")]
    public string WaitUntilSnapshotStable(
        [Description("Duration in milliseconds the UI tree must remain unchanged to be considered stable. Default 500.")] int stabilityMs = 500,
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000)
    {
        _audit.Record("wpf_wait_until_snapshot_stable");
        var sw = Stopwatch.StartNew();
        string? lastSnapshot = null;
        var stableStart = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var snapshot = _uia.CaptureSnapshot(maxDepth: 3);
                var currentJson = UiaAdapter.Fingerprint(snapshot.Tree);

                if (currentJson == lastSnapshot)
                {
                    if (stableStart.ElapsedMilliseconds >= stabilityMs)
                        return Ok("snapshot_stable");
                }
                else
                {
                    lastSnapshot = currentJson;
                    stableStart.Restart();
                }
            }
            catch { }

            Thread.Sleep(150);
        }

        return Error($"Snapshot did not stabilize within {timeoutMs}ms.");
    }

    [McpServerTool(Name = "wpf_wait_for_window", ReadOnly = true), Description("Wait for a window/dialog with title or automation id.")]
    public string WaitForWindow(
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000,
        [Description("Window title to match (case-insensitive substring). Provide either title or automationId.")] string? title = null,
        [Description("AutomationId of the target window; used when title is omitted.")] string? automationId = null)
    {
        _audit.Record("wpf_wait_for_window");
        var result = PollUntil(() =>
        {
            try
            {
                if (!_session.IsAttached || _session.Automation is null || _session.Application is null)
                    return false;

                var windows = _session.Application.GetAllTopLevelWindows(_session.Automation);
                return windows.Any(w =>
                    (!string.IsNullOrEmpty(title) && w.Title?.Contains(title, StringComparison.OrdinalIgnoreCase) == true) ||
                    (!string.IsNullOrEmpty(automationId) && w.Properties.AutomationId.ValueOrDefault == automationId));
            }
            catch { return false; }
        }, timeoutMs);

        return result
            ? Ok("window_found")
            : Error($"Window not found within {timeoutMs}ms.");
    }

    private static bool PollUntil(Func<bool> condition, int timeoutMs, int pollIntervalMs = 200)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (condition()) return true;
            }
            catch { }
            Thread.Sleep(pollIntervalMs);
        }
        return false;
    }

    [McpServerTool(Name = "wpf_wait_for_disabled", ReadOnly = true), Description("Wait until element is disabled (up to timeout ms).")]
    public string WaitForDisabled(
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000,
        [Description("AutomationId of the target element. Preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_wait_for_disabled");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var result = PollUntil(() =>
        {
            var el = _uia.FindElement(criteria);
            return el?.Properties.IsEnabled.ValueOrDefault == false;
        }, timeoutMs);

        return result
            ? Ok("element_disabled")
            : Error($"Element not disabled within {timeoutMs}ms.");
    }

    [McpServerTool(Name = "wpf_wait_for_visible", ReadOnly = true), Description("Wait until element is visible/not offscreen (up to timeout ms).")]
    public string WaitForVisible(
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000,
        [Description("AutomationId of the target element. Preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_wait_for_visible");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var result = PollUntil(() =>
        {
            var el = _uia.FindElement(criteria);
            return el is not null && !el.Properties.IsOffscreen.ValueOrDefault;
        }, timeoutMs);

        return result
            ? Ok("element_visible")
            : Error($"Element not visible within {timeoutMs}ms.");
    }

    [McpServerTool(Name = "wpf_wait_for_hidden", ReadOnly = true), Description("Wait until element is hidden/offscreen (up to timeout ms).")]
    public string WaitForHidden(
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000,
        [Description("AutomationId of the target element. Preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_wait_for_hidden");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var result = PollUntil(() =>
        {
            var el = _uia.FindElement(criteria);
            return el is null || el.Properties.IsOffscreen.ValueOrDefault;
        }, timeoutMs);

        return result
            ? Ok("element_hidden")
            : Error($"Element not hidden within {timeoutMs}ms.");
    }

    [McpServerTool(Name = "wpf_wait_for_value", ReadOnly = true), Description("Wait until element value equals or contains expected.")]
    public string WaitForValue(
        [Description("The value (from the ValuePattern) to wait for. Required.")] string expectedValue,
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000,
        [Description("AutomationId of the target element. Preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null,
        [Description("Matching mode: false (default) requires the value to equal expectedValue (case-insensitive); true matches if the value contains expectedValue.")] bool contains = false)
    {
        _audit.Record("wpf_wait_for_value");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var result = PollUntil(() =>
        {
            var el = _uia.FindElement(criteria);
            if (el is null) return false;
            try
            {
                if (el.Patterns.Value.IsSupported)
                {
                    var val = el.Patterns.Value.Pattern.Value.ValueOrDefault ?? "";
                    return contains
                        ? val.Contains(expectedValue, StringComparison.OrdinalIgnoreCase)
                        : val.Equals(expectedValue, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return false;
        }, timeoutMs);

        return result
            ? Ok("value_matched")
            : Error($"Value did not match within {timeoutMs}ms.");
    }

    [McpServerTool(Name = "wpf_wait_for_selection", ReadOnly = true), Description("Wait until a selection change occurs in a list/combo/tree.")]
    public string WaitForSelection(
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000,
        [Description("AutomationId of the target list/combo/tree element. Preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null,
        [Description("Optional. If set, waits until the selected item's name equals this value. If omitted, waits until the selection changes from its initial value.")] string? expectedItem = null)
    {
        _audit.Record("wpf_wait_for_selection");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };

        string? initialSelection = null;
        var el = _uia.FindElement(criteria);
        if (el is not null && el.Patterns.Selection.IsSupported)
        {
            try
            {
                var sel = el.Patterns.Selection.Pattern.Selection.ValueOrDefault;
                initialSelection = sel?.FirstOrDefault()?.Name;
            }
            catch { }
        }

        var result = PollUntil(() =>
        {
            var element = _uia.FindElement(criteria);
            if (element is null || !element.Patterns.Selection.IsSupported) return false;
            try
            {
                var sel = element.Patterns.Selection.Pattern.Selection.ValueOrDefault;
                var currentSelection = sel?.FirstOrDefault()?.Name;
                if (expectedItem is not null)
                    return currentSelection == expectedItem;
                return currentSelection != initialSelection;
            }
            catch { return false; }
        }, timeoutMs);

        return result
            ? Ok("selection_changed")
            : Error($"Selection did not change within {timeoutMs}ms.");
    }

    [McpServerTool(Name = "wpf_wait_for_dialog", ReadOnly = true), Description("Wait for a modal dialog to appear.")]
    public string WaitForDialog(
        [Description("Maximum time to wait in milliseconds before giving up. Default 10000.")] int timeoutMs = 10000,
        [Description("Optional dialog title to match (case-insensitive substring). If omitted, waits for any Window-type element to appear.")] string? title = null)
    {
        _audit.Record("wpf_wait_for_dialog");
        var result = PollUntil(() =>
        {
            try
            {
                var criteria = new ElementCriteria { ControlType = "Window" };
                var windows = _uia.FindElements(criteria);
                foreach (var w in windows)
                {
                    if (title is not null)
                    {
                        var winTitle = w.Properties.Name.ValueOrDefault;
                        if (winTitle?.Contains(title, StringComparison.OrdinalIgnoreCase) == true)
                            return true;
                    }
                    else
                    {
                        return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }, timeoutMs);

        return result
            ? Ok("dialog_found")
            : Error($"Dialog not found within {timeoutMs}ms.");
    }

    [McpServerTool(Name = "wpf_wait_for_navigation", ReadOnly = true), Description("Wait for view/page/content transition by detecting UI tree change.")]
    public string WaitForNavigation(
        [Description("Maximum time to wait in milliseconds for the UI tree to change. Default 10000.")] int timeoutMs = 10000,
        [Description("Settling delay in milliseconds after a change is detected, to let the new view finish rendering. Default 300.")] int stabilityMs = 300)
    {
        _audit.Record("wpf_wait_for_navigation");
        var sw = Stopwatch.StartNew();

        string? initialSnapshot = null;
        try
        {
            var snap = _uia.CaptureSnapshot(maxDepth: 2);
            initialSnapshot = UiaAdapter.Fingerprint(snap.Tree);
        }
        catch { }

        // Wait for snapshot to differ from initial
        var changed = PollUntil(() =>
        {
            try
            {
                var snap = _uia.CaptureSnapshot(maxDepth: 2);
                var current = UiaAdapter.Fingerprint(snap.Tree);
                return current != initialSnapshot;
            }
            catch { return false; }
        }, timeoutMs);

        if (!changed)
            return Error($"No navigation detected within {timeoutMs}ms.");

        // Wait for stability after change
        Thread.Sleep(stabilityMs);
        return Ok("navigation_detected");
    }

    private static string Ok(string result) =>
        JsonSerializer.Serialize(new { result }, JsonOptions.Default);

    private static string Error(string message) =>
        throw ToolError.Fail(message);
}
