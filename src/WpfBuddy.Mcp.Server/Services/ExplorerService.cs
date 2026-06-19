using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using WpfBuddy.Mcp.Server.Models;

namespace WpfBuddy.Mcp.Server.Services;

/// <summary>
/// Autonomous explorer that navigates an unknown WPF app by interacting with
/// actionable elements and building a state machine of discovered screens.
/// </summary>
public sealed class ExplorerService
{
    private readonly SessionManager _session;
    private readonly UiaAdapter _uia;
    private readonly AuditLog _audit;

    public ExplorerService(SessionManager session, UiaAdapter uia, AuditLog audit)
    {
        _session = session;
        _uia = uia;
        _audit = audit;
    }

    public ExplorationResult Explore(int maxSteps = 30, int maxDepth = 3, int delayMs = 500)
    {
        var result = new ExplorationResult();
        var visitedStates = new HashSet<string>();
        var transitions = new List<StateTransition>();
        var screensDiscovered = new List<ScreenInfo>();

        var initialFingerprint = GetWindowFingerprint();
        var initialScreen = CaptureScreen(initialFingerprint);
        screensDiscovered.Add(initialScreen);
        visitedStates.Add(initialFingerprint);

        var actionQueue = new Queue<ExplorationAction>();
        EnqueueActions(actionQueue, initialFingerprint);

        int stepsTaken = 0;

        while (actionQueue.Count > 0 && stepsTaken < maxSteps)
        {
            var action = actionQueue.Dequeue();
            stepsTaken++;

            // Ensure we're on the right screen
            var currentFingerprint = GetWindowFingerprint();
            if (currentFingerprint != action.SourceState)
            {
                // Try to navigate back — skip this action
                TryNavigateBack();
                Thread.Sleep(delayMs);
                continue;
            }

            // Perform the action
            bool success = TryPerformAction(action);
            if (!success) continue;

            Thread.Sleep(delayMs);

            // Capture resulting state
            var newFingerprint = GetWindowFingerprint();
            var transition = new StateTransition
            {
                From = action.SourceState,
                To = newFingerprint,
                Action = action.Description,
                ElementName = action.ElementName,
                ElementAutomationId = action.ElementAutomationId
            };
            transitions.Add(transition);

            if (!visitedStates.Contains(newFingerprint))
            {
                visitedStates.Add(newFingerprint);
                var screen = CaptureScreen(newFingerprint);
                screensDiscovered.Add(screen);

                if (visitedStates.Count <= maxDepth * 3)
                {
                    EnqueueActions(actionQueue, newFingerprint);
                }
            }

            // Navigate back if we opened a new window/dialog
            if (newFingerprint != action.SourceState)
            {
                TryNavigateBack();
                Thread.Sleep(delayMs);
            }
        }

        result.Screens = screensDiscovered;
        result.Transitions = transitions;
        result.StepsTaken = stepsTaken;
        result.MermaidDiagram = GenerateMermaid(screensDiscovered, transitions);

        return result;
    }

    private string GetWindowFingerprint()
    {
        try
        {
            var window = _session.ActiveWindow;
            if (window is null) return "unknown";

            var title = window.Title ?? "untitled";
            var children = window.FindAll(TreeScope.Children, FlaUI.Core.Conditions.TrueCondition.Default);
            var controlSignature = string.Join("|", children.Take(10).Select(c =>
                $"{c.Properties.ControlType.ValueOrDefault}:{c.Properties.AutomationId.ValueOrDefault}"));

            return StableFingerprint($"{title}##{controlSignature}");
        }
        catch
        {
            return "error";
        }
    }

    private static string StableFingerprint(string signature)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(signature));
        // Use a short, stable hex prefix as the node id.
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    private ScreenInfo CaptureScreen(string fingerprint)
    {
        var window = _session.ActiveWindow;
        var allElements = _uia.QueryElements();

        var actionable = allElements.Where(e => IsActionable(e.ControlType)).ToList();
        var withId = actionable.Count(e => !string.IsNullOrEmpty(e.AutomationId));

        return new ScreenInfo
        {
            Fingerprint = fingerprint,
            WindowTitle = window?.Title ?? "unknown",
            TotalElements = allElements.Count,
            ActionableElements = actionable.Count,
            AutomationIdCoverage = actionable.Count > 0 ? (int)((double)withId / actionable.Count * 100) : 100,
            NavigationElements = actionable
                .Where(e => IsNavigational(e))
                .Select(e => new ScreenElement
                {
                    AutomationId = e.AutomationId,
                    Name = e.Name,
                    ControlType = e.ControlType
                })
                .Take(20)
                .ToList(),
            InputElements = actionable
                .Where(e => IsInputElement(e))
                .Select(e => new ScreenElement
                {
                    AutomationId = e.AutomationId,
                    Name = e.Name,
                    ControlType = e.ControlType
                })
                .Take(20)
                .ToList()
        };
    }

    private void EnqueueActions(Queue<ExplorationAction> queue, string sourceState)
    {
        try
        {
            var window = _session.ActiveWindow;
            if (window is null) return;

            var elements = window.FindAll(TreeScope.Descendants, FlaUI.Core.Conditions.TrueCondition.Default);

            foreach (var el in elements.Take(50))
            {
                var controlType = el.Properties.ControlType.ValueOrDefault.ToString();
                var automationId = el.Properties.AutomationId.ValueOrDefault ?? "";
                var name = el.Properties.Name.ValueOrDefault ?? "";

                if (IsDestructive(name) || IsDestructive(automationId))
                {
                    // Skip controls that may trigger destructive/irreversible actions.
                    continue;
                }

                if (IsNavigationalType(controlType) && !string.IsNullOrEmpty(name))
                {
                    queue.Enqueue(new ExplorationAction
                    {
                        SourceState = sourceState,
                        ElementAutomationId = automationId,
                        ElementName = name,
                        ControlType = controlType,
                        Description = $"Click '{name}' ({controlType})"
                    });
                }
            }
        }
        catch { }
    }

    private bool TryPerformAction(ExplorationAction action)
    {
        try
        {
            // Defensive gate: never invoke controls that may trigger destructive actions.
            if (IsDestructive(action.ElementName) || IsDestructive(action.ElementAutomationId))
                return false;

            var criteria = new ElementCriteria();
            if (!string.IsNullOrEmpty(action.ElementAutomationId))
                criteria.AutomationId = action.ElementAutomationId;
            else
                criteria.Name = action.ElementName;

            var element = _uia.FindElement(criteria);
            if (element is null) return false;

            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
            }
            else if (element.Patterns.ExpandCollapse.IsSupported)
            {
                element.Patterns.ExpandCollapse.Pattern.Expand();
            }
            else
            {
                element.Click();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TryNavigateBack()
    {
        try
        {
            // Try pressing Escape to close dialogs/popups
            Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        }
        catch { }
    }

    private string GenerateMermaid(List<ScreenInfo> screens, List<StateTransition> transitions)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("stateDiagram-v2");

        foreach (var screen in screens)
        {
            var label = SanitizeMermaidLabel(screen.WindowTitle);
            sb.AppendLine($"    {screen.Fingerprint} : {label}");
        }

        foreach (var t in transitions.DistinctBy(x => $"{x.From}->{x.To}:{x.Action}"))
        {
            var label = SanitizeMermaidLabel(t.Action) ?? "action";
            sb.AppendLine($"    {t.From} --> {t.To} : {label}");
        }

        return sb.ToString();
    }

    private static string? SanitizeMermaidLabel(string? label)
    {
        if (label is null) return null;

        var s = label
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\"", "'")
            .Replace("-->", "->")   // arrow operator
            .Replace("%%", "%")     // comment marker
            .Replace(":", " ");     // transition/label separator

        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    private static bool IsActionable(string? controlType) =>
        controlType is "Button" or "TextBox" or "ComboBox" or "CheckBox"
            or "RadioButton" or "MenuItem" or "TabItem" or "ListItem"
            or "DataItem" or "TreeItem" or "Slider" or "Hyperlink";

    private static bool IsNavigational(UiElement e) =>
        e.ControlType is "Button" or "MenuItem" or "TabItem" or "Hyperlink" or "TreeItem";

    private static bool IsInputElement(UiElement e) =>
        e.ControlType is "TextBox" or "ComboBox" or "CheckBox" or "RadioButton" or "Slider";

    private static readonly string[] DestructiveKeywords =
    [
        "delete", "remove", "send", "submit", "pay", "format",
        "drop", "purge", "discard", "reset", "clear", "save"
    ];

    private static bool IsDestructive(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var kw in DestructiveKeywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsNavigationalType(string controlType) =>
        controlType is "Button" or "MenuItem" or "TabItem" or "Hyperlink" or "TreeItem";
}

public sealed class ExplorationResult
{
    public List<ScreenInfo> Screens { get; set; } = [];
    public List<StateTransition> Transitions { get; set; } = [];
    public int StepsTaken { get; set; }
    public string MermaidDiagram { get; set; } = string.Empty;
}

public sealed class ScreenInfo
{
    public string Fingerprint { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public int TotalElements { get; set; }
    public int ActionableElements { get; set; }
    public int AutomationIdCoverage { get; set; }
    public List<ScreenElement> NavigationElements { get; set; } = [];
    public List<ScreenElement> InputElements { get; set; } = [];
}

public sealed class ScreenElement
{
    public string? AutomationId { get; set; }
    public string? Name { get; set; }
    public string? ControlType { get; set; }
}

public sealed class StateTransition
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string? Action { get; set; }
    public string? ElementName { get; set; }
    public string? ElementAutomationId { get; set; }
}

public sealed class ExplorationAction
{
    public string SourceState { get; set; } = string.Empty;
    public string ElementAutomationId { get; set; } = string.Empty;
    public string ElementName { get; set; } = string.Empty;
    public string ControlType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
