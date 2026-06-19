using System.Text.Json.Serialization;

namespace WpfBuddy.Mcp.Server.Models;

public sealed class ElementSelector
{
    [JsonPropertyName("window")]
    public WindowSelector? Window { get; set; }

    [JsonPropertyName("element")]
    public ElementCriteria? Element { get; set; }

    [JsonPropertyName("probe")]
    public ProbeCriteria? Probe { get; set; }

    [JsonPropertyName("fallbacks")]
    public List<ElementCriteria>? Fallbacks { get; set; }

    /// <summary>Set when the primary selector matched more than one (or zero) element in the live tree.</summary>
    [JsonPropertyName("nonUnique")]
    public bool NonUnique { get; set; }

    /// <summary>Number of live-tree matches for the primary selector when uniqueness was validated.</summary>
    [JsonPropertyName("matchCount")]
    public int? MatchCount { get; set; }

    /// <summary>Set when the primary selector lacks a stable constraint (e.g. only ControlType=="Unknown").</summary>
    [JsonPropertyName("lowConfidence")]
    public bool LowConfidence { get; set; }
}

public sealed class WindowSelector
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("process")]
    public string? Process { get; set; }

    [JsonPropertyName("automationId")]
    public string? AutomationId { get; set; }
}

public sealed class ElementCriteria
{
    [JsonPropertyName("automationId")]
    public string? AutomationId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("controlType")]
    public string? ControlType { get; set; }

    [JsonPropertyName("className")]
    public string? ClassName { get; set; }

    [JsonPropertyName("near")]
    public ElementCriteria? Near { get; set; }

    [JsonPropertyName("indexPath")]
    public int[]? IndexPath { get; set; }

    /// <summary>How <see cref="Name"/> is matched: "equals" (default), "contains", "startsWith", or "regex".</summary>
    [JsonPropertyName("nameMatch")]
    public string? NameMatch { get; set; }
}

public sealed class ProbeCriteria
{
    [JsonPropertyName("bindingPath")]
    public string? BindingPath { get; set; }

    [JsonPropertyName("dataContextType")]
    public string? DataContextType { get; set; }
}
