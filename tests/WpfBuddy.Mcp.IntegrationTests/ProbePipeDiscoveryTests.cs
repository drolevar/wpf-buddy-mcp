using WpfBuddy.Mcp.Probe;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.IntegrationTests;

/// <summary>Verifies probe pipe auto-discovery (EFF-M4) finds a running probe.</summary>
public class ProbePipeDiscoveryTests
{
    [Fact]
    public void EnumerateProbePipes_FindsRunningProbe()
    {
        var pipe = "wpfbuddy-mcp-probe-test-" + Guid.NewGuid().ToString("N");
        using var host = ProbeHost.Start(pipe);

        // The server pipe is created asynchronously by the listen loop; poll briefly.
        var found = false;
        for (var i = 0; i < 20 && !found; i++)
        {
            found = ProbeClient.EnumerateProbePipes().Contains(pipe);
            if (!found) Thread.Sleep(50);
        }

        Assert.True(found, "EnumerateProbePipes did not find the started probe pipe.");
    }
}
