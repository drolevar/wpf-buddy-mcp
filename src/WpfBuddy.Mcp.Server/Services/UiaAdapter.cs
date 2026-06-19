using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using WpfBuddy.Mcp.Server.Models;

namespace WpfBuddy.Mcp.Server.Services;

public sealed class UiaAdapter
{
    private readonly SessionManager _session;
    private int _elementCounter;

    public UiaAdapter(SessionManager session)
    {
        _session = session;
    }

    public UiSnapshot CaptureSnapshot(AutomationElement? root = null, int maxDepth = 10)
    {
        EnsureAttached();
        Interlocked.Exchange(ref _elementCounter, 0);

        var window = _session.ActiveWindow
            ?? throw new InvalidOperationException("Could not get main window. The application may be busy or not responding.");
        var startElement = root ?? window;

        var tree = BuildCachedTree(startElement, maxDepth);
        var allElements = FlattenTree(tree);

        var automationIds = allElements
            .Where(e => !string.IsNullOrEmpty(e.AutomationId))
            .GroupBy(e => e.AutomationId)
            .Where(g => g.Count() > 1)
            .Count();

        var missingIds = allElements.Count(e =>
            string.IsNullOrEmpty(e.AutomationId) &&
            IsActionableControlType(e.ControlType));

        var missingNames = allElements.Count(e =>
            string.IsNullOrEmpty(e.Name) &&
            string.IsNullOrEmpty(e.AutomationId) &&
            IsActionableControlType(e.ControlType));

        return new UiSnapshot
        {
            SessionId = _session.SessionId,
            TimestampUtc = DateTime.UtcNow,
            Window = new WindowInfo
            {
                Title = window.Title,
                ProcessId = _session.Application!.ProcessId,
                Handle = window.Properties.NativeWindowHandle.ValueOrDefault.ToString()
            },
            Tree = tree,
            Diagnostics = new SnapshotDiagnostics
            {
                MissingAutomationIds = missingIds,
                DuplicateAutomationIds = automationIds,
                MissingNames = missingNames,
                TotalElements = allElements.Count
            }
        };
    }

    public List<AutomationElement> FindElements(ElementCriteria criteria, AutomationElement? root = null)
    {
        EnsureAttached();
        var searchRoot = root ?? _session.ActiveWindow
            ?? throw new InvalidOperationException("Could not get main window.");
        var condition = BuildCondition(criteria);
        var predicate = BuildNamePredicate(criteria);
        IEnumerable<AutomationElement> matches = searchRoot.FindAll(TreeScope.Descendants, condition);
        if (predicate is not null) matches = matches.Where(predicate);
        return matches.ToList();
    }

    public AutomationElement? FindElement(ElementCriteria criteria, AutomationElement? root = null, int timeoutMs = 0)
    {
        EnsureAttached();
        var searchRoot = root ?? _session.ActiveWindow
            ?? throw new InvalidOperationException("Could not get main window.");
        var condition = BuildCondition(criteria);
        var predicate = BuildNamePredicate(criteria);

        AutomationElement? FindOnce() => predicate is null
            ? searchRoot.FindFirst(TreeScope.Descendants, condition)
            : searchRoot.FindAll(TreeScope.Descendants, condition).FirstOrDefault(predicate);

        // timeoutMs > 0 retries until the element appears (transient absence shouldn't fail instantly).
        if (timeoutMs <= 0)
            return FindOnce();
        return Retry.WhileNull(FindOnce, TimeSpan.FromMilliseconds(timeoutMs)).Result;
    }

    private static Func<AutomationElement, bool>? BuildNamePredicate(ElementCriteria criteria)
    {
        if (string.IsNullOrEmpty(criteria.Name) || string.IsNullOrEmpty(criteria.NameMatch) ||
            criteria.NameMatch.Equals("equals", StringComparison.OrdinalIgnoreCase))
            return null;

        var needle = criteria.Name!;
        var mode = criteria.NameMatch!.ToLowerInvariant();
        return el =>
        {
            var n = el.Properties.Name.ValueOrDefault ?? string.Empty;
            return mode switch
            {
                "contains" => n.Contains(needle, StringComparison.OrdinalIgnoreCase),
                "startswith" => n.StartsWith(needle, StringComparison.OrdinalIgnoreCase),
                "regex" => SafeRegex(n, needle),
                _ => n.Equals(needle, StringComparison.OrdinalIgnoreCase)
            };
        };
    }

    private static bool SafeRegex(string input, string pattern)
    {
        try { return Regex.IsMatch(input, pattern); } catch { return false; }
    }

    /// <summary>Cheap structural fingerprint of a UI tree for change/stability detection (avoids serializing the whole tree each poll).</summary>
    public static string Fingerprint(IEnumerable<UiElement> tree)
    {
        var sb = new StringBuilder();
        void Walk(IEnumerable<UiElement> nodes)
        {
            foreach (var e in nodes)
            {
                sb.Append(e.AutomationId).Append('|').Append(e.ControlType).Append('|')
                  .Append(e.Name).Append('|').Append(e.IsEnabled).Append('|').Append(e.Value).Append(';');
                Walk(e.Children);
            }
        }
        Walk(tree);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public AutomationElement? ResolveSelector(ElementSelector selector)
    {
        EnsureAttached();

        if (selector.Element is not null)
        {
            var element = FindElement(selector.Element);
            if (element is not null) return element;
        }

        if (selector.Fallbacks is not null)
        {
            foreach (var fallback in selector.Fallbacks)
            {
                var element = FindElement(fallback);
                if (element is not null) return element;
            }
        }

        return null;
    }

    public List<UiElement> QueryElements(string? automationId = null, string? name = null, string? controlType = null, string? className = null)
    {
        EnsureAttached();
        var window = _session.ActiveWindow
            ?? throw new InvalidOperationException("Could not get main window.");
        var cf = _session.Automation!.ConditionFactory;
        var conditions = new List<ConditionBase>();

        if (!string.IsNullOrEmpty(automationId))
            conditions.Add(cf.ByAutomationId(automationId));
        if (!string.IsNullOrEmpty(name))
            conditions.Add(cf.ByName(name));
        if (!string.IsNullOrEmpty(controlType) && Enum.TryParse<ControlType>(controlType, true, out var ct))
            conditions.Add(cf.ByControlType(ct));
        if (!string.IsNullOrEmpty(className))
            conditions.Add(cf.ByClassName(className));

        ConditionBase finalCondition = conditions.Count switch
        {
            0 => TrueCondition.Default,
            1 => conditions[0],
            _ => new AndCondition(conditions.ToArray())
        };

        var elements = window.FindAll(TreeScope.Descendants, finalCondition);
        return elements.Select(MapElement).ToList();
    }

    public UiElement MapElement(AutomationElement element)
    {
        var patterns = new List<string>();
        try
        {
            if (element.Patterns.Invoke.IsSupported) patterns.Add("Invoke");
            if (element.Patterns.Value.IsSupported) patterns.Add("Value");
            if (element.Patterns.Toggle.IsSupported) patterns.Add("Toggle");
            if (element.Patterns.Selection.IsSupported) patterns.Add("Selection");
            if (element.Patterns.SelectionItem.IsSupported) patterns.Add("SelectionItem");
            if (element.Patterns.ExpandCollapse.IsSupported) patterns.Add("ExpandCollapse");
            if (element.Patterns.ScrollItem.IsSupported) patterns.Add("ScrollItem");
            if (element.Patterns.Grid.IsSupported) patterns.Add("Grid");
            if (element.Patterns.RangeValue.IsSupported) patterns.Add("RangeValue");
        }
        catch { }

        string? value = null;
        try
        {
            if (element.Patterns.Value.IsSupported)
                value = element.Patterns.Value.Pattern.Value.ValueOrDefault;
        }
        catch { }

        var bounds = element.BoundingRectangle;

        return new UiElement
        {
            Id = $"e{Interlocked.Increment(ref _elementCounter)}",
            AutomationId = element.Properties.AutomationId.ValueOrDefault,
            Name = element.Properties.Name.ValueOrDefault,
            ControlType = element.Properties.ControlType.ValueOrDefault.ToString(),
            ClassName = element.Properties.ClassName.ValueOrDefault,
            IsEnabled = element.Properties.IsEnabled.ValueOrDefault,
            IsOffscreen = element.Properties.IsOffscreen.ValueOrDefault,
            Bounds = new ElementBounds
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height
            },
            Patterns = patterns,
            Value = value
        };
    }

    private List<UiElement> BuildCachedTree(AutomationElement startElement, int maxDepth)
    {
        // Cache the bulk element properties for the whole subtree in one pass instead of a
        // cross-process round-trip per property per node. AutomationElementMode.Full keeps a
        // live element so any uncached read (e.g. pattern availability) falls back transparently.
        try
        {
            var el = _session.Automation!.PropertyLibrary.Element;
            var cache = new CacheRequest
            {
                AutomationElementMode = AutomationElementMode.Full,
                TreeScope = TreeScope.Subtree
            };
            cache.Add(el.AutomationId);
            cache.Add(el.Name);
            cache.Add(el.ControlType);
            cache.Add(el.ClassName);
            cache.Add(el.IsEnabled);
            cache.Add(el.IsOffscreen);
            cache.Add(el.BoundingRectangle);
            using (cache.Activate())
                return BuildTree(startElement, maxDepth, 0);
        }
        catch
        {
            // Caching unavailable for any reason — fall back to the uncached walk.
            return BuildTree(startElement, maxDepth, 0);
        }
    }

    private List<UiElement> BuildTree(AutomationElement element, int maxDepth, int currentDepth)
    {
        var result = new List<UiElement>();
        if (currentDepth > maxDepth) return result;

        var uiElement = MapElement(element);

        try
        {
            var children = element.FindAll(TreeScope.Children, TrueCondition.Default);
            foreach (var child in children)
                uiElement.Children.AddRange(BuildTree(child, maxDepth, currentDepth + 1));
        }
        catch { }

        result.Add(uiElement);
        return result;
    }

    private ConditionBase BuildCondition(ElementCriteria criteria)
    {
        var cf = _session.Automation!.ConditionFactory;
        var conditions = new List<ConditionBase>();

        if (!string.IsNullOrEmpty(criteria.AutomationId))
            conditions.Add(cf.ByAutomationId(criteria.AutomationId));
        // When a non-equals name match is requested, the UIA condition can't express it —
        // omit Name here and let BuildNamePredicate filter the candidates in-memory.
        var nameViaPredicate = !string.IsNullOrEmpty(criteria.NameMatch)
            && !criteria.NameMatch.Equals("equals", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(criteria.Name) && !nameViaPredicate)
            conditions.Add(cf.ByName(criteria.Name));
        if (!string.IsNullOrEmpty(criteria.ControlType) && Enum.TryParse<ControlType>(criteria.ControlType, true, out var ct))
            conditions.Add(cf.ByControlType(ct));
        if (!string.IsNullOrEmpty(criteria.ClassName))
            conditions.Add(cf.ByClassName(criteria.ClassName));

        return conditions.Count switch
        {
            0 => TrueCondition.Default,
            1 => conditions[0],
            _ => new AndCondition(conditions.ToArray())
        };
    }

    private static List<UiElement> FlattenTree(List<UiElement> tree)
    {
        var result = new List<UiElement>();
        foreach (var element in tree)
        {
            result.Add(element);
            result.AddRange(FlattenTree(element.Children));
        }
        return result;
    }

    private static bool IsActionableControlType(string? controlType)
    {
        if (string.IsNullOrEmpty(controlType)) return false;
        return controlType is "Button" or "TextBox" or "ComboBox" or "CheckBox"
            or "RadioButton" or "MenuItem" or "Tab" or "TabItem" or "ListItem"
            or "DataItem" or "TreeItem" or "Slider" or "Hyperlink";
    }

    private void EnsureAttached()
    {
        if (!_session.IsAttached)
            throw new InvalidOperationException("No app attached. Use wpf_attach or wpf_launch_app first.");
    }
}
