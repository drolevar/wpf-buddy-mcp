using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class ReportingTools
{
    private readonly UiaAdapter _uia;
    private readonly SessionManager _session;
    private readonly AuditLog _audit;

    public ReportingTools(UiaAdapter uia, SessionManager session, AuditLog audit)
    {
        _uia = uia;
        _session = session;
        _audit = audit;
    }

    [McpServerTool(Name = "wpf_generate_testability_report", ReadOnly = true), Description("Analyze current window and produce testability report with scores.")]
    public string GenerateTestabilityReport()
    {
        _audit.Record("wpf_generate_testability_report");
        var window = _session.ActiveWindow;
        if (window is null)
            return JsonSerializer.Serialize(new { error = "No window attached." }, JsonOptions.Default);

        var allElements = _uia.QueryElements();
        var total = allElements.Count;
        var withAutomationId = allElements.Count(e => !string.IsNullOrEmpty(e.AutomationId));
        var withName = allElements.Count(e => !string.IsNullOrEmpty(e.Name));
        var interactive = allElements.Count(e => IsInteractive(e.ControlType));
        var interactiveWithId = allElements.Count(e => IsInteractive(e.ControlType) && !string.IsNullOrEmpty(e.AutomationId));
        var offscreen = allElements.Count(e => e.IsOffscreen);

        var automationIdCoverage = total > 0 ? (double)withAutomationId / total * 100 : 0;
        var interactiveCoverage = interactive > 0 ? (double)interactiveWithId / interactive * 100 : 0;

        var overallScore = (automationIdCoverage * 0.5 + interactiveCoverage * 0.5);

        return JsonSerializer.Serialize(new
        {
            windowTitle = window.Title,
            totalElements = total,
            scores = new
            {
                overall = Math.Round(overallScore, 1),
                automationIdCoverage = Math.Round(automationIdCoverage, 1),
                interactiveElementCoverage = Math.Round(interactiveCoverage, 1)
            },
            details = new
            {
                elementsWithAutomationId = withAutomationId,
                elementsWithName = withName,
                interactiveElements = interactive,
                interactiveWithAutomationId = interactiveWithId,
                offscreenElements = offscreen
            },
            recommendations = BuildRecommendations(automationIdCoverage, interactiveCoverage)
        }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_generate_diagnostics_report", ReadOnly = true), Description("Generate full diagnostics report including audit log, session, and UI state.")]
    public string GenerateDiagnosticsReport()
    {
        _audit.Record("wpf_generate_diagnostics_report");
        var status = _session.GetStatus();
        var auditEntries = _audit.GetEntries();

        UiSnapshot? snapshot = null;
        try { snapshot = _uia.CaptureSnapshot(maxDepth: 3); } catch { }

        return JsonSerializer.Serialize(new
        {
            generatedAtUtc = DateTime.UtcNow,
            session = status,
            auditLog = new
            {
                totalEntries = auditEntries.Count,
                recentEntries = auditEntries.TakeLast(20).ToList()
            },
            uiState = snapshot is null ? null : new
            {
                windowTitle = snapshot.Window?.Title,
                elementCount = snapshot.Diagnostics?.TotalElements ?? 0,
                capturedAtUtc = snapshot.TimestampUtc
            }
        }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_export_artifacts", ReadOnly = true), Description("Bundle session artifacts (audit log, last snapshot) as JSON.")]
    public string ExportArtifacts()
    {
        _audit.Record("wpf_export_artifacts");

        var artifacts = new
        {
            exportedAtUtc = DateTime.UtcNow,
            session = _session.GetStatus(),
            auditLog = _audit.GetEntries(),
            snapshot = (object?)null
        };

        try
        {
            var snap = _uia.CaptureSnapshot(maxDepth: 3);
            return JsonSerializer.Serialize(new
            {
                exportedAtUtc = DateTime.UtcNow,
                session = _session.GetStatus(),
                auditLog = _audit.GetEntries(),
                snapshot = snap
            }, JsonOptions.Default);
        }
        catch
        {
            return JsonSerializer.Serialize(artifacts, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_import_artifacts", ReadOnly = true), Description("Load artifacts JSON bundle and return summary.")]
    public string ImportArtifacts([Description("Artifacts bundle as a JSON string (as produced by wpf_export_artifacts); parsed read-only to summarize its session, auditLog, and snapshot contents.")] string artifactsJson)
    {
        _audit.Record("wpf_import_artifacts");
        try
        {
            var doc = JsonDocument.Parse(artifactsJson);
            var root = doc.RootElement;

            var hasSession = root.TryGetProperty("session", out _);
            var hasAuditLog = root.TryGetProperty("auditLog", out var auditEl);
            var hasSnapshot = root.TryGetProperty("snapshot", out _);

            int auditCount = 0;
            if (hasAuditLog && auditEl.ValueKind == JsonValueKind.Array)
                auditCount = auditEl.GetArrayLength();

            return JsonSerializer.Serialize(new
            {
                result = "imported",
                hasSession,
                hasAuditLog,
                auditEntryCount = auditCount,
                hasSnapshot
            }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_compare_reports", ReadOnly = true), Description("Compare two testability reports and highlight changes.")]
    public string CompareReports([Description("Baseline testability report as a JSON string (the 'before' report, as produced by wpf_generate_testability_report). Required.")] string reportAJson, [Description("Comparison testability report as a JSON string (the 'after' report). Required. Deltas are computed as report B minus report A.")] string reportBJson)
    {
        _audit.Record("wpf_compare_reports");
        try
        {
            var a = JsonDocument.Parse(reportAJson);
            var b = JsonDocument.Parse(reportBJson);

            double scoreA = 0, scoreB = 0;
            int elemA = 0, elemB = 0;

            if (a.RootElement.TryGetProperty("scores", out var scoresA) && scoresA.TryGetProperty("overall", out var oa))
                scoreA = oa.GetDouble();
            if (b.RootElement.TryGetProperty("scores", out var scoresB) && scoresB.TryGetProperty("overall", out var ob))
                scoreB = ob.GetDouble();
            if (a.RootElement.TryGetProperty("totalElements", out var teA))
                elemA = teA.GetInt32();
            if (b.RootElement.TryGetProperty("totalElements", out var teB))
                elemB = teB.GetInt32();

            return JsonSerializer.Serialize(new
            {
                scoreDelta = Math.Round(scoreB - scoreA, 1),
                elementDelta = elemB - elemA,
                improved = scoreB > scoreA,
                reportA = new { score = scoreA, elements = elemA },
                reportB = new { score = scoreB, elements = elemB }
            }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Default);
        }
    }

    private static bool IsInteractive(string? controlType) => controlType is
        "Button" or "TextBox" or "Edit" or "ComboBox" or "CheckBox"
        or "RadioButton" or "MenuItem" or "Tab" or "TabItem" or "ListItem"
        or "DataItem" or "TreeItem" or "Slider" or "Hyperlink";

    private static List<string> BuildRecommendations(double automationIdCoverage, double interactiveCoverage)
    {
        var recs = new List<string>();
        if (automationIdCoverage < 50) recs.Add("Low AutomationId coverage. Add x:Name or AutomationProperties.AutomationId to controls.");
        if (interactiveCoverage < 80) recs.Add("Many interactive elements lack AutomationId. Prioritize buttons, text fields, and menus.");
        if (automationIdCoverage >= 80 && interactiveCoverage >= 80) recs.Add("Good testability. Consider adding HelpText properties for accessibility.");
        return recs;
    }
}
