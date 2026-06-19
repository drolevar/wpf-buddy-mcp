using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;

namespace WpfBuddy.Mcp.IntegrationTests;

/// <summary>
/// Documents the verified #13 decision (ModelContextProtocol 0.2.0-preview.1):
/// a tool that THROWS yields a CallToolResponse{IsError=true} — a tool-error result the model
/// sees, NOT a JSON-RPC protocol error. A plain exception's message is genericized; a thrown
/// McpException PRESERVES its message. => signal tool failures by throwing McpException.
/// </summary>
public class McpErrorSemanticsTests
{
    private static RequestContext<CallToolRequestParams> Ctx() =>
        new(Substitute.For<IMcpServer>()) { Params = new CallToolRequestParams { Name = "t" } };

    [Fact]
    public async Task ThrownException_YieldsIsErrorResult_NotProtocolError()
    {
        var tool = McpServerTool.Create((Func<string>)(() => throw new InvalidOperationException("boom")));
        var resp = await tool.InvokeAsync(Ctx(), default);   // returns normally — does not propagate
        Assert.True(resp.IsError);
    }

    [Fact]
    public async Task ThrownMcpException_PreservesMessage()
    {
        var tool = McpServerTool.Create((Func<string>)(() => throw new McpException("element-not-found-xyz")));
        var resp = await tool.InvokeAsync(Ctx(), default);
        Assert.True(resp.IsError);
        Assert.Contains("element-not-found-xyz", JsonSerializer.Serialize(resp));
    }
}
