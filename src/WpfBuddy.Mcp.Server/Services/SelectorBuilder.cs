using FlaUI.Core.AutomationElements;
using WpfBuddy.Mcp.Server.Models;

namespace WpfBuddy.Mcp.Server.Services;

public sealed class SelectorBuilder
{
    private readonly UiaAdapter _uiaAdapter;

    public SelectorBuilder(UiaAdapter uiaAdapter)
    {
        _uiaAdapter = uiaAdapter;
    }

    public ElementSelector BuildSelector(AutomationElement element)
    {
        var automationId = element.Properties.AutomationId.ValueOrDefault;
        var name = element.Properties.Name.ValueOrDefault;
        var controlType = element.Properties.ControlType.ValueOrDefault.ToString();
        var className = element.Properties.ClassName.ValueOrDefault;

        var primary = new ElementCriteria();
        var fallbacks = new List<ElementCriteria>();

        // Best: AutomationId
        if (!string.IsNullOrEmpty(automationId))
        {
            primary.AutomationId = automationId;
            primary.ControlType = controlType;

            // Fallback: name + control type
            if (!string.IsNullOrEmpty(name))
            {
                fallbacks.Add(new ElementCriteria
                {
                    Name = name,
                    ControlType = controlType
                });
            }
        }
        else if (!string.IsNullOrEmpty(name))
        {
            // No AutomationId, use Name + ControlType
            primary.Name = name;
            primary.ControlType = controlType;

            if (!string.IsNullOrEmpty(className))
            {
                fallbacks.Add(new ElementCriteria
                {
                    ClassName = className,
                    ControlType = controlType
                });
            }
        }
        else
        {
            // Worst case: className + control type
            primary.ClassName = className;
            primary.ControlType = controlType;
        }

        // R2-17: don't emit a primary selector whose only constraint is an unusable ControlType
        // (null/empty/"Unknown") with no other discriminator. Fall back to IndexPath if available,
        // otherwise flag the selector as low-confidence.
        var hasUsableControlType = !string.IsNullOrEmpty(controlType)
            && !string.Equals(controlType, "Unknown", StringComparison.OrdinalIgnoreCase);
        var hasDiscriminator = !string.IsNullOrEmpty(primary.AutomationId)
            || !string.IsNullOrEmpty(primary.Name)
            || !string.IsNullOrEmpty(primary.ClassName)
            || hasUsableControlType;

        var selector = new ElementSelector
        {
            Element = primary,
            Fallbacks = fallbacks.Count > 0 ? fallbacks : null
        };

        if (!hasDiscriminator)
        {
            var indexPath = TryGetIndexPath(element);
            if (indexPath is not null)
            {
                primary.IndexPath = indexPath;
            }
            else
            {
                selector.LowConfidence = true;
            }
        }

        // R2-12: validate the chosen primary selector against the live tree. The tree may be
        // unavailable (e.g. element detached), so swallow failures and only flag when we get a
        // definitive non-unique result.
        try
        {
            var (isUnique, matchCount) = ValidateSelectorUniqueness(primary);
            selector.MatchCount = matchCount;
            if (!isUnique)
            {
                selector.NonUnique = true;
            }
        }
        catch
        {
            // Live tree unavailable; leave uniqueness flags unset.
        }

        return selector;
    }

    private static int[]? TryGetIndexPath(AutomationElement element)
    {
        try
        {
            var path = new List<int>();
            var current = element;
            var parent = current.Parent;
            while (parent is not null)
            {
                var children = parent.FindAllChildren();
                var index = Array.FindIndex(children, c => Equals(c, current));
                if (index < 0)
                {
                    return null;
                }
                path.Insert(0, index);
                current = parent;
                parent = current.Parent;
            }
            return path.Count > 0 ? path.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    public bool ValidateSelector(ElementSelector selector)
    {
        var element = _uiaAdapter.ResolveSelector(selector);
        return element is not null;
    }

    public (bool isUnique, int matchCount) ValidateSelectorUniqueness(ElementCriteria criteria)
    {
        // Guard: criteria with no concrete constraints would match the entire tree, which is
        // never a meaningful "unique" match. Treat as non-unique with zero matches.
        if (string.IsNullOrEmpty(criteria.AutomationId)
            && string.IsNullOrEmpty(criteria.Name)
            && string.IsNullOrEmpty(criteria.ControlType)
            && string.IsNullOrEmpty(criteria.ClassName)
            && criteria.IndexPath is null)
        {
            return (false, 0);
        }

        var elements = _uiaAdapter.FindElements(criteria);
        return (elements.Count == 1, elements.Count);
    }

    public List<(ElementSelector selector, string strategy, int stability)> RankSelectors(AutomationElement element)
    {
        var results = new List<(ElementSelector selector, string strategy, int stability)>();
        var automationId = element.Properties.AutomationId.ValueOrDefault;
        var name = element.Properties.Name.ValueOrDefault;
        var controlType = element.Properties.ControlType.ValueOrDefault.ToString();
        var className = element.Properties.ClassName.ValueOrDefault;

        if (!string.IsNullOrEmpty(automationId))
        {
            var sel = new ElementSelector { Element = new ElementCriteria { AutomationId = automationId } };
            results.Add((sel, "AutomationId", 95));
        }

        if (!string.IsNullOrEmpty(automationId) && !string.IsNullOrEmpty(controlType))
        {
            var sel = new ElementSelector { Element = new ElementCriteria { AutomationId = automationId, ControlType = controlType } };
            results.Add((sel, "AutomationId+ControlType", 98));
        }

        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(controlType))
        {
            var sel = new ElementSelector { Element = new ElementCriteria { Name = name, ControlType = controlType } };
            results.Add((sel, "Name+ControlType", 70));
        }

        if (!string.IsNullOrEmpty(className) && !string.IsNullOrEmpty(controlType))
        {
            var sel = new ElementSelector { Element = new ElementCriteria { ClassName = className, ControlType = controlType } };
            results.Add((sel, "ClassName+ControlType", 40));
        }

        return results.OrderByDescending(r => r.stability).ToList();
    }
}
