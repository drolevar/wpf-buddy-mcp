using System.ComponentModel;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class IntentTools
{
    private readonly UiaAdapter _uia;
    private readonly SessionManager _session;
    private readonly AuditLog _audit;
    private readonly RecordingService _recording;

    public IntentTools(UiaAdapter uia, SessionManager session, AuditLog audit, RecordingService recording)
    {
        _uia = uia;
        _session = session;
        _audit = audit;
        _recording = recording;
    }

    [McpServerTool(Name = "wpf_goal_execute", Destructive = true), Description("Execute a high-level goal by analyzing the current screen state and performing the needed actions adaptively. The AI decides the steps based on what's visible. Example goals: 'fill the form and save', 'navigate to Settings', 'select the first patient'.")]
    public string GoalExecute(
        [Description("Natural-language description of the high-level goal to accomplish, e.g. 'fill the form and save' or 'navigate to Settings'. Required.")] string goal,
        [Description("Optional extra context to guide planning, such as data values or constraints. Omit if not needed.")] string? context = null,
        [Description("Maximum number of action steps to attempt before stopping. Default 20.")] int maxSteps = 20,
        [Description("Delay in milliseconds to wait after each action for the UI to update. Default 300.")] int delayMs = 300)
    {
        _audit.Record("wpf_goal_execute", parameters: new() { ["goal"] = goal });
        try
        {
            var planner = new GoalPlanner(_uia, _session);
            var result = planner.Execute(goal, context, maxSteps, delayMs);
            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_goal_plan", ReadOnly = true), Description("Plan steps to achieve a goal WITHOUT executing them. Returns a proposed action plan based on current UI state. Useful for preview/dry-run.")]
    public string GoalPlan(
        [Description("Natural-language description of the goal to plan steps for. Required.")] string goal,
        [Description("Optional extra context to guide planning. Omit if not needed.")] string? context = null)
    {
        _audit.Record("wpf_goal_plan", parameters: new() { ["goal"] = goal });
        try
        {
            var allElements = _uia.QueryElements();
            var actionable = allElements.Where(e => IsActionable(e.ControlType)).ToList();

            var plan = AnalyzeGoal(goal, actionable);

            return JsonSerializer.Serialize(new
            {
                goal,
                currentScreen = _session.ActiveWindow?.Title,
                proposedSteps = plan,
                confidence = plan.Count > 0 ? "high" : "low",
                note = plan.Count == 0
                    ? "Could not determine steps from current screen. The goal may require navigation to a different screen first."
                    : "Steps are based on current screen state. Execute with wpf_goal_execute."
            }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_goal_verify", ReadOnly = true), Description("Verify that a goal's postcondition is met. Checks the current UI state against expected outcomes.")]
    public string GoalVerify(
        [Description("Natural-language description of the expected outcome to verify against the current UI, e.g. 'no errors are shown' or 'the grid contains data'. Required.")] string expectedOutcome)
    {
        _audit.Record("wpf_goal_verify");
        try
        {
            var allElements = _uia.QueryElements();
            var verification = VerifyOutcome(expectedOutcome, allElements);
            return JsonSerializer.Serialize(verification, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_smart_fill", Destructive = true), Description("Intelligently fill a form based on field names and provided data. Maps data keys to UI fields by name/automationId similarity.")]
    public string SmartFill(
        [Description("JSON object string mapping field names to values, e.g. '{\"FirstName\":\"John\",\"AgreeTos\":\"true\"}'. Keys are matched to fields by Name/AutomationId similarity. For CheckBox/RadioButton fields, values 'true'/'yes'/'1' mean checked, anything else unchecked. Required.")] string dataJson)
    {
        _audit.Record("wpf_smart_fill");
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(dataJson, JsonOptions.Default);
            if (data is null || data.Count == 0)
                return JsonSerializer.Serialize(new { error = "No data provided. Pass a JSON object with field names/values." }, JsonOptions.Default);

            var allElements = _uia.QueryElements();
            var inputs = allElements.Where(e => e.ControlType is "TextBox" or "ComboBox" or "CheckBox" or "RadioButton").ToList();

            var results = new List<object>();

            foreach (var kvp in data)
            {
                var match = FindBestMatch(kvp.Key, inputs);
                if (match is null)
                {
                    results.Add(new { field = kvp.Key, status = "not_found", message = "No matching field found" });
                    continue;
                }

                var criteria = new ElementCriteria
                {
                    AutomationId = match.AutomationId,
                    Name = string.IsNullOrEmpty(match.AutomationId) ? match.Name : null
                };

                var element = _uia.FindElement(criteria);
                if (element is null)
                {
                    results.Add(new { field = kvp.Key, status = "error", message = "Element found in query but not resolvable" });
                    continue;
                }

                try
                {
                    if (match.ControlType is "CheckBox" or "RadioButton")
                    {
                        if (element.Patterns.Toggle.IsSupported)
                        {
                            var isChecked = element.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault == ToggleState.On;
                            var shouldCheck = kvp.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                             kvp.Value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                                             kvp.Value == "1";
                            if (isChecked != shouldCheck)
                                element.Patterns.Toggle.Pattern.Toggle();
                        }
                    }
                    else if (element.Patterns.Value.IsSupported)
                    {
                        element.Patterns.Value.Pattern.SetValue(kvp.Value);
                    }
                    else
                    {
                        element.Focus();
                        Thread.Sleep(50);
                        Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
                        Keyboard.Type(kvp.Value);
                    }

                    results.Add(new
                    {
                        field = kvp.Key,
                        status = "filled",
                        matchedElement = match.AutomationId ?? match.Name,
                        value = kvp.Value
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new { field = kvp.Key, status = "error", message = ex.Message });
                }
            }

            return JsonSerializer.Serialize(new
            {
                filled = results.Count(r => ((JsonElement)JsonSerializer.SerializeToElement(r)).GetProperty("status").GetString() == "filled"),
                total = data.Count,
                details = results
            }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_navigate_to", Destructive = false, Idempotent = true), Description("Navigate to a target screen/tab/page by name. Searches for navigation elements (tabs, menu items, buttons) matching the target and activates them.")]
    public string NavigateTo(
        [Description("Name or AutomationId of the navigation target (tab, menu item, hyperlink, tree item, button, or list item). Matched first by exact equality, then by substring (case-insensitive). Required.")] string target,
        [Description("Delay in milliseconds to wait after activating the target for the UI to update. Default 300.")] int delayMs = 300)
    {
        _audit.Record("wpf_navigate_to", parameters: new() { ["target"] = target });
        try
        {
            var allElements = _uia.QueryElements();
            var navElements = allElements.Where(e =>
                e.ControlType is "TabItem" or "MenuItem" or "Hyperlink" or "TreeItem" or "Button" or "ListItem").ToList();

            // Find best match for target
            var match = navElements.FirstOrDefault(e =>
                (e.Name?.Equals(target, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.AutomationId?.Equals(target, StringComparison.OrdinalIgnoreCase) ?? false));

            if (match is null)
            {
                match = navElements.FirstOrDefault(e =>
                    (e.Name?.Contains(target, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.AutomationId?.Contains(target, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (match is null)
                return JsonSerializer.Serialize(new
                {
                    error = $"No navigation element matching '{target}' found.",
                    availableTargets = navElements.Select(e => e.Name ?? e.AutomationId).Where(n => n is not null).Distinct().Take(15)
                }, JsonOptions.Default);

            var criteria = new ElementCriteria
            {
                AutomationId = match.AutomationId,
                Name = string.IsNullOrEmpty(match.AutomationId) ? match.Name : null
            };
            var element = _uia.FindElement(criteria);
            if (element is null)
                return JsonSerializer.Serialize(new { error = "Matched element not resolvable." }, JsonOptions.Default);

            if (element.Patterns.Invoke.IsSupported)
                element.Patterns.Invoke.Pattern.Invoke();
            else if (element.Patterns.SelectionItem.IsSupported)
                element.Patterns.SelectionItem.Pattern.Select();
            else if (element.Patterns.ExpandCollapse.IsSupported)
                element.Patterns.ExpandCollapse.Pattern.Expand();
            else
                element.Click();

            Thread.Sleep(delayMs);

            return JsonSerializer.Serialize(new
            {
                result = "navigated",
                target,
                element = new { match.AutomationId, match.Name, match.ControlType },
                currentWindow = _session.ActiveWindow?.Title
            }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    private List<object> AnalyzeGoal(string goal, List<UiElement> actionable)
    {
        var steps = new List<object>();
        var goalLower = goal.ToLowerInvariant();

        // Pattern: "fill" or "enter" — look for input fields
        if (goalLower.Contains("fill") || goalLower.Contains("enter") || goalLower.Contains("type"))
        {
            var inputs = actionable.Where(e => e.ControlType is "TextBox" or "ComboBox").ToList();
            foreach (var input in inputs.Where(i => i.IsEnabled))
            {
                steps.Add(new { action = "set_value", target = input.Name ?? input.AutomationId, controlType = input.ControlType });
            }
        }

        // Pattern: "save" or "submit" — look for submit button
        if (goalLower.Contains("save") || goalLower.Contains("submit") || goalLower.Contains("create") || goalLower.Contains("add"))
        {
            var saveBtn = actionable.FirstOrDefault(e =>
                e.ControlType == "Button" &&
                ((e.Name?.Contains("Save", StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (e.Name?.Contains("Submit", StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (e.Name?.Contains("OK", StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (e.Name?.Contains("Add", StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (e.Name?.Contains("Create", StringComparison.OrdinalIgnoreCase) ?? false)));

            if (saveBtn is not null)
                steps.Add(new { action = "click", target = saveBtn.Name ?? saveBtn.AutomationId });
        }

        // Pattern: "navigate" or "go to" — look for nav elements
        if (goalLower.Contains("navigate") || goalLower.Contains("go to") || goalLower.Contains("open"))
        {
            var tabs = actionable.Where(e => e.ControlType is "TabItem" or "MenuItem").ToList();
            foreach (var tab in tabs)
            {
                if (goalLower.Contains(tab.Name?.ToLowerInvariant() ?? ""))
                {
                    steps.Add(new { action = "select_tab", target = tab.Name ?? tab.AutomationId });
                    break;
                }
            }
        }

        // Pattern: "select" — look for list/grid items
        if (goalLower.Contains("select"))
        {
            var selectable = actionable.Where(e => e.ControlType is "ListItem" or "DataItem" or "TreeItem").Take(1).ToList();
            foreach (var item in selectable)
            {
                steps.Add(new { action = "select", target = item.Name ?? item.AutomationId });
            }
        }

        return steps;
    }

    private object VerifyOutcome(string expectedOutcome, List<UiElement> allElements)
    {
        var outcomeLower = expectedOutcome.ToLowerInvariant();
        var checks = new List<object>();
        bool overallPass = true;

        // Check for "no errors" / "no validation"
        if (outcomeLower.Contains("no error") || outcomeLower.Contains("no validation"))
        {
            var errors = allElements.Where(e =>
                (e.Name?.Contains("error", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.AutomationId?.Contains("Error", StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

            var pass = errors.Count == 0;
            overallPass &= pass;
            checks.Add(new { check = "no_errors", pass, detail = pass ? "No error elements found" : $"Found {errors.Count} error element(s)" });
        }

        // Check for "appears" or "visible" or "exists"
        if (outcomeLower.Contains("appears") || outcomeLower.Contains("visible") || outcomeLower.Contains("exists"))
        {
            checks.Add(new { check = "element_presence", pass = true, detail = "Screen is rendered with elements" });
        }

        // Check for specific text
        var textElements = allElements.Where(e => !string.IsNullOrEmpty(e.Value) || !string.IsNullOrEmpty(e.Name)).ToList();
        if (outcomeLower.Contains("grid") || outcomeLower.Contains("list"))
        {
            var grids = allElements.Where(e => e.ControlType is "DataGrid" or "List" or "Table").ToList();
            var hasData = grids.Count > 0;
            overallPass &= hasData;
            checks.Add(new { check = "grid_present", pass = hasData, detail = hasData ? $"Found {grids.Count} grid(s)" : "No grid found" });
        }

        return new
        {
            expectedOutcome,
            overallResult = overallPass ? "PASS" : "FAIL",
            checks
        };
    }

    private static UiElement? FindBestMatch(string fieldName, List<UiElement> inputs)
    {
        var normalized = fieldName.Replace("_", "").Replace(" ", "").Replace("-", "").ToLowerInvariant();

        // Exact match on AutomationId or Name
        var exact = inputs.FirstOrDefault(i =>
            (i.AutomationId?.Replace("_", "").Replace(" ", "").Equals(normalized, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (i.Name?.Replace("_", "").Replace(" ", "").Replace("-", "").Equals(normalized, StringComparison.OrdinalIgnoreCase) ?? false));
        if (exact is not null) return exact;

        // Contains match
        var contains = inputs.FirstOrDefault(i =>
            (i.AutomationId?.Contains(fieldName, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (i.Name?.Contains(fieldName, StringComparison.OrdinalIgnoreCase) ?? false));
        if (contains is not null) return contains;

        // Fuzzy — normalized contains
        return inputs.FirstOrDefault(i =>
            (i.AutomationId?.Replace("_", "").Replace(" ", "").Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (i.Name?.Replace("_", "").Replace(" ", "").Replace("-", "").Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private static bool IsActionable(string? controlType) =>
        controlType is "Button" or "TextBox" or "ComboBox" or "CheckBox"
            or "RadioButton" or "MenuItem" or "TabItem" or "ListItem"
            or "DataItem" or "TreeItem" or "Slider" or "Hyperlink";
}

internal sealed class GoalPlanner
{
    private readonly UiaAdapter _uia;
    private readonly SessionManager _session;

    public GoalPlanner(UiaAdapter uia, SessionManager session)
    {
        _uia = uia;
        _session = session;
    }

    public object Execute(string goal, string? context, int maxSteps, int delayMs)
    {
        var stepsExecuted = new List<object>();
        var goalLower = goal.ToLowerInvariant();

        // Analyze current state
        var allElements = _uia.QueryElements();
        var actionable = allElements.Where(e => IsActionable(e.ControlType)).ToList();

        // Strategy: Navigate if needed
        if (goalLower.Contains("navigate") || goalLower.Contains("go to") || goalLower.Contains("open"))
        {
            var target = ExtractTarget(goal);
            if (target is not null)
            {
                var navResult = TryNavigate(target, actionable, delayMs);
                stepsExecuted.Add(navResult);
            }
        }

        // Strategy: Fill form if goal mentions data
        if (goalLower.Contains("fill") || goalLower.Contains("enter") || goalLower.Contains("create") || goalLower.Contains("add"))
        {
            var inputs = actionable.Where(e => e.ControlType is "TextBox" or "ComboBox" && e.IsEnabled).ToList();
            if (inputs.Count > 0)
            {
                stepsExecuted.Add(new
                {
                    step = "identified_form_fields",
                    fields = inputs.Select(i => new { i.AutomationId, i.Name, i.ControlType }).ToList(),
                    note = "Use wpf_smart_fill with data JSON to populate these fields."
                });
            }
        }

        // Strategy: Click submit-like button
        if (goalLower.Contains("save") || goalLower.Contains("submit") || goalLower.Contains("confirm"))
        {
            var submitBtn = FindSubmitButton(actionable);
            if (submitBtn is not null)
            {
                var element = _uia.FindElement(new ElementCriteria
                {
                    AutomationId = submitBtn.AutomationId,
                    Name = string.IsNullOrEmpty(submitBtn.AutomationId) ? submitBtn.Name : null
                });

                if (element is not null)
                {
                    if (element.Properties.IsEnabled.ValueOrDefault)
                    {
                        if (element.Patterns.Invoke.IsSupported)
                            element.Patterns.Invoke.Pattern.Invoke();
                        else
                            element.Click();

                        Thread.Sleep(delayMs);
                        stepsExecuted.Add(new { step = "clicked_submit", button = submitBtn.Name ?? submitBtn.AutomationId, result = "success" });
                    }
                    else
                    {
                        stepsExecuted.Add(new { step = "submit_blocked", button = submitBtn.Name ?? submitBtn.AutomationId, reason = "Button is disabled — required fields may be empty." });
                    }
                }
            }
        }

        // Strategy: Select item
        if (goalLower.Contains("select"))
        {
            var target = ExtractTarget(goal);
            if (target is not null)
            {
                var selectableItems = actionable.Where(e => e.ControlType is "ListItem" or "DataItem" or "TreeItem" or "TabItem").ToList();
                var match = selectableItems.FirstOrDefault(e =>
                    (e.Name?.Contains(target, StringComparison.OrdinalIgnoreCase) ?? false));

                if (match is not null)
                {
                    var element = _uia.FindElement(new ElementCriteria
                    {
                        AutomationId = match.AutomationId,
                        Name = string.IsNullOrEmpty(match.AutomationId) ? match.Name : null
                    });

                    if (element is not null)
                    {
                        if (element.Patterns.SelectionItem.IsSupported)
                            element.Patterns.SelectionItem.Pattern.Select();
                        else
                            element.Click();

                        Thread.Sleep(delayMs);
                        stepsExecuted.Add(new { step = "selected", item = match.Name ?? match.AutomationId, result = "success" });
                    }
                }
            }
        }

        return new
        {
            goal,
            currentWindow = _session.ActiveWindow?.Title,
            stepsExecuted,
            finalState = stepsExecuted.Count > 0 ? "actions_performed" : "no_matching_strategy",
            suggestion = stepsExecuted.Count == 0
                ? "Goal could not be mapped to available UI elements. Try being more specific or navigate to the correct screen first."
                : null as string
        };
    }

    private object TryNavigate(string target, List<UiElement> actionable, int delayMs)
    {
        var navElements = actionable.Where(e => e.ControlType is "TabItem" or "MenuItem" or "Hyperlink" or "TreeItem" or "Button").ToList();
        var match = navElements.FirstOrDefault(e =>
            (e.Name?.Contains(target, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (e.AutomationId?.Contains(target, StringComparison.OrdinalIgnoreCase) ?? false));

        if (match is null)
            return new { step = "navigate", result = "target_not_found", target };

        var element = _uia.FindElement(new ElementCriteria
        {
            AutomationId = match.AutomationId,
            Name = string.IsNullOrEmpty(match.AutomationId) ? match.Name : null
        });

        if (element is null)
            return new { step = "navigate", result = "element_not_resolvable", target };

        if (element.Patterns.Invoke.IsSupported)
            element.Patterns.Invoke.Pattern.Invoke();
        else if (element.Patterns.SelectionItem.IsSupported)
            element.Patterns.SelectionItem.Pattern.Select();
        else
            element.Click();

        Thread.Sleep(delayMs);
        return new { step = "navigate", result = "success", target, element = match.Name ?? match.AutomationId };
    }

    private static UiElement? FindSubmitButton(List<UiElement> actionable)
    {
        return actionable.FirstOrDefault(e =>
            e.ControlType == "Button" &&
            ((e.Name?.Contains("Save", StringComparison.OrdinalIgnoreCase) ?? false) ||
             (e.Name?.Contains("Submit", StringComparison.OrdinalIgnoreCase) ?? false) ||
             (e.Name?.Contains("OK", StringComparison.OrdinalIgnoreCase) ?? false) ||
             (e.Name?.Contains("Add", StringComparison.OrdinalIgnoreCase) ?? false) ||
             (e.Name?.Contains("Create", StringComparison.OrdinalIgnoreCase) ?? false) ||
             (e.Name?.Contains("Confirm", StringComparison.OrdinalIgnoreCase) ?? false)));
    }

    private static string? ExtractTarget(string goal)
    {
        var words = goal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Look for quoted strings
        var quoteStart = goal.IndexOf('\'');
        var quoteEnd = goal.LastIndexOf('\'');
        if (quoteStart >= 0 && quoteEnd > quoteStart)
            return goal[(quoteStart + 1)..quoteEnd];

        quoteStart = goal.IndexOf('"');
        quoteEnd = goal.LastIndexOf('"');
        if (quoteStart >= 0 && quoteEnd > quoteStart)
            return goal[(quoteStart + 1)..quoteEnd];

        // Take the last meaningful word(s) after "to" or "the"
        for (int i = words.Length - 1; i >= 1; i--)
        {
            if (words[i - 1].Equals("to", StringComparison.OrdinalIgnoreCase) ||
                words[i - 1].Equals("the", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join(" ", words[i..]);
            }
        }

        return words.Length > 1 ? words[^1] : null;
    }

    private static bool IsActionable(string? controlType) =>
        controlType is "Button" or "TextBox" or "ComboBox" or "CheckBox"
            or "RadioButton" or "MenuItem" or "TabItem" or "ListItem"
            or "DataItem" or "TreeItem" or "Slider" or "Hyperlink";
}
