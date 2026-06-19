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

    /// <summary>
    /// The single source of truth for an element's candidate selector strategies (R2-16), shared by
    /// <see cref="RankSelectors"/> and the wpf_get_selector_candidates tool so they never diverge.
    /// Base stability reflects strategy robustness only; live uniqueness is folded in by RankSelectors.
    /// Ordered simplest-sufficient first (R2-15): a bare AutomationId outranks AutomationId+ControlType
    /// because, when unique, the extra ControlType constraint is redundant and only adds brittleness.
    /// </summary>
    public List<SelectorCandidate> GenerateCandidates(AutomationElement element)
    {
        var automationId = element.Properties.AutomationId.ValueOrDefault;
        var name = element.Properties.Name.ValueOrDefault;
        var controlType = element.Properties.ControlType.ValueOrDefault.ToString();
        var className = element.Properties.ClassName.ValueOrDefault;

        // Don't constrain on an unusable ControlType (null/empty/"Unknown") — see R2-17.
        var hasUsableControlType = !string.IsNullOrEmpty(controlType)
            && !string.Equals(controlType, "Unknown", StringComparison.OrdinalIgnoreCase);

        var candidates = new List<SelectorCandidate>();

        if (!string.IsNullOrEmpty(automationId))
            candidates.Add(new("AutomationId", new ElementCriteria { AutomationId = automationId }, 98));

        if (!string.IsNullOrEmpty(automationId) && hasUsableControlType)
            candidates.Add(new("AutomationId+ControlType", new ElementCriteria { AutomationId = automationId, ControlType = controlType }, 95));

        if (!string.IsNullOrEmpty(name) && hasUsableControlType)
            candidates.Add(new("Name+ControlType", new ElementCriteria { Name = name, ControlType = controlType }, 70));

        if (!string.IsNullOrEmpty(name))
            candidates.Add(new("Name", new ElementCriteria { Name = name }, 60));

        if (!string.IsNullOrEmpty(className) && hasUsableControlType)
            candidates.Add(new("ClassName+ControlType", new ElementCriteria { ClassName = className, ControlType = controlType }, 40));

        return candidates;
    }

    /// <summary>
    /// Rank candidate selectors for an element. R2-14: each candidate is verified against the live tree
    /// and its match count folded into the score, so a duplicate AutomationId (matchCount &gt; 1) drops
    /// below a unique compound selector instead of always ranking 95-98.
    /// </summary>
    public List<(ElementSelector selector, string strategy, int stability, int matchCount)> RankSelectors(AutomationElement element)
    {
        var ranked = new List<(ElementSelector selector, string strategy, int stability, int matchCount)>();

        foreach (var c in GenerateCandidates(element))
        {
            int matchCount;
            try
            {
                (_, matchCount) = ValidateSelectorUniqueness(c.Criteria);
            }
            catch
            {
                matchCount = -1; // live tree unavailable — fall back to the base score
            }

            var score = ComputeStability(c.BaseStability, matchCount);
            ranked.Add((new ElementSelector { Element = c.Criteria }, c.Strategy, score, matchCount));
        }

        // Highest score first; tie-break toward the simpler selector (fewer constraints) — R2-15.
        return ranked
            .OrderByDescending(r => r.stability)
            .ThenBy(r => ConstraintCount(r.selector.Element))
            .ToList();
    }

    /// <summary>
    /// Map a candidate's base stability and its live match count to a final ranking score (R2-14).
    /// Unique (1) keeps its base score; unverifiable (&lt; 0) trusts the base; non-resolving (0) scores 0;
    /// non-unique (&gt; 1) is forced strictly below EVERY unique selector — including the lowest unique
    /// base (ClassName+ControlType = 40) — so an ambiguous selector can never outrank a unique one.
    /// </summary>
    public static int ComputeStability(int baseStability, int matchCount)
    {
        const int nonUniqueCeiling = 39; // strictly below the lowest unique base stability (40)
        return matchCount switch
        {
            1 => baseStability,                                    // unique & sufficient → full strategy score
            0 => 0,                                                 // does not resolve in the live tree
            < 0 => baseStability,                                  // could not verify → trust the base score
            _ => Math.Max(1, nonUniqueCeiling - (matchCount - 1))  // non-unique → always below any unique selector
        };
    }

    private static int ConstraintCount(ElementCriteria? c)
    {
        if (c is null) return 0;
        var n = 0;
        if (!string.IsNullOrEmpty(c.AutomationId)) n++;
        if (!string.IsNullOrEmpty(c.Name)) n++;
        if (!string.IsNullOrEmpty(c.ControlType)) n++;
        if (!string.IsNullOrEmpty(c.ClassName)) n++;
        return n;
    }
}

/// <summary>A single selector strategy: a human-readable name, its criteria, and a base stability score.</summary>
public sealed record SelectorCandidate(string Strategy, ElementCriteria Criteria, int BaseStability);
