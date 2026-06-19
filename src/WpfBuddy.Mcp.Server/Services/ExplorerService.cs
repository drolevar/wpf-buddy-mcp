using System.Text.Json.Serialization;
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
        EnqueueActions(actionQueue, initialFingerprint, depth: 1);

        int stepsTaken = 0;
        int consecutiveStuck = 0;

        while (actionQueue.Count > 0 && stepsTaken < maxSteps)
        {
            var action = actionQueue.Dequeue();
            stepsTaken++;

            // Ensure we're on the right screen
            var currentFingerprint = GetWindowFingerprint();
            if (currentFingerprint != action.SourceState)
            {
                // Not on the action's source screen — try to get back to it. R2-21: if we cannot
                // return after several attempts (e.g. stranded on an unresponsive modal), bail out
                // rather than spinning uselessly through the rest of the queue.
                if (TryNavigateBack(action.SourceState, delayMs))
                {
                    consecutiveStuck = 0;
                }
                else if (++consecutiveStuck >= MaxConsecutiveStuck)
                {
                    result.Aborted = true;
                    result.AbortReason = $"Stranded: could not return to a known screen after {consecutiveStuck} attempts (likely an unresponsive dialog).";
                    break;
                }
                continue;
            }

            // We're on the action's source screen — genuine progress, so the stuck streak is broken.
            // (Reset here, not only on a successful navigate-back, so interspersed recoveries don't
            // accumulate toward a misleading "consecutive" abort — R2-21.)
            consecutiveStuck = 0;

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

                // R2-22: real per-action depth limit (was a global visited-count heuristic). Only
                // recurse from the new screen while still within maxDepth navigation hops.
                if (action.Depth < maxDepth)
                {
                    EnqueueActions(actionQueue, newFingerprint, depth: action.Depth + 1);
                }
            }

            // Navigate back if we opened a new window/dialog
            if (newFingerprint != action.SourceState)
            {
                TryNavigateBack(action.SourceState, delayMs);
            }
        }

        result.Screens = screensDiscovered;
        result.Transitions = transitions;
        result.StepsTaken = stepsTaken;
        result.MermaidDiagram = GenerateMermaid(screensDiscovered, transitions);

        return result;
    }

    // Bail out of the crawl after this many consecutive failures to return to a known screen (R2-21).
    private const int MaxConsecutiveStuck = 3;

    private string GetWindowFingerprint()
    {
        try
        {
            // R2-20: re-resolve the window actually on screen (e.g. a dialog) rather than a stale cache.
            var window = _session.RefreshActiveWindow();
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

    private void EnqueueActions(Queue<ExplorationAction> queue, string sourceState, int depth)
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
                        Description = $"Click '{name}' ({controlType})",
                        Depth = depth
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

    // Try to return to <paramref name="targetState"/>. Escape-only navigation strands the crawler on
    // a modal that ignores Escape (R2-21), so each attempt also tries a non-committal dialog button.
    // Returns true once the live window matches the target state, false if all attempts fail.
    private bool TryNavigateBack(string targetState, int delayMs)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE); }
            catch { }
            Thread.Sleep(delayMs);
            if (GetWindowFingerprint() == targetState) return true;

            // Escape didn't dismiss it — try a Cancel/Close/No button on a lingering modal dialog.
            TryDismissModalDialog();
            Thread.Sleep(delayMs);
            if (GetWindowFingerprint() == targetState) return true;
        }
        return false;
    }

    private static readonly string[] DismissLabels = ["Cancel", "Close", "No"];

    // Click a non-committal dismissal button on the current window — but ONLY when it is an actual
    // modal dialog, never the main window (clicking "Cancel/Close" there could be destructive).
    private void TryDismissModalDialog()
    {
        try
        {
            var window = _session.RefreshActiveWindow();
            if (window is null) return;

            bool isModal;
            try { isModal = window.Patterns.Window.PatternOrDefault?.IsModal.ValueOrDefault == true; }
            catch { isModal = false; }
            if (!isModal) return;

            var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
            foreach (var b in buttons)
            {
                var label = b.Properties.Name.ValueOrDefault ?? "";
                if (!DismissLabels.Any(d => label.Equals(d, StringComparison.OrdinalIgnoreCase)))
                    continue;
                try
                {
                    if (b.Patterns.Invoke.IsSupported) b.Patterns.Invoke.Pattern.Invoke();
                    else b.Click();
                }
                catch { }
                return;
            }
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

// R2-24: explicit [JsonPropertyName] wire contract for the explorer DTOs (matches other models).
public sealed class ExplorationResult
{
    [JsonPropertyName("screens")] public List<ScreenInfo> Screens { get; set; } = [];
    [JsonPropertyName("transitions")] public List<StateTransition> Transitions { get; set; } = [];
    [JsonPropertyName("stepsTaken")] public int StepsTaken { get; set; }
    [JsonPropertyName("mermaidDiagram")] public string MermaidDiagram { get; set; } = string.Empty;

    /// <summary>Set when the crawl gave up early (e.g. stranded on an unresponsive modal) — R2-21.</summary>
    [JsonPropertyName("aborted")] public bool Aborted { get; set; }
    [JsonPropertyName("abortReason")] public string? AbortReason { get; set; }
}

public sealed class ScreenInfo
{
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = string.Empty;
    [JsonPropertyName("windowTitle")] public string WindowTitle { get; set; } = string.Empty;
    [JsonPropertyName("totalElements")] public int TotalElements { get; set; }
    [JsonPropertyName("actionableElements")] public int ActionableElements { get; set; }
    [JsonPropertyName("automationIdCoverage")] public int AutomationIdCoverage { get; set; }
    [JsonPropertyName("navigationElements")] public List<ScreenElement> NavigationElements { get; set; } = [];
    [JsonPropertyName("inputElements")] public List<ScreenElement> InputElements { get; set; } = [];
}

public sealed class ScreenElement
{
    [JsonPropertyName("automationId")] public string? AutomationId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("controlType")] public string? ControlType { get; set; }
}

public sealed class StateTransition
{
    [JsonPropertyName("from")] public string From { get; set; } = string.Empty;
    [JsonPropertyName("to")] public string To { get; set; } = string.Empty;
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("elementName")] public string? ElementName { get; set; }
    [JsonPropertyName("elementAutomationId")] public string? ElementAutomationId { get; set; }
}

public sealed class ExplorationAction
{
    [JsonPropertyName("sourceState")] public string SourceState { get; set; } = string.Empty;
    [JsonPropertyName("elementAutomationId")] public string ElementAutomationId { get; set; } = string.Empty;
    [JsonPropertyName("elementName")] public string ElementName { get; set; } = string.Empty;
    [JsonPropertyName("controlType")] public string ControlType { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;

    /// <summary>Navigation hops from the start screen; bounds recursion via maxDepth (R2-22).</summary>
    [JsonPropertyName("depth")] public int Depth { get; set; }
}
