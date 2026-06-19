using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class SnapshotTools
{
    private readonly SessionManager _session;
    private readonly UiaAdapter _uia;
    private readonly AuditLog _audit;

    public SnapshotTools(SessionManager session, UiaAdapter uia, AuditLog audit)
    {
        _session = session;
        _uia = uia;
        _audit = audit;
    }

    [McpServerTool(Name = "wpf_snapshot", ReadOnly = true), Description("Return compact UI tree for current window.")]
    public string Snapshot([Description("Maximum tree depth to traverse from the window root. Higher values return more nested elements at greater cost. Optional; default 5.")] int maxDepth = 5)
    {
        _audit.Record("wpf_snapshot");
        var snapshot = _uia.CaptureSnapshot(maxDepth: maxDepth);
        return JsonSerializer.Serialize(snapshot, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_snapshot_element", ReadOnly = true), Description("Return subtree for one element by automation id or name.")]
    public string SnapshotElement([Description("AutomationId of the target element; preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null,
        [Description("Maximum subtree depth to traverse below the matched element. Optional; default 3.")] int maxDepth = 3)
    {
        _audit.Record("wpf_snapshot_element", new ElementCriteria { AutomationId = automationId, Name = name });
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null)
            return JsonSerializer.Serialize(new { error = "Element not found." }, JsonOptions.Default);

        var snapshot = _uia.CaptureSnapshot(element, maxDepth);
        return JsonSerializer.Serialize(snapshot, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_query", ReadOnly = true), Description("Find elements by AutomationId, name, control type, or class name. Returns first match.")]
    public string Query([Description("AutomationId to match; preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content to match; used when automationId is omitted.")] string? name = null,
        [Description("UIA ControlType to match, e.g. Button, Edit, ComboBox, ListItem, CheckBox.")] string? controlType = null,
        [Description("WPF/Win32 class name to match, e.g. TextBox, Window.")] string? className = null)
    {
        _audit.Record("wpf_query");
        var elements = _uia.QueryElements(automationId, name, controlType, className);
        var first = elements.FirstOrDefault();
        if (first is null)
            return JsonSerializer.Serialize(new { error = "No matching element found." }, JsonOptions.Default);

        return JsonSerializer.Serialize(first, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_query_all", ReadOnly = true), Description("Find all elements matching criteria.")]
    public string QueryAll([Description("AutomationId to match; preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content to match; used when automationId is omitted.")] string? name = null,
        [Description("UIA ControlType to match, e.g. Button, Edit, ComboBox, ListItem, CheckBox.")] string? controlType = null,
        [Description("WPF/Win32 class name to match, e.g. TextBox, Window.")] string? className = null)
    {
        _audit.Record("wpf_query_all");
        var elements = _uia.QueryElements(automationId, name, controlType, className);
        return JsonSerializer.Serialize(elements, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_element", ReadOnly = true), Description("Resolve a selector to one element and return its properties.")]
    public string GetElement([Description("AutomationId of the target element; preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null,
        [Description("UIA ControlType to disambiguate the match, e.g. Button, Edit, ComboBox.")] string? controlType = null)
    {
        _audit.Record("wpf_get_element");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name, ControlType = controlType };
        var element = _uia.FindElement(criteria);
        if (element is null)
            return JsonSerializer.Serialize(new { error = "Element not found." }, JsonOptions.Default);

        var mapped = _uia.MapElement(element);
        return JsonSerializer.Serialize(mapped, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_properties", ReadOnly = true), Description("Get full UIA properties for an element.")]
    public string GetProperties([Description("AutomationId of the target element; preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_get_properties");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null)
            return JsonSerializer.Serialize(new { error = "Element not found." }, JsonOptions.Default);

        var props = new
        {
            automationId = element.Properties.AutomationId.ValueOrDefault,
            name = element.Properties.Name.ValueOrDefault,
            controlType = element.Properties.ControlType.ValueOrDefault.ToString(),
            className = element.Properties.ClassName.ValueOrDefault,
            isEnabled = element.Properties.IsEnabled.ValueOrDefault,
            isOffscreen = element.Properties.IsOffscreen.ValueOrDefault,
            isKeyboardFocusable = element.Properties.IsKeyboardFocusable.ValueOrDefault,
            hasKeyboardFocus = element.Properties.HasKeyboardFocus.ValueOrDefault,
            itemType = element.Properties.ItemType.ValueOrDefault,
            helpText = element.Properties.HelpText.ValueOrDefault,
            acceleratorKey = element.Properties.AcceleratorKey.ValueOrDefault,
            accessKey = element.Properties.AccessKey.ValueOrDefault,
            bounds = element.BoundingRectangle
        };

        return JsonSerializer.Serialize(props, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_patterns", ReadOnly = true), Description("List supported UIA patterns for an element.")]
    public string GetPatterns([Description("AutomationId of the target element; preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_get_patterns");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null)
            return JsonSerializer.Serialize(new { error = "Element not found." }, JsonOptions.Default);

        var mapped = _uia.MapElement(element);
        return JsonSerializer.Serialize(new { patterns = mapped.Patterns }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_text", ReadOnly = true), Description("Get visible text from element.")]
    public string GetText([Description("AutomationId of the target element; preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_get_text");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null)
            return JsonSerializer.Serialize(new { error = "Element not found." }, JsonOptions.Default);

        var text = element.Properties.Name.ValueOrDefault;
        string? value = null;
        try
        {
            if (element.Patterns.Value.IsSupported)
                value = element.Patterns.Value.Pattern.Value.ValueOrDefault;
        }
        catch { }

        return JsonSerializer.Serialize(new { text, value }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_value", ReadOnly = true), Description("Get value from TextBox, ComboBox, Slider, DatePicker, etc.")]
    public string GetValue([Description("AutomationId of the target element; preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_get_value");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null)
            return JsonSerializer.Serialize(new { error = "Element not found." }, JsonOptions.Default);

        string? value = null;
        try
        {
            if (element.Patterns.Value.IsSupported)
                value = element.Patterns.Value.Pattern.Value.ValueOrDefault;
            else if (element.Patterns.RangeValue.IsSupported)
                value = element.Patterns.RangeValue.Pattern.Value.ValueOrDefault.ToString();
        }
        catch { }

        return JsonSerializer.Serialize(new { value }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_state", ReadOnly = true), Description("Get element state: enabled, visible, focused, selected, expanded, checked, read-only, offscreen.")]
    public string GetState([Description("AutomationId of the target element; preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_get_state");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null)
            return JsonSerializer.Serialize(new { error = "Element not found." }, JsonOptions.Default);

        bool? isChecked = null;
        string? expandState = null;
        bool? isSelected = null;

        try
        {
            if (element.Patterns.Toggle.IsSupported)
                isChecked = element.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault == FlaUI.Core.Definitions.ToggleState.On;
            if (element.Patterns.ExpandCollapse.IsSupported)
                expandState = element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.ValueOrDefault.ToString();
            if (element.Patterns.SelectionItem.IsSupported)
                isSelected = element.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault;
        }
        catch { }

        var state = new
        {
            isEnabled = element.Properties.IsEnabled.ValueOrDefault,
            isOffscreen = element.Properties.IsOffscreen.ValueOrDefault,
            hasFocus = element.Properties.HasKeyboardFocus.ValueOrDefault,
            isKeyboardFocusable = element.Properties.IsKeyboardFocusable.ValueOrDefault,
            isChecked,
            expandState,
            isSelected
        };

        return JsonSerializer.Serialize(state, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_bounds", ReadOnly = true), Description("Get screen/window-relative bounding box for an element.")]
    public string GetBounds([Description("AutomationId of the target element; preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_get_bounds");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null)
            return JsonSerializer.Serialize(new { error = "Element not found." }, JsonOptions.Default);

        var bounds = element.BoundingRectangle;
        var windowBounds = _session.ActiveWindow?.BoundingRectangle;
        var result = new
        {
            screen = new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height },
            windowRelative = windowBounds is not null ? new
            {
                x = bounds.X - windowBounds.Value.X,
                y = bounds.Y - windowBounds.Value.Y,
                width = bounds.Width,
                height = bounds.Height
            } : null
        };

        return JsonSerializer.Serialize(result, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_selection", ReadOnly = true), Description("Get selected item(s) from list/grid/tree/combo.")]
    public string GetSelection([Description("AutomationId of the target element; preferred selector when available.")] string? automationId = null,
        [Description("Element Name/content; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_get_selection");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null)
            return JsonSerializer.Serialize(new { error = "Element not found." }, JsonOptions.Default);

        if (!element.Patterns.Selection.IsSupported)
            return JsonSerializer.Serialize(new { error = "Element does not support Selection pattern." }, JsonOptions.Default);

        try
        {
            var selection = element.Patterns.Selection.Pattern.Selection.ValueOrDefault;
            var items = selection?.Select(s => new
            {
                name = s.Properties.Name.ValueOrDefault,
                automationId = s.Properties.AutomationId.ValueOrDefault,
                controlType = s.Properties.ControlType.ValueOrDefault.ToString()
            }).ToList();

            return JsonSerializer.Serialize(new { selectedItems = items ?? (object)new List<object>() }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_diff_snapshot", ReadOnly = true), Description("Compare two snapshots and report added/removed/changed elements.")]
    public string DiffSnapshot([Description("Required. JSON of the earlier UI snapshot (as returned by wpf_snapshot) to diff from.")] string beforeJson,
        [Description("Required. JSON of the later UI snapshot (as returned by wpf_snapshot) to diff against.")] string afterJson)
    {
        _audit.Record("wpf_diff_snapshot");

        try
        {
            var before = JsonSerializer.Deserialize<UiSnapshot>(beforeJson, JsonOptions.Default);
            var after = JsonSerializer.Deserialize<UiSnapshot>(afterJson, JsonOptions.Default);
            if (before is null || after is null)
                return JsonSerializer.Serialize(new { error = "Could not parse snapshots." }, JsonOptions.Default);

            var beforeElements = FlattenElements(before.Tree);
            var afterElements = FlattenElements(after.Tree);

            var beforeIds = beforeElements.Select(e => e.AutomationId ?? e.Id).ToHashSet();
            var afterIds = afterElements.Select(e => e.AutomationId ?? e.Id).ToHashSet();

            var added = afterIds.Except(beforeIds).ToList();
            var removed = beforeIds.Except(afterIds).ToList();
            var common = beforeIds.Intersect(afterIds).ToList();

            var changed = new List<object>();
            foreach (var id in common)
            {
                var b = beforeElements.First(e => (e.AutomationId ?? e.Id) == id);
                var a = afterElements.First(e => (e.AutomationId ?? e.Id) == id);
                if (b.IsEnabled != a.IsEnabled || b.Value != a.Value || b.Name != a.Name)
                {
                    changed.Add(new { id, before = new { b.IsEnabled, b.Value, b.Name }, after = new { a.IsEnabled, a.Value, a.Name } });
                }
            }

            return JsonSerializer.Serialize(new { added, removed, changed, addedCount = added.Count, removedCount = removed.Count, changedCount = changed.Count }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_watch_ui_changes", ReadOnly = true), Description("Monitor UI tree changes for a short interval and report differences.")]
    public string WatchUiChanges([Description("Total time to watch for changes, in milliseconds. Optional; default 3000.")] int durationMs = 3000,
        [Description("Interval between successive snapshot polls, in milliseconds. Optional; default 500.")] int pollIntervalMs = 500)
    {
        _audit.Record("wpf_watch_ui_changes");

        var changes = new List<object>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? lastSnapshot = null;
        int iteration = 0;

        while (sw.ElapsedMilliseconds < durationMs)
        {
            try
            {
                var snapshot = _uia.CaptureSnapshot(maxDepth: 3);
                var currentJson = JsonSerializer.Serialize(snapshot.Tree, JsonOptions.Default);

                if (lastSnapshot is not null && currentJson != lastSnapshot)
                {
                    changes.Add(new { timestampMs = sw.ElapsedMilliseconds, iteration, changeDetected = true });
                }

                lastSnapshot = currentJson;
                iteration++;
            }
            catch { }

            Thread.Sleep(pollIntervalMs);
        }

        return JsonSerializer.Serialize(new { durationMs, totalIterations = iteration, changesDetected = changes.Count, changes }, JsonOptions.Default);
    }

    private static List<UiElement> FlattenElements(List<UiElement> tree)
    {
        var result = new List<UiElement>();
        foreach (var el in tree)
        {
            result.Add(el);
            if (el.Children.Count > 0)
                result.AddRange(FlattenElements(el.Children));
        }
        return result;
    }
}
