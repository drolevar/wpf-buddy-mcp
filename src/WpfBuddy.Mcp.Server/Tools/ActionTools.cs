using System.ComponentModel;
using System.Text.Json;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class ActionTools
{
    private readonly UiaAdapter _uia;
    private readonly AuditLog _audit;
    private readonly RecordingService _recording;

    public ActionTools(UiaAdapter uia, AuditLog audit, RecordingService recording)
    {
        _uia = uia;
        _audit = audit;
        _recording = recording;
    }

    [McpServerTool(Name = "wpf_invoke", Destructive = false), Description("Invoke Button, MenuItem, Hyperlink through UIA InvokePattern.")]
    public string Invoke([Description("AutomationId of the target element.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_invoke", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (!element.Patterns.Invoke.IsSupported)
            return Error("Element does not support Invoke pattern.");

        element.Patterns.Invoke.Pattern.Invoke();
        RecordAction("invoke", criteria);
        return Ok("invoked");
    }

    [McpServerTool(Name = "wpf_click", Destructive = false), Description("Click element using pattern if available, coordinates as fallback.")]
    public string Click([Description("AutomationId of the target element.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_click", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
        }
        else
        {
            element.Click();
        }

        RecordAction("click", criteria);
        return Ok("clicked");
    }

    [McpServerTool(Name = "wpf_set_value", Destructive = true, Idempotent = true), Description("Set text/value via ValuePattern.")]
    public string SetValue([Description("New value to write into the element via ValuePattern; overwrites existing content.")] string value, [Description("AutomationId of the target element.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_set_value", criteria, new() { ["value"] = "***" });

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (!element.Patterns.Value.IsSupported)
            return Error("Element does not support Value pattern.");

        element.Patterns.Value.Pattern.SetValue(value);
        RecordAction("set_value", criteria, value);
        return Ok("value_set");
    }

    [McpServerTool(Name = "wpf_clear_value", Destructive = true, Idempotent = true), Description("Clear text/value from element.")]
    public string ClearValue([Description("AutomationId of the target element.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_clear_value", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (element.Patterns.Value.IsSupported)
        {
            element.Patterns.Value.Pattern.SetValue(string.Empty);
        }

        RecordAction("clear_value", criteria);
        return Ok("cleared");
    }

    [McpServerTool(Name = "wpf_type_text", Destructive = true), Description("Type text into focused or selected element via keyboard input.")]
    public string TypeText([Description("Text to type via simulated keyboard input at the current caret/focus.")] string text, [Description("AutomationId of the element to focus before typing; if omitted, types into the currently focused element.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_type_text", criteria, new() { ["text"] = "***" });

        if (!string.IsNullOrEmpty(automationId) || !string.IsNullOrEmpty(name))
        {
            var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
            if (element is null)
                return Error("Element not found.");
            element.Focus();
        }

        Keyboard.Type(text);
        RecordAction("type_text", criteria, text);
        return Ok("typed");
    }

    [McpServerTool(Name = "wpf_send_keys", Destructive = true), Description("Send keyboard shortcuts (e.g., Ctrl+S, Enter, Tab).")]
    public string SendKeys([Description("Key combination using '+' to join modifiers and a key, e.g. 'Ctrl+S', 'Ctrl+Shift+P'. Modifiers: ctrl/control, alt, shift. Keys: enter/return, tab, escape/esc, delete/del, backspace, space, home, end, up, down, left, right, F1-F12, or a single letter/digit.")] string keys)
    {
        _audit.Record("wpf_send_keys", parameters: new() { ["keys"] = keys });

        // Parse common key names
        var keyParts = keys.Split('+').Select(k => k.Trim().ToLowerInvariant()).ToArray();
        var modifiers = new List<VirtualKeyShort>();
        VirtualKeyShort? mainKey = null;

        foreach (var part in keyParts)
        {
            switch (part)
            {
                case "ctrl" or "control": modifiers.Add(VirtualKeyShort.CONTROL); break;
                case "alt": modifiers.Add(VirtualKeyShort.ALT); break;
                case "shift": modifiers.Add(VirtualKeyShort.SHIFT); break;
                case "enter" or "return": mainKey = VirtualKeyShort.ENTER; break;
                case "tab": mainKey = VirtualKeyShort.TAB; break;
                case "escape" or "esc": mainKey = VirtualKeyShort.ESCAPE; break;
                case "delete" or "del": mainKey = VirtualKeyShort.DELETE; break;
                case "backspace": mainKey = VirtualKeyShort.BACK; break;
                case "space": mainKey = VirtualKeyShort.SPACE; break;
                case "home": mainKey = VirtualKeyShort.HOME; break;
                case "end": mainKey = VirtualKeyShort.END; break;
                case "up": mainKey = VirtualKeyShort.UP; break;
                case "down": mainKey = VirtualKeyShort.DOWN; break;
                case "left": mainKey = VirtualKeyShort.LEFT; break;
                case "right": mainKey = VirtualKeyShort.RIGHT; break;
                default:
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                    {
                        mainKey = (VirtualKeyShort)char.ToUpperInvariant(part[0]);
                    }
                    else if (part.StartsWith("f") && int.TryParse(part[1..], out var fNum) && fNum >= 1 && fNum <= 12)
                    {
                        mainKey = (VirtualKeyShort)((int)VirtualKeyShort.F1 + fNum - 1);
                    }
                    break;
            }
        }

        if (mainKey is null)
            return Error("Could not parse key combination.");

        foreach (var mod in modifiers) Keyboard.Press(mod);
        Keyboard.Press(mainKey.Value);
        Keyboard.Release(mainKey.Value);
        foreach (var mod in modifiers) Keyboard.Release(mod);

        return Ok("keys_sent");
    }

    [McpServerTool(Name = "wpf_focus", Destructive = false, Idempotent = true), Description("Move focus to element.")]
    public string Focus([Description("AutomationId of the target element.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_focus", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        element.Focus();
        return Ok("focused");
    }

    [McpServerTool(Name = "wpf_select", Destructive = false, Idempotent = true), Description("Select list/grid/tree/combo item by automation id or name.")]
    public string Select([Description("AutomationId of the target item to select.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_select", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (!element.Patterns.SelectionItem.IsSupported)
            return Error("Element does not support SelectionItem pattern.");

        element.Patterns.SelectionItem.Pattern.Select();
        RecordAction("select", criteria);
        return Ok("selected");
    }

    [McpServerTool(Name = "wpf_select_by_text", Destructive = false, Idempotent = true), Description("Select item in a list/combo by visible text.")]
    public string SelectByText([Description("Visible text of the item to select (matched against element Name).")] string text, [Description("AutomationId of the container (list/combo) to scope the search; optional.")] string? parentAutomationId = null, [Description("Name of the container element; used when parentAutomationId is omitted.")] string? parentName = null)
    {
        _audit.Record("wpf_select_by_text", parameters: new() { ["text"] = text });

        FlaUI.Core.AutomationElements.AutomationElement? parent = null;
        if (!string.IsNullOrEmpty(parentAutomationId) || !string.IsNullOrEmpty(parentName))
        {
            parent = _uia.FindElement(new ElementCriteria { AutomationId = parentAutomationId, Name = parentName });
        }

        var criteria = new ElementCriteria { Name = text };
        var element = _uia.FindElement(criteria, parent);
        if (element is null)
            return Error($"Item with text '{text}' not found.");

        if (element.Patterns.SelectionItem.IsSupported)
        {
            element.Patterns.SelectionItem.Pattern.Select();
        }
        else
        {
            element.Click();
        }

        RecordAction("select", new ElementCriteria { Name = text });
        return Ok("selected");
    }

    [McpServerTool(Name = "wpf_toggle", Destructive = false), Description("Toggle checkbox, toggle button, or expander.")]
    public string Toggle([Description("AutomationId of the target element.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_toggle", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (!element.Patterns.Toggle.IsSupported)
            return Error("Element does not support Toggle pattern.");

        element.Patterns.Toggle.Pattern.Toggle();
        RecordAction("toggle", criteria);
        return Ok("toggled");
    }

    [McpServerTool(Name = "wpf_check", Destructive = false, Idempotent = true), Description("Ensure checkbox is checked.")]
    public string Check([Description("AutomationId of the target checkbox.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_check", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (!element.Patterns.Toggle.IsSupported)
            return Error("Element does not support Toggle pattern.");

        // Loop to handle 3-state checkboxes (Off → On → Indeterminate → Off)
        for (int i = 0; i < 3; i++)
        {
            var state = element.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault;
            if (state == FlaUI.Core.Definitions.ToggleState.On)
                break;
            element.Patterns.Toggle.Pattern.Toggle();
        }

        return Ok("checked");
    }

    [McpServerTool(Name = "wpf_uncheck", Destructive = false, Idempotent = true), Description("Ensure checkbox is unchecked.")]
    public string Uncheck([Description("AutomationId of the target checkbox.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_uncheck", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (!element.Patterns.Toggle.IsSupported)
            return Error("Element does not support Toggle pattern.");

        // Loop to handle 3-state checkboxes (Off → On → Indeterminate → Off)
        for (int i = 0; i < 3; i++)
        {
            var state = element.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault;
            if (state == FlaUI.Core.Definitions.ToggleState.Off)
                break;
            element.Patterns.Toggle.Pattern.Toggle();
        }

        return Ok("unchecked");
    }

    [McpServerTool(Name = "wpf_expand", Destructive = false, Idempotent = true), Description("Expand combo/tree/expander/menu.")]
    public string Expand([Description("AutomationId of the target element.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_expand", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (!element.Patterns.ExpandCollapse.IsSupported)
            return Error("Element does not support ExpandCollapse pattern.");

        element.Patterns.ExpandCollapse.Pattern.Expand();
        RecordAction("expand", criteria);
        return Ok("expanded");
    }

    [McpServerTool(Name = "wpf_collapse", Destructive = false, Idempotent = true), Description("Collapse combo/tree/expander/menu.")]
    public string Collapse([Description("AutomationId of the target element.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_collapse", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (!element.Patterns.ExpandCollapse.IsSupported)
            return Error("Element does not support ExpandCollapse pattern.");

        element.Patterns.ExpandCollapse.Pattern.Collapse();
        RecordAction("collapse", criteria);
        return Ok("collapsed");
    }

    [McpServerTool(Name = "wpf_scroll_into_view", Destructive = false, Idempotent = true), Description("Scroll element into view.")]
    public string ScrollIntoView([Description("AutomationId of the target element to bring into view.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_scroll_into_view", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (element.Patterns.ScrollItem.IsSupported)
        {
            element.Patterns.ScrollItem.Pattern.ScrollIntoView();
            return Ok("scrolled_into_view");
        }

        return Error("Element does not support ScrollItem pattern.");
    }

    [McpServerTool(Name = "wpf_double_click", Destructive = false), Description("Double-click element.")]
    public string DoubleClick([Description("AutomationId of the target element.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_double_click", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        element.DoubleClick();
        RecordAction("double_click", criteria);
        return Ok("double_clicked");
    }

    [McpServerTool(Name = "wpf_right_click", Destructive = false), Description("Right-click element to open context menu.")]
    public string RightClick([Description("AutomationId of the target element.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_right_click", criteria);

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        element.RightClick();
        RecordAction("right_click", criteria);
        return Ok("right_clicked");
    }

    [McpServerTool(Name = "wpf_select_by_index", Destructive = false, Idempotent = true), Description("Select item by index in a list/combo. Marked as brittle.")]
    public string SelectByIndex([Description("Zero-based index of the item to select among the container's ListItem/TreeItem/DataItem children.")] int index, [Description("AutomationId of the container (list/combo) to scope the search; optional.")] string? parentAutomationId = null, [Description("Name of the container element; used when parentAutomationId is omitted.")] string? parentName = null)
    {
        _audit.Record("wpf_select_by_index", parameters: new() { ["index"] = index });

        FlaUI.Core.AutomationElements.AutomationElement? parent = null;
        if (!string.IsNullOrEmpty(parentAutomationId) || !string.IsNullOrEmpty(parentName))
        {
            parent = _uia.FindElement(new ElementCriteria { AutomationId = parentAutomationId, Name = parentName });
            if (parent is null)
                return Error("Parent element not found.");
        }

        var items = _uia.FindElements(new ElementCriteria { ControlType = "ListItem" }, parent);
        if (items.Count == 0)
            items = _uia.FindElements(new ElementCriteria { ControlType = "TreeItem" }, parent);
        if (items.Count == 0)
            items = _uia.FindElements(new ElementCriteria { ControlType = "DataItem" }, parent);

        if (index < 0 || index >= items.Count)
            return Error($"Index {index} out of range. Found {items.Count} items.");

        var target = items[index];
        if (target.Patterns.SelectionItem.IsSupported)
            target.Patterns.SelectionItem.Pattern.Select();
        else
            target.Click();

        return Ok($"selected_index_{index}");
    }

    [McpServerTool(Name = "wpf_scroll", Destructive = false), Description("Scroll container by direction and amount.")]
    public string Scroll([Description("Scroll direction; one of: up, down, left, right.")] string direction, [Description("Scroll amount as a multiplier; defaults to 1.0 (one small increment).")] double amount = 1.0, [Description("AutomationId of the scrollable container.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_scroll", criteria, new() { ["direction"] = direction, ["amount"] = amount });

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (!element.Patterns.Scroll.IsSupported)
            return Error("Element does not support Scroll pattern.");

        var scrollPattern = element.Patterns.Scroll.Pattern;
        switch (direction.ToLowerInvariant())
        {
            case "up":
                scrollPattern.Scroll(FlaUI.Core.Definitions.ScrollAmount.NoAmount, FlaUI.Core.Definitions.ScrollAmount.SmallDecrement);
                break;
            case "down":
                scrollPattern.Scroll(FlaUI.Core.Definitions.ScrollAmount.NoAmount, FlaUI.Core.Definitions.ScrollAmount.SmallIncrement);
                break;
            case "left":
                scrollPattern.Scroll(FlaUI.Core.Definitions.ScrollAmount.SmallDecrement, FlaUI.Core.Definitions.ScrollAmount.NoAmount);
                break;
            case "right":
                scrollPattern.Scroll(FlaUI.Core.Definitions.ScrollAmount.SmallIncrement, FlaUI.Core.Definitions.ScrollAmount.NoAmount);
                break;
            default:
                return Error($"Unknown direction: {direction}. Use up, down, left, right.");
        }

        return Ok("scrolled");
    }

    [McpServerTool(Name = "wpf_open_menu_path", Destructive = false), Description("Open menu path such as 'File > Export > PDF'.")]
    public string OpenMenuPath([Description("Menu path with levels separated by '>', ' > ', or ' → ', e.g. 'File > Export > PDF'. Each level is matched by MenuItem Name, then by AutomationId.")] string menuPath)
    {
        _audit.Record("wpf_open_menu_path", parameters: new() { ["path"] = menuPath });

        var parts = menuPath.Split(new[] { ">", " > ", " → " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return Error("Menu path is empty.");

        foreach (var part in parts)
        {
            var criteria = new ElementCriteria { Name = part, ControlType = "MenuItem" };
            var menuItem = _uia.FindElement(criteria);
            if (menuItem is null)
            {
                // Try by automation id
                criteria = new ElementCriteria { AutomationId = part };
                menuItem = _uia.FindElement(criteria);
            }
            if (menuItem is null)
                return Error($"Menu item '{part}' not found.");

            if (menuItem.Patterns.ExpandCollapse.IsSupported)
                menuItem.Patterns.ExpandCollapse.Pattern.Expand();
            else if (menuItem.Patterns.Invoke.IsSupported)
                menuItem.Patterns.Invoke.Pattern.Invoke();
            else
                menuItem.Click();

            Thread.Sleep(100);
        }

        RecordAction("open_menu_path", null, menuPath);
        return Ok("menu_opened");
    }

    [McpServerTool(Name = "wpf_open_context_menu_item", Destructive = false), Description("Right-click target and invoke context menu item.")]
    public string OpenContextMenuItem([Description("Visible Name of the context-menu item to invoke after right-clicking the target.")] string menuItemName, [Description("AutomationId of the element to right-click.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_open_context_menu_item", criteria, new() { ["menuItem"] = menuItemName });

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        element.RightClick();
        Thread.Sleep(200);

        // Find the context menu item
        var menuCriteria = new ElementCriteria { Name = menuItemName, ControlType = "MenuItem" };
        var menuItem = _uia.FindElement(menuCriteria);
        if (menuItem is null)
            return Error($"Context menu item '{menuItemName}' not found.");

        if (menuItem.Patterns.Invoke.IsSupported)
            menuItem.Patterns.Invoke.Pattern.Invoke();
        else
            menuItem.Click();

        return Ok("context_menu_item_invoked");
    }

    [McpServerTool(Name = "wpf_drag_drop", Destructive = true), Description("Drag source element to target element.")]
    public string DragDrop([Description("AutomationId of the element to drag from (required).")] string sourceAutomationId, [Description("AutomationId of the element to drop onto (required).")] string targetAutomationId)
    {
        _audit.Record("wpf_drag_drop", parameters: new() { ["source"] = sourceAutomationId, ["target"] = targetAutomationId });

        var source = _uia.FindElement(new ElementCriteria { AutomationId = sourceAutomationId });
        if (source is null)
            return Error("Source element not found.");

        var target = _uia.FindElement(new ElementCriteria { AutomationId = targetAutomationId });
        if (target is null)
            return Error("Target element not found.");

        var srcBounds = source.BoundingRectangle;
        var tgtBounds = target.BoundingRectangle;
        var srcCenter = new System.Drawing.Point((int)(srcBounds.X + srcBounds.Width / 2), (int)(srcBounds.Y + srcBounds.Height / 2));
        var tgtCenter = new System.Drawing.Point((int)(tgtBounds.X + tgtBounds.Width / 2), (int)(tgtBounds.Y + tgtBounds.Height / 2));

        Mouse.MoveTo(srcCenter);
        Mouse.Down(MouseButton.Left);
        Thread.Sleep(100);
        Mouse.MoveTo(tgtCenter);
        Thread.Sleep(100);
        Mouse.Up(MouseButton.Left);

        return Ok("drag_drop_completed");
    }

    [McpServerTool(Name = "wpf_set_slider", Destructive = true, Idempotent = true), Description("Set Slider/RangeBase value.")]
    public string SetSlider([Description("Target value to set on the Slider/RangeBase via RangeValuePattern; must fall within the control's min/max range.")] double value, [Description("AutomationId of the target element.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_set_slider", criteria, new() { ["value"] = value });

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (!element.Patterns.RangeValue.IsSupported)
            return Error("Element does not support RangeValue pattern.");

        element.Patterns.RangeValue.Pattern.SetValue(value);
        return Ok("slider_set");
    }

    [McpServerTool(Name = "wpf_set_date", Destructive = true, Idempotent = true), Description("Set DatePicker/Calendar date by typing text value.")]
    public string SetDate([Description("Date value as text, formatted per the control's expected culture/format (e.g. 'MM/dd/yyyy' or '2026-06-17'); set via ValuePattern or typed.")] string date, [Description("AutomationId of the target DatePicker/Calendar.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("AutomationId of a parent/container element to scope the search to; optional.")] string? parentAutomationId = null, [Description("Name of a parent/container element to scope the search to; used when parentAutomationId is omitted.")] string? parentName = null, [Description("If > 0, wait up to this many milliseconds for the element to appear before failing. Default 0 (fail immediately if absent).")] int waitMs = 0)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        _audit.Record("wpf_set_date", criteria, new() { ["date"] = date });

        var element = Resolve(criteria, parentAutomationId, parentName, waitMs);
        if (element is null)
            return Error("Element not found.");

        if (element.Patterns.Value.IsSupported)
        {
            element.Patterns.Value.Pattern.SetValue(date);
            return Ok("date_set");
        }

        // Fallback: focus and type
        element.Focus();
        Keyboard.Type(date);
        return Ok("date_typed");
    }

    [McpServerTool(Name = "wpf_accept_dialog", Destructive = false), Description("Click OK/Yes/Accept on current modal dialog.")]
    public string AcceptDialog()
    {
        _audit.Record("wpf_accept_dialog");

        // Try common accept button names
        string[] acceptNames = ["OK", "Ok", "Yes", "Accept", "Confirm", "Save"];
        foreach (var buttonName in acceptNames)
        {
            var criteria = new ElementCriteria { Name = buttonName, ControlType = "Button" };
            var element = _uia.FindElement(criteria);
            if (element is not null)
            {
                if (element.Patterns.Invoke.IsSupported)
                    element.Patterns.Invoke.Pattern.Invoke();
                else
                    element.Click();
                return Ok($"accepted_via_{buttonName}");
            }
        }

        return Error("No accept/OK button found in current window.");
    }

    [McpServerTool(Name = "wpf_cancel_dialog", Destructive = false), Description("Click Cancel/No/Close on current modal dialog.")]
    public string CancelDialog()
    {
        _audit.Record("wpf_cancel_dialog");

        string[] cancelNames = ["Cancel", "No", "Close", "Abort"];
        foreach (var buttonName in cancelNames)
        {
            var criteria = new ElementCriteria { Name = buttonName, ControlType = "Button" };
            var element = _uia.FindElement(criteria);
            if (element is not null)
            {
                if (element.Patterns.Invoke.IsSupported)
                    element.Patterns.Invoke.Pattern.Invoke();
                else
                    element.Click();
                return Ok($"cancelled_via_{buttonName}");
            }
        }

        return Error("No cancel/close button found in current window.");
    }

    // Resolves a target element, optionally scoped to a parent/container and optionally
    // waiting up to waitMs for it to appear (0 = fail immediately if absent).
    private FlaUI.Core.AutomationElements.AutomationElement? Resolve(ElementCriteria criteria, string? parentAutomationId, string? parentName, int waitMs)
    {
        FlaUI.Core.AutomationElements.AutomationElement? root = null;
        if (!string.IsNullOrEmpty(parentAutomationId) || !string.IsNullOrEmpty(parentName))
            root = _uia.FindElement(new ElementCriteria { AutomationId = parentAutomationId, Name = parentName });
        return _uia.FindElement(criteria, root, waitMs);
    }

    private void RecordAction(string action, ElementCriteria? selector, string? value = null)
    {
        if (_recording.IsRecording)
        {
            _recording.AddStep(new RecordingStep
            {
                Action = action,
                Selector = selector,
                Value = value
            });
        }
    }

    private static string Ok(string result) =>
        JsonSerializer.Serialize(new { result }, JsonOptions.Default);

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, JsonOptions.Default);
}
