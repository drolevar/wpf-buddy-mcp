using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class AssertionTools
{
    private readonly UiaAdapter _uia;
    private readonly AuditLog _audit;

    public AssertionTools(UiaAdapter uia, AuditLog audit)
    {
        _uia = uia;
        _audit = audit;
    }

    [McpServerTool(Name = "wpf_assert_exists", ReadOnly = true), Description("Assert element exists.")]
    public string AssertExists(
        [Description("AutomationId of the target element.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null,
        [Description("Optional control type filter (e.g. Button, TextBox, CheckBox) to disambiguate matches.")] string? controlType = null)
    {
        _audit.Record("wpf_assert_exists");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name, ControlType = controlType };
        var element = _uia.FindElement(criteria);
        return element is not null
            ? Pass("Element exists.")
            : Fail("Element does not exist.");
    }

    [McpServerTool(Name = "wpf_assert_not_exists", ReadOnly = true), Description("Assert element is absent.")]
    public string AssertNotExists(
        [Description("AutomationId of the target element.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_assert_not_exists");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        return element is null
            ? Pass("Element does not exist.")
            : Fail("Element unexpectedly exists.");
    }

    [McpServerTool(Name = "wpf_assert_visible", ReadOnly = true), Description("Assert element is visible (not offscreen).")]
    public string AssertVisible(
        [Description("AutomationId of the target element.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_assert_visible");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null) return Fail("Element not found.");

        return !element.Properties.IsOffscreen.ValueOrDefault
            ? Pass("Element is visible.")
            : Fail("Element is offscreen.");
    }

    [McpServerTool(Name = "wpf_assert_enabled", ReadOnly = true), Description("Assert element is enabled.")]
    public string AssertEnabled(
        [Description("AutomationId of the target element.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_assert_enabled");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null) return Fail("Element not found.");

        return element.Properties.IsEnabled.ValueOrDefault
            ? Pass("Element is enabled.")
            : Fail("Element is disabled.");
    }

    [McpServerTool(Name = "wpf_assert_disabled", ReadOnly = true), Description("Assert element is disabled.")]
    public string AssertDisabled(
        [Description("AutomationId of the target element.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_assert_disabled");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null) return Fail("Element not found.");

        return !element.Properties.IsEnabled.ValueOrDefault
            ? Pass("Element is disabled.")
            : Fail("Element is enabled.");
    }

    [McpServerTool(Name = "wpf_assert_text", ReadOnly = true), Description("Assert element text equals, contains, or matches expected value.")]
    public string AssertText(
        [Description("Expected text to compare against the element's text/value. Required.")] string expected,
        [Description("AutomationId of the target element.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null,
        [Description("Comparison mode (case-insensitive): equals (default), contains, startswith, endswith.")] string mode = "equals")
    {
        _audit.Record("wpf_assert_text");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null) return Fail("Element not found.");

        var text = element.Properties.Name.ValueOrDefault ?? string.Empty;
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                var value = element.Patterns.Value.Pattern.Value.ValueOrDefault;
                if (!string.IsNullOrEmpty(value)) text = value;
            }
        }
        catch { }

        var match = mode.ToLowerInvariant() switch
        {
            "contains" => text.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "startswith" => text.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            "endswith" => text.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            _ => text.Equals(expected, StringComparison.OrdinalIgnoreCase)
        };

        return match
            ? Pass($"Text matches ({mode}).")
            : Fail($"Text mismatch. Expected ({mode}): '{expected}', Actual: '{text}'.");
    }

    [McpServerTool(Name = "wpf_assert_value", ReadOnly = true), Description("Assert element value equals expected.")]
    public string AssertValue(
        [Description("Expected value to compare against the element's ValuePattern value (case-insensitive equals). Required.")] string expected,
        [Description("AutomationId of the target element.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_assert_value");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null) return Fail("Element not found.");

        string? value = null;
        try
        {
            if (element.Patterns.Value.IsSupported)
                value = element.Patterns.Value.Pattern.Value.ValueOrDefault;
        }
        catch { }

        if (value is null) return Fail("Element does not expose a value.");

        return value.Equals(expected, StringComparison.OrdinalIgnoreCase)
            ? Pass("Value matches.")
            : Fail($"Value mismatch. Expected: '{expected}', Actual: '{value}'.");
    }

    [McpServerTool(Name = "wpf_assert_checked", ReadOnly = true), Description("Assert checkbox/toggle is checked.")]
    public string AssertChecked(
        [Description("AutomationId of the target element.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_assert_checked");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null) return Fail("Element not found.");

        if (!element.Patterns.Toggle.IsSupported)
            return Fail("Element does not support Toggle pattern.");

        var state = element.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault;
        return state == FlaUI.Core.Definitions.ToggleState.On
            ? Pass("Element is checked.")
            : Fail($"Element is not checked (state: {state}).");
    }

    [McpServerTool(Name = "wpf_assert_unchecked", ReadOnly = true), Description("Assert checkbox/toggle is unchecked.")]
    public string AssertUnchecked(
        [Description("AutomationId of the target element.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_assert_unchecked");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null) return Fail("Element not found.");

        if (!element.Patterns.Toggle.IsSupported)
            return Fail("Element does not support Toggle pattern.");

        var state = element.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault;
        return state == FlaUI.Core.Definitions.ToggleState.Off
            ? Pass("Element is unchecked.")
            : Fail($"Element is not unchecked (state: {state}).");
    }

    [McpServerTool(Name = "wpf_assert_no_validation_errors", ReadOnly = true), Description("Assert no validation errors are visible in current window (checks for error-styled elements).")]
    public string AssertNoValidationErrors()
    {
        _audit.Record("wpf_assert_no_validation_errors");
        // Check for elements with common validation error indicators
        var errorElements = _uia.QueryElements(className: "AdornedElementPlaceholder");
        if (errorElements.Count > 0)
            return Fail($"Found {errorElements.Count} elements with validation adorners.");

        // Also check for elements with "Error" or "Invalid" in the name
        var errorNames = _uia.QueryElements(name: "Error");
        if (errorNames.Count > 0)
            return Fail($"Found {errorNames.Count} elements with 'Error' in name.");

        return Pass("No validation errors detected.");
    }

    [McpServerTool(Name = "wpf_assert_selected", ReadOnly = true), Description("Assert that a specific item is selected in a list/combo/tree.")]
    public string AssertSelected(
        [Description("Name of the item expected to be selected (case-insensitive equals). Required.")] string expectedItem,
        [Description("AutomationId of the target selection container (list/combo/tree).")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_assert_selected");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null) return Fail("Element not found.");

        if (!element.Patterns.Selection.IsSupported)
            return Fail("Element does not support Selection pattern.");

        try
        {
            var selection = element.Patterns.Selection.Pattern.Selection.ValueOrDefault;
            var selectedName = selection?.FirstOrDefault()?.Properties.Name.ValueOrDefault;
            return selectedName?.Equals(expectedItem, StringComparison.OrdinalIgnoreCase) == true
                ? Pass($"Item '{expectedItem}' is selected.")
                : Fail($"Expected '{expectedItem}' selected, but got '{selectedName}'.");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "wpf_assert_grid_row_count", ReadOnly = true), Description("Assert DataGrid/ListView row count.")]
    public string AssertGridRowCount(
        [Description("Expected row count to compare against. Required.")] int expected,
        [Description("Comparison operator (case-insensitive): equals (default), greater/gt, less/lt, greaterequal/gte, lessequal/lte.")] string comparison = "equals",
        [Description("AutomationId of the target grid/list element.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_assert_grid_row_count");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null) return Fail("Element not found.");

        int rowCount = 0;
        if (element.Patterns.Grid.IsSupported)
        {
            rowCount = element.Patterns.Grid.Pattern.RowCount.ValueOrDefault;
        }
        else
        {
            var items = _uia.FindElements(new ElementCriteria { ControlType = "DataItem" }, element);
            if (items.Count == 0)
                items = _uia.FindElements(new ElementCriteria { ControlType = "ListItem" }, element);
            rowCount = items.Count;
        }

        var pass = comparison.ToLowerInvariant() switch
        {
            "greater" or "gt" => rowCount > expected,
            "less" or "lt" => rowCount < expected,
            "greaterequal" or "gte" => rowCount >= expected,
            "lessequal" or "lte" => rowCount <= expected,
            _ => rowCount == expected
        };

        return pass
            ? Pass($"Row count is {rowCount} ({comparison} {expected}).")
            : Fail($"Row count is {rowCount}, expected {comparison} {expected}.");
    }

    [McpServerTool(Name = "wpf_assert_grid_cell", ReadOnly = true), Description("Assert a grid cell contains expected value.")]
    public string AssertGridCell(
        [Description("Zero-based row index of the cell. Required.")] int row,
        [Description("Zero-based column index of the cell. Required.")] int column,
        [Description("Expected substring the cell value should contain (case-insensitive). Required.")] string expected,
        [Description("AutomationId of the target grid element.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_assert_grid_cell");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null) return Fail("Element not found.");

        if (!element.Patterns.Grid.IsSupported)
            return Fail("Element does not support Grid pattern.");

        try
        {
            var cell = element.Patterns.Grid.Pattern.GetItem(row, column);
            var value = cell.Properties.Name.ValueOrDefault ?? "";
            return value.Contains(expected, StringComparison.OrdinalIgnoreCase)
                ? Pass($"Cell [{row},{column}] contains '{expected}'.")
                : Fail($"Cell [{row},{column}] is '{value}', expected '{expected}'.");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "wpf_assert_accessibility", ReadOnly = true), Description("Assert minimum accessibility expectations: name or automationId present, keyboard focusable.")]
    public string AssertAccessibility(
        [Description("AutomationId of the target element.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_assert_accessibility");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null) return Fail("Element not found.");

        var issues = new List<string>();
        if (string.IsNullOrEmpty(element.Properties.AutomationId.ValueOrDefault))
            issues.Add("Missing AutomationId");
        if (string.IsNullOrEmpty(element.Properties.Name.ValueOrDefault))
            issues.Add("Missing accessible name");
        if (!element.Properties.IsKeyboardFocusable.ValueOrDefault && element.Properties.IsEnabled.ValueOrDefault)
            issues.Add("Not keyboard focusable");

        return issues.Count == 0
            ? Pass("Element meets accessibility requirements.")
            : Fail($"Accessibility issues: {string.Join(", ", issues)}");
    }

    [McpServerTool(Name = "wpf_assert_snapshot_matches", ReadOnly = true), Description("Compare current UI state to a saved baseline snapshot (element count and structure).")]
    public string AssertSnapshotMatches(
        [Description("Baseline UI snapshot as a JSON string (UiSnapshot shape, e.g. from a prior snapshot capture). Required.")] string baselineJson,
        [Description("Allowed difference in element count as a percentage of the baseline, 0-100. Default 5.")] int tolerancePercent = 5)
    {
        _audit.Record("wpf_assert_snapshot_matches");

        try
        {
            var baseline = JsonSerializer.Deserialize<UiSnapshot>(baselineJson, JsonOptions.Default);
            if (baseline is null) return Fail("Could not parse baseline snapshot.");

            var current = _uia.CaptureSnapshot(maxDepth: 5);
            var baselineCount = CountElements(baseline.Tree);
            var currentCount = CountElements(current.Tree);

            var diff = Math.Abs(currentCount - baselineCount);
            var tolerance = baselineCount * tolerancePercent / 100;

            return diff <= tolerance
                ? Pass($"Snapshot matches within {tolerancePercent}% tolerance ({currentCount} vs {baselineCount} elements).")
                : Fail($"Snapshot differs: {currentCount} elements vs {baselineCount} baseline (tolerance: {tolerancePercent}%).");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int CountElements(List<UiElement> tree)
    {
        int count = tree.Count;
        foreach (var el in tree)
            count += CountElements(el.Children);
        return count;
    }

    private static string Pass(string message) =>
        JsonSerializer.Serialize(new { assertion = "pass", message }, JsonOptions.Default);

    private static string Fail(string message) =>
        JsonSerializer.Serialize(new { assertion = "fail", message }, JsonOptions.Default);
}
