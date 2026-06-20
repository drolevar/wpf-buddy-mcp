using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class DiagnosticsTools
{
    private readonly UiaAdapter _uia;
    private readonly AuditLog _audit;

    public DiagnosticsTools(UiaAdapter uia, AuditLog audit)
    {
        _uia = uia;
        _audit = audit;
    }

    [McpServerTool(Name = "wpf_get_diagnostics", ReadOnly = true), Description("Combined report: missing AutomationIds, duplicates, missing names, selector quality.")]
    public string GetDiagnostics()
    {
        _audit.Record("wpf_get_diagnostics");

        var allElements = _uia.QueryElements();

        var allMissingIds = allElements
            .Where(e => string.IsNullOrEmpty(e.AutomationId) && IsActionable(e.ControlType))
            .Select(e => new { e.Name, e.ControlType, e.ClassName })
            .ToList();
        var missingIds = allMissingIds.Take(20).ToList();

        var duplicateIds = allElements
            .Where(e => !string.IsNullOrEmpty(e.AutomationId))
            .GroupBy(e => e.AutomationId)
            .Where(g => g.Count() > 1)
            .Select(g => new { automationId = g.Key, count = g.Count() })
            .ToList();

        var allMissingNames = allElements
            .Where(e => string.IsNullOrEmpty(e.Name) && string.IsNullOrEmpty(e.AutomationId) && IsActionable(e.ControlType))
            .Select(e => new { e.ControlType, e.ClassName })
            .ToList();
        var missingNames = allMissingNames.Take(20).ToList();

        var report = new
        {
            summary = new
            {
                totalElements = allElements.Count,
                actionableElements = allElements.Count(e => IsActionable(e.ControlType)),
                missingAutomationIds = allMissingIds.Count,
                duplicateAutomationIds = duplicateIds.Count,
                missingAccessibleNames = allMissingNames.Count
            },
            missingAutomationIds = missingIds,
            duplicateAutomationIds = duplicateIds,
            missingAccessibleNames = missingNames
        };

        return JsonSerializer.Serialize(report, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_analyze_automation_quality", ReadOnly = true), Description("Score reflects AutomationId coverage of actionable controls only; also reports missing names and duplicate IDs as recommendations.")]
    public string AnalyzeAutomationQuality()
    {
        _audit.Record("wpf_analyze_automation_quality");

        var allElements = _uia.QueryElements();
        var actionable = allElements.Where(e => IsActionable(e.ControlType)).ToList();

        var withId = actionable.Count(e => !string.IsNullOrEmpty(e.AutomationId));
        var withName = actionable.Count(e => !string.IsNullOrEmpty(e.Name));
        var duplicateIdCount = allElements
            .Where(e => !string.IsNullOrEmpty(e.AutomationId))
            .GroupBy(e => e.AutomationId)
            .Count(g => g.Count() > 1);

        var score = actionable.Count > 0 ? (int)((double)withId / actionable.Count * 100) : 100;
        // R2-7: surface accessible-name coverage (previously computed but ignored) and duplicate-ID
        // count as their own metrics, and state the score basis, so the report reflects more than
        // AutomationId coverage alone instead of over-claiming.
        var accessibilityScore = actionable.Count > 0 ? (int)((double)withName / actionable.Count * 100) : 100;

        var report = new
        {
            qualityScore = score,
            scoreBasis = "AutomationId coverage of actionable controls",
            accessibilityScore,
            grade = score switch
            {
                >= 90 => "A",
                >= 75 => "B",
                >= 60 => "C",
                >= 40 => "D",
                _ => "F"
            },
            totalElements = allElements.Count,
            actionableElements = actionable.Count,
            withAutomationId = withId,
            withAccessibleName = withName,
            duplicateAutomationIds = duplicateIdCount,
            recommendations = GenerateRecommendations(actionable, allElements)
        };

        return JsonSerializer.Serialize(report, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_generate_session_report", ReadOnly = true), Description("Summarize actions, snapshots, diagnostics, failures for current session.")]
    public string GenerateSessionReport()
    {
        _audit.Record("wpf_generate_session_report");
        var entries = _audit.GetEntries();

        var report = new
        {
            totalActions = entries.Count,
            successfulActions = entries.Count(e => e.Result == "success"),
            failedActions = entries.Count(e => e.Result == "error"),
            toolUsage = entries.GroupBy(e => e.Tool).Select(g => new { tool = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList(),
            errors = entries.Where(e => e.Error is not null).Select(e => new { e.Tool, e.Error, e.TimestampUtc }).ToList(),
            timespan = entries.Count > 0
                ? new { start = entries.First().TimestampUtc, end = entries.Last().TimestampUtc }
                : null
        };

        return JsonSerializer.Serialize(report, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_audit_log", ReadOnly = true), Description("Return actions performed in current session.")]
    public string GetAuditLog()
    {
        var entries = _audit.GetEntries();
        return JsonSerializer.Serialize(new { count = entries.Count, entries }, JsonOptions.Default);
    }

    private static List<string> GenerateRecommendations(List<UiElement> actionable, List<UiElement> all)
    {
        var recommendations = new List<string>();

        var missingIdCount = actionable.Count(e => string.IsNullOrEmpty(e.AutomationId));
        if (missingIdCount > 0)
            recommendations.Add($"Add AutomationId to {missingIdCount} actionable controls for stable test automation.");

        var missingNameCount = actionable.Count(e => string.IsNullOrEmpty(e.Name) && string.IsNullOrEmpty(e.AutomationId));
        if (missingNameCount > 0)
            recommendations.Add($"Add accessible names to {missingNameCount} controls for accessibility compliance.");

        var duplicates = all
            .Where(e => !string.IsNullOrEmpty(e.AutomationId))
            .GroupBy(e => e.AutomationId)
            .Count(g => g.Count() > 1);
        if (duplicates > 0)
            recommendations.Add($"Fix {duplicates} duplicate AutomationId values for reliable selector resolution.");

        if (recommendations.Count == 0)
            recommendations.Add("Automation quality looks good. All actionable controls have identifiers.");

        return recommendations;
    }

    // R2-6: delegate to the shared catalog so the actionable list stays unified across files.
    private static bool IsActionable(string? controlType) => ControlTypeCatalog.IsInteractive(controlType);
}
