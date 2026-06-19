using ModelContextProtocol;
using WpfBuddy.Mcp.Server.Services;
using WpfBuddy.Mcp.Server.Tools;
using System.Text.Json;

namespace WpfBuddy.Mcp.IntegrationTests;

/// <summary>
/// Tests that tools signal a tool-error when no app is attached. Per the verified #13/#2 contract
/// (see <see cref="McpErrorSemanticsTests"/>), failures are raised by THROWING <see cref="McpException"/>
/// — the SDK turns that into a CallToolResponse{IsError=true} with the message preserved — rather than
/// returning a fake-success JSON payload carrying an "error" field. DevCheck is the deliberate
/// exception: it reports problems (including "not attached") as a structured "issues" list.
/// </summary>
public class UnattachedErrorTests
{
    private readonly SessionManager _session = new();
    private readonly AuditLog _audit = new();
    private readonly UiaAdapter _uia;
    private readonly ProbeClient _probe = new();
    private readonly RecordingService _recording;
    private readonly ExplorerService _explorer;
    private readonly DevWatcherService _devWatcher;

    public UnattachedErrorTests()
    {
        _uia = new UiaAdapter(_session);
        _recording = new RecordingService(_session);
        _explorer = new ExplorerService(_session, _uia, _audit);
        _devWatcher = new DevWatcherService(_session, _uia, _probe);
    }

    private static void AssertSignalsError(McpException ex) =>
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));   // message preserved for the model

    [Fact]
    public void ExploreScreen_ThrowsError_WhenNotAttached()
    {
        var tools = new ExplorerTools(_explorer, _uia, _audit);
        AssertSignalsError(Assert.Throws<McpException>(() => tools.ExploreScreen()));
    }

    [Fact]
    public void ExploreApp_ThrowsError_WhenNotAttached()
    {
        var tools = new ExplorerTools(_explorer, _uia, _audit);
        AssertSignalsError(Assert.Throws<McpException>(() => tools.ExploreApp(maxSteps: 1)));
    }

    [Fact]
    public void SuggestTestScenarios_ThrowsError_WhenNotAttached()
    {
        var tools = new ExplorerTools(_explorer, _uia, _audit);
        AssertSignalsError(Assert.Throws<McpException>(() => tools.SuggestTestScenarios()));
    }

    [Fact]
    public async Task WhyDisabled_ThrowsError_WhenNotAttached()
    {
        var tools = new WhyTools(_uia, _probe, _session, _audit);
        AssertSignalsError(await Assert.ThrowsAsync<McpException>(() => tools.WhyDisabled(name: "Save")));
    }

    [Fact]
    public void WhyHidden_ThrowsError_WhenNotAttached()
    {
        var tools = new WhyTools(_uia, _probe, _session, _audit);
        AssertSignalsError(Assert.Throws<McpException>(() => tools.WhyHidden(name: "Panel")));
    }

    [Fact]
    public async Task WhyValidationFailed_ThrowsError_WhenNotAttached()
    {
        var tools = new WhyTools(_uia, _probe, _session, _audit);
        AssertSignalsError(await Assert.ThrowsAsync<McpException>(() => tools.WhyValidationFailed()));
    }

    [Fact]
    public async Task WhyEmpty_ThrowsError_WhenNotAttached()
    {
        var tools = new WhyTools(_uia, _probe, _session, _audit);
        AssertSignalsError(await Assert.ThrowsAsync<McpException>(() => tools.WhyEmpty(name: "TextBox1")));
    }

    [Fact]
    public async Task ExplainScreen_ThrowsError_WhenNotAttached()
    {
        var tools = new WhyTools(_uia, _probe, _session, _audit);
        AssertSignalsError(await Assert.ThrowsAsync<McpException>(() => tools.ExplainScreen()));
    }

    [Fact]
    public void GoalPlan_ThrowsError_WhenNotAttached()
    {
        var tools = new IntentTools(_uia, _session, _audit, _recording);
        AssertSignalsError(Assert.Throws<McpException>(() => tools.GoalPlan("fill the form and save")));
    }

    [Fact]
    public void GoalExecute_ThrowsError_WhenNotAttached()
    {
        var tools = new IntentTools(_uia, _session, _audit, _recording);
        AssertSignalsError(Assert.Throws<McpException>(() => tools.GoalExecute("save the form")));
    }

    [Fact]
    public void GoalVerify_ThrowsError_WhenNotAttached()
    {
        var tools = new IntentTools(_uia, _session, _audit, _recording);
        AssertSignalsError(Assert.Throws<McpException>(() => tools.GoalVerify("no errors visible")));
    }

    [Fact]
    public void SmartFill_ThrowsError_WhenNotAttached()
    {
        var tools = new IntentTools(_uia, _session, _audit, _recording);
        AssertSignalsError(Assert.Throws<McpException>(() => tools.SmartFill("{\"Name\": \"Test\"}")));
    }

    [Fact]
    public void NavigateTo_ThrowsError_WhenNotAttached()
    {
        var tools = new IntentTools(_uia, _session, _audit, _recording);
        AssertSignalsError(Assert.Throws<McpException>(() => tools.NavigateTo("Settings")));
    }

    [Fact]
    public async Task DevCheck_ReportsIssues_WhenNotAttached()
    {
        // DevCheck deliberately degrades gracefully: it reports "not attached" as a structured issue
        // rather than throwing, so an agent can run a quick health check without a session.
        var tools = new DevWatcherTools(_devWatcher, _uia, _audit);
        var result = await tools.DevCheck();
        var json = JsonDocument.Parse(result);
        Assert.True(json.RootElement.TryGetProperty("issues", out _) ||
                    json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void DevSuggestIds_ThrowsError_WhenNotAttached()
    {
        var tools = new DevWatcherTools(_devWatcher, _uia, _audit);
        AssertSignalsError(Assert.Throws<McpException>(() => tools.DevSuggestIds()));
    }

    [Fact]
    public void DevAccessibilityQuick_ThrowsError_WhenNotAttached()
    {
        var tools = new DevWatcherTools(_devWatcher, _uia, _audit);
        AssertSignalsError(Assert.Throws<McpException>(() => tools.DevAccessibilityQuick()));
    }
}
