using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class ExplorerTools
{
    private readonly ExplorerService _explorer;
    private readonly UiaAdapter _uia;
    private readonly AuditLog _audit;

    public ExplorerTools(ExplorerService explorer, UiaAdapter uia, AuditLog audit)
    {
        _explorer = explorer;
        _uia = uia;
        _audit = audit;
    }

    [McpServerTool(Name = "wpf_explore_app", Destructive = false), Description("Autonomously explore the attached app by clicking navigation elements. Returns a state machine diagram of discovered screens, element inventories, and testability scores.")]
    public string ExploreApp(
        [Description("Maximum number of exploration steps (element interactions) to perform before stopping. Optional; defaults to 30.")] int maxSteps = 30,
        [Description("Maximum navigation depth to recurse from the starting screen. Optional; defaults to 3.")] int maxDepth = 3,
        [Description("Delay in milliseconds to wait after each interaction for the UI to settle. Optional; defaults to 500.")] int delayMs = 500)
    {
        _audit.Record("wpf_explore_app");
        try
        {
            var result = _explorer.Explore(maxSteps, maxDepth, delayMs);

            var response = new
            {
                summary = new
                {
                    screensDiscovered = result.Screens.Count,
                    transitionsFound = result.Transitions.Count,
                    stepsTaken = result.StepsTaken
                },
                screens = result.Screens.Select(s => new
                {
                    s.Fingerprint,
                    s.WindowTitle,
                    s.TotalElements,
                    s.ActionableElements,
                    automationIdCoverage = $"{s.AutomationIdCoverage}%",
                    testabilityScore = s.AutomationIdCoverage >= 80 ? "good" : s.AutomationIdCoverage >= 50 ? "fair" : "poor",
                    navigationElements = s.NavigationElements,
                    inputElements = s.InputElements
                }),
                transitions = result.Transitions.Select(t => new
                {
                    from = t.From,
                    to = t.To,
                    action = t.Action,
                    element = t.ElementName
                }),
                mermaidDiagram = result.MermaidDiagram
            };

            return JsonSerializer.Serialize(response, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            throw ToolError.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "wpf_explore_screen", ReadOnly = true), Description("Analyze the current screen: list all actionable elements, group by function (navigation, input, display), and rate testability.")]
    public string ExploreScreen()
    {
        _audit.Record("wpf_explore_screen");
        try
        {
            var allElements = _uia.QueryElements();
            var actionable = allElements.Where(e => IsActionable(e.ControlType)).ToList();
            var withId = actionable.Count(e => !string.IsNullOrEmpty(e.AutomationId));
            var coverage = actionable.Count > 0 ? (int)((double)withId / actionable.Count * 100) : 100;

            var grouped = new
            {
                navigation = actionable
                    .Where(e => e.ControlType is "Button" or "MenuItem" or "TabItem" or "Hyperlink" or "TreeItem")
                    .Select(e => new { e.AutomationId, e.Name, e.ControlType, e.IsEnabled })
                    .ToList(),
                input = actionable
                    .Where(e => e.ControlType is "TextBox" or "ComboBox" or "CheckBox" or "RadioButton" or "Slider")
                    .Select(e => new { e.AutomationId, e.Name, e.ControlType, e.Value, e.IsEnabled })
                    .ToList(),
                display = allElements
                    .Where(e => e.ControlType is "Text" or "Image" or "ProgressBar" or "StatusBar")
                    .Select(e => new { e.AutomationId, e.Name, e.ControlType, e.Value })
                    .Take(20)
                    .ToList(),
                grids = allElements
                    .Where(e => e.ControlType is "DataGrid" or "Table" or "List")
                    .Select(e => new { e.AutomationId, e.Name, e.ControlType })
                    .ToList()
            };

            var response = new
            {
                summary = new
                {
                    totalElements = allElements.Count,
                    actionableElements = actionable.Count,
                    automationIdCoverage = $"{coverage}%",
                    testabilityScore = coverage >= 80 ? "good" : coverage >= 50 ? "fair" : "poor",
                    recommendation = coverage < 50
                        ? "Many elements lack AutomationIds. Add x:Name or AutomationProperties.AutomationId for reliable automation."
                        : coverage < 80
                            ? "Some elements missing AutomationIds. Consider adding IDs to key actionable elements."
                            : "Good automation coverage. Most elements are reliably selectable."
                },
                elements = grouped
            };

            return JsonSerializer.Serialize(response, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            throw ToolError.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "wpf_suggest_test_scenarios", ReadOnly = true), Description("Based on discovered screens and elements, suggest test scenarios for the current window.")]
    public string SuggestTestScenarios()
    {
        _audit.Record("wpf_suggest_test_scenarios");
        try
        {
            var allElements = _uia.QueryElements();
            var actionable = allElements.Where(e => IsActionable(e.ControlType)).ToList();

            var scenarios = new List<object>();

            // Input form scenarios
            var inputs = actionable.Where(e => e.ControlType is "TextBox" or "ComboBox" or "CheckBox" or "RadioButton").ToList();
            var buttons = actionable.Where(e => e.ControlType is "Button").ToList();
            var submitButton = buttons.FirstOrDefault(b =>
                (b.Name?.Contains("Save", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (b.Name?.Contains("Submit", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (b.Name?.Contains("OK", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (b.Name?.Contains("Add", StringComparison.OrdinalIgnoreCase) ?? false));

            if (inputs.Count > 0 && submitButton is not null)
            {
                scenarios.Add(new
                {
                    type = "form_submission",
                    name = $"Fill and submit form via '{submitButton.Name}'",
                    steps = inputs.Select(i => $"Set '{i.Name ?? i.AutomationId}' ({i.ControlType})")
                        .Append($"Click '{submitButton.Name}'")
                        .ToList(),
                    assertions = new[] { "No validation errors", "Form cleared or navigated away" }
                });

                scenarios.Add(new
                {
                    type = "validation",
                    name = "Submit empty form and verify validation errors",
                    steps = new[] { $"Click '{submitButton.Name}' without filling fields" },
                    assertions = new[] { "Validation errors appear", "Required fields highlighted" }
                });
            }

            // Navigation scenarios
            var tabs = actionable.Where(e => e.ControlType is "TabItem").ToList();
            if (tabs.Count > 1)
            {
                scenarios.Add(new
                {
                    type = "navigation",
                    name = "Navigate through all tabs",
                    steps = tabs.Select(t => $"Click tab '{t.Name}'").ToList(),
                    assertions = new[] { "Each tab loads without error", "Content changes per tab" }
                });
            }

            // Grid scenarios
            var grids = allElements.Where(e => e.ControlType is "DataGrid" or "Table" or "List").ToList();
            if (grids.Count > 0)
            {
                scenarios.Add(new
                {
                    type = "data_grid",
                    name = "Verify grid loads and supports interaction",
                    steps = new[] { "Wait for grid to load", "Select first row", "Verify details populate" },
                    assertions = new[] { "Grid has at least 1 row", "Selection triggers detail view" }
                });
            }

            // Menu scenarios
            var menus = actionable.Where(e => e.ControlType is "MenuItem").ToList();
            if (menus.Count > 0)
            {
                scenarios.Add(new
                {
                    type = "menu_navigation",
                    name = "Explore menu items",
                    steps = menus.Take(5).Select(m => $"Open menu '{m.Name}'").ToList(),
                    assertions = new[] { "Menu items are enabled", "Actions complete without error" }
                });
            }

            return JsonSerializer.Serialize(new
            {
                screenContext = new
                {
                    inputFields = inputs.Count,
                    buttons = buttons.Count,
                    tabs = tabs.Count,
                    grids = grids.Count,
                    menuItems = menus.Count
                },
                suggestedScenarios = scenarios
            }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            throw ToolError.Fail(ex.Message);
        }
    }

    private static bool IsActionable(string? controlType) =>
        controlType is "Button" or "TextBox" or "ComboBox" or "CheckBox"
            or "RadioButton" or "MenuItem" or "TabItem" or "ListItem"
            or "DataItem" or "TreeItem" or "Slider" or "Hyperlink";
}
