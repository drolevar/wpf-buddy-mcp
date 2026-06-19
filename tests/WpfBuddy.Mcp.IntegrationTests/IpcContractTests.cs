using WpfBuddy.Mcp.Probe;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.IntegrationTests;

/// <summary>
/// Contract tests for the named-pipe IPC: hosts the real ProbeHost on an in-process pipe
/// and drives it with the real ProbeClient. No live WPF app needed (ping/info don't touch
/// the dispatcher). Covers the BOM-free framing, request/response round-trip, and the
/// concurrency guard added in CORR-M1.
/// </summary>
public class IpcContractTests
{
    private static string NewPipe() => "wpfbuddy-mcp-probe-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Ping_RoundTrips()
    {
        var pipe = NewPipe();
        using var host = ProbeHost.Start(pipe);
        using var client = new ProbeClient();

        Assert.True(await client.ConnectAsync(pipe, 3000));
        var resp = await client.SendAsync("ping");

        Assert.NotNull(resp);
        Assert.Equal("pong", resp!.Result);
    }

    [Fact]
    public async Task Info_ReturnsPid()
    {
        var pipe = NewPipe();
        using var host = ProbeHost.Start(pipe);
        using var client = new ProbeClient();

        Assert.True(await client.ConnectAsync(pipe, 3000));
        var resp = await client.SendAsync("info");

        Assert.NotNull(resp);
        Assert.NotNull(resp!.Data);
        Assert.Contains("pid", resp.Data!);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsError()
    {
        var pipe = NewPipe();
        using var host = ProbeHost.Start(pipe);
        using var client = new ProbeClient();

        Assert.True(await client.ConnectAsync(pipe, 3000));
        var resp = await client.SendAsync("does_not_exist");

        Assert.NotNull(resp);
        Assert.False(string.IsNullOrEmpty(resp!.Error));
    }

    [Fact]
    public async Task ConcurrentSends_DoNotCorruptTheChannel()
    {
        var pipe = NewPipe();
        using var host = ProbeHost.Start(pipe);
        using var client = new ProbeClient();

        Assert.True(await client.ConnectAsync(pipe, 3000));

        // The SemaphoreSlim gate must serialize these so the line-framed pipe never desyncs.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.SendAsync("ping")));

        Assert.All(results, r => Assert.Equal("pong", r?.Result));
    }
}
