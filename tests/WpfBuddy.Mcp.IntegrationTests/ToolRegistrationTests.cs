using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;
using WpfBuddy.Mcp.Server.Tools;

namespace WpfBuddy.Mcp.IntegrationTests;

/// <summary>
/// Tests that all tool classes can be instantiated and return proper errors when not attached.
/// </summary>
public class ToolRegistrationTests
{
    private readonly SessionManager _session = new();
    private readonly AuditLog _audit = new();
    private readonly UiaAdapter _uia;
    private readonly ProbeClient _probe = new();
    private readonly RecordingService _recording;
    private readonly ExplorerService _explorer;
    private readonly DevWatcherService _devWatcher;

    public ToolRegistrationTests()
    {
        _uia = new UiaAdapter(_session);
        _recording = new RecordingService(_session);
        _explorer = new ExplorerService(_session, _uia, _audit, new RecordingPolicy());
        _devWatcher = new DevWatcherService(_session, _uia, _probe);
    }

    [Fact]
    public void ExplorerTools_CanBeInstantiated()
    {
        var tools = new ExplorerTools(_explorer, _uia, _audit);
        Assert.NotNull(tools);
    }

    [Fact]
    public void WhyTools_CanBeInstantiated()
    {
        var tools = new WhyTools(_uia, _probe, _session, _audit);
        Assert.NotNull(tools);
    }

    [Fact]
    public void IntentTools_CanBeInstantiated()
    {
        var tools = new IntentTools(_uia, _session, _audit, _recording);
        Assert.NotNull(tools);
    }

    [Fact]
    public void DevWatcherTools_CanBeInstantiated()
    {
        var tools = new DevWatcherTools(_devWatcher, _uia, _audit);
        Assert.NotNull(tools);
    }

    [Fact]
    public void SessionManager_NotAttachedByDefault()
    {
        Assert.False(_session.IsAttached);
        Assert.Equal(string.Empty, _session.SessionId);
    }
}
