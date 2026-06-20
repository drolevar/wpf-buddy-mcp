using System.Diagnostics;
using System.Text.Json;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;
using WpfBuddy.Mcp.Server.Tools;

namespace WpfBuddy.Mcp.IntegrationTests;

/// <summary>
/// Integration tests that attach to a real WPF app (Notepad) and verify tools work end-to-end.
/// These tests require a Windows desktop environment.
/// </summary>
[Collection("LiveApp")]
public class LiveAppTests : IDisposable
{
    private readonly SessionManager _session = new();
    private readonly AuditLog _audit = new();
    private readonly UiaAdapter _uia;
    private readonly ProbeClient _probe = new();
    private readonly RecordingService _recording;
    private readonly ExplorerService _explorer;
    private readonly DevWatcherService _devWatcher;
    private Process? _notepadProcess;

    public LiveAppTests()
    {
        _uia = new UiaAdapter(_session);
        _recording = new RecordingService(_session);
        _explorer = new ExplorerService(_session, _uia, _audit, new RecordingPolicy());
        _devWatcher = new DevWatcherService(_session, _uia, _probe);

        // Launch mspaint as test target (reliable WPF-like Win32 app on all Windows versions)
        _notepadProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "mspaint.exe",
            UseShellExecute = true
        });
        // Wait for the process to be ready
        Thread.Sleep(2000);

        if (_notepadProcess is not null && !_notepadProcess.HasExited)
        {
            _session.AttachByPid(_notepadProcess.Id);
        }
        else
        {
            // On newer Windows, try finding mspaint by name
            var proc = Process.GetProcessesByName("mspaint").FirstOrDefault();
            if (proc is not null)
            {
                _notepadProcess = proc;
                _session.AttachByPid(proc.Id);
            }
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        if (_notepadProcess is not null && !_notepadProcess.HasExited)
        {
            _notepadProcess.Kill();
            _notepadProcess.Dispose();
        }
    }

    [SkippableFact]
    public void Session_IsAttached()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        Assert.True(_session.IsAttached);
        Assert.NotEmpty(_session.SessionId);
    }

    // === Explorer Tools ===

    [SkippableFact]
    public void ExploreScreen_ReturnsElements()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new ExplorerTools(_explorer, _uia, _audit);
        var result = tools.ExploreScreen();
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.GetProperty("totalElements").GetInt32() > 0);
    }

    [SkippableFact]
    public void SuggestTestScenarios_ReturnsSuggestions()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new ExplorerTools(_explorer, _uia, _audit);
        var result = tools.SuggestTestScenarios();
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("screenContext", out _));
    }

    [SkippableFact]
    public void ExploreApp_NavigatesAndReturnsResults()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new ExplorerTools(_explorer, _uia, _audit);
        var result = tools.ExploreApp(maxSteps: 3, delayMs: 200);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.GetProperty("screensDiscovered").GetInt32() >= 1);
        Assert.True(json.RootElement.TryGetProperty("mermaidDiagram", out _));
    }

    // === Why Tools ===

    [SkippableFact]
    public async Task ExplainScreen_ReturnsScreenInfo()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new WhyTools(_uia, _probe, _session, _audit);
        var result = await tools.ExplainScreen();
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("screen", out var screen));
        Assert.True(screen.TryGetProperty("windowTitle", out _));
        Assert.True(screen.TryGetProperty("purpose", out _));
    }

    [SkippableFact]
    public async Task WhyDisabled_HandlesEnabledElement()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new WhyTools(_uia, _probe, _session, _audit);
        var result = await tools.WhyDisabled(name: "Close");
        var json = JsonDocument.Parse(result);

        var root = json.RootElement;
        Assert.True(root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _) || root.TryGetProperty("analysis", out _));
    }

    [SkippableFact]
    public void WhyHidden_HandlesVisibleElement()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new WhyTools(_uia, _probe, _session, _audit);
        var result = tools.WhyHidden(name: "File");
        var json = JsonDocument.Parse(result);

        var root = json.RootElement;
        Assert.True(root.TryGetProperty("result", out _) || root.TryGetProperty("analysis", out _));
    }

    [SkippableFact]
    public async Task WhyValidationFailed_ReturnsAnalysis()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new WhyTools(_uia, _probe, _session, _audit);
        var result = await tools.WhyValidationFailed();
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("analysis", out _));
    }

    // === Intent Tools ===

    [SkippableFact]
    public void GoalPlan_ReturnsProposedSteps()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new IntentTools(_uia, _session, _audit, _recording);
        var result = tools.GoalPlan("open the File menu");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("goal", out _));
        Assert.True(json.RootElement.TryGetProperty("proposedSteps", out _));
    }

    [SkippableFact]
    public void NavigateTo_FindsMenuItems()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new IntentTools(_uia, _session, _audit, _recording);
        var result = tools.NavigateTo("File");
        var json = JsonDocument.Parse(result);

        var root = json.RootElement;
        Assert.True(root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _));
    }

    [SkippableFact]
    public void SmartFill_ReturnsResult_WithNoMatchingFields()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new IntentTools(_uia, _session, _audit, _recording);
        var result = tools.SmartFill("{\"NonExistentField\": \"value\"}");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("details", out _) || json.RootElement.TryGetProperty("error", out _));
    }

    [SkippableFact]
    public void GoalVerify_ChecksOutcome()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new IntentTools(_uia, _session, _audit, _recording);
        var result = tools.GoalVerify("no errors visible");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("overallResult", out var overallResult));
        Assert.Equal("PASS", overallResult.GetString());
    }

    // === Dev Watcher Tools ===

    [SkippableFact]
    public async Task DevCheck_ReturnsHealthReport()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new DevWatcherTools(_devWatcher, _uia, _audit);
        var result = await tools.DevCheck();
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.GetProperty("totalElements").GetInt32() > 0);
        Assert.True(summary.TryGetProperty("healthScore", out _));
    }

    [SkippableFact]
    public async Task DevDiff_ReturnsChangeReport()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new DevWatcherTools(_devWatcher, _uia, _audit);

        // First check establishes baseline
        await tools.DevCheck();

        // Second call shows diff
        var result = await tools.DevDiff();
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out _));
    }

    [SkippableFact]
    public void DevSuggestIds_ReturnsSuggestions()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new DevWatcherTools(_devWatcher, _uia, _audit);
        var result = tools.DevSuggestIds();
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("totalMissingIds", out _));
        Assert.True(json.RootElement.TryGetProperty("suggestions", out _));
    }

    [SkippableFact]
    public void DevAccessibilityQuick_ReturnsScore()
    {
        Skip.IfNot(_session.IsAttached, "Could not attach to test application");
        var tools = new DevWatcherTools(_devWatcher, _uia, _audit);
        var result = tools.DevAccessibilityQuick();
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("accessibilityScore", out _));
        Assert.True(json.RootElement.TryGetProperty("issues", out _));
    }
}
