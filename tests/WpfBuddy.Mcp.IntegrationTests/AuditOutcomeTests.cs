using ModelContextProtocol;
using WpfBuddy.Mcp.Server.Services;
using WpfBuddy.Mcp.Server.Tools;

namespace WpfBuddy.Mcp.IntegrationTests;

/// <summary>
/// CORR-M3: tools call <see cref="AuditLog.Record"/> at their start with an optimistic
/// result="success"; a later failure raised via <see cref="ToolError.Fail"/> must flip that
/// in-flight entry (tracked per async flow) to result="error" with the message, so session and
/// diagnostics reports reflect real outcomes instead of always "success".
/// </summary>
public class AuditOutcomeTests
{
    [Fact]
    public void Record_DefaultsToSuccess()
    {
        var audit = new AuditLog();
        audit.Record("wpf_ok");

        var last = audit.GetEntries().Last();
        Assert.Equal("wpf_ok", last.Tool);
        Assert.Equal("success", last.Result);
        Assert.Null(last.Error);
    }

    [Fact]
    public void ToolError_Fail_MarksInFlightEntryAsError()
    {
        var audit = new AuditLog();
        audit.Record("wpf_demo");                                  // optimistic success + sets current

        McpException? caught = null;
        try { throw ToolError.Fail("boom"); }                      // how every failing tool signals
        catch (McpException e) { caught = e; }

        Assert.NotNull(caught);
        Assert.Equal("boom", caught!.Message);                     // message preserved for the model
        var last = audit.GetEntries().Last();
        Assert.Equal("wpf_demo", last.Tool);
        Assert.Equal("error", last.Result);                        // flipped from success
        Assert.Equal("boom", last.Error);
    }

    [Fact]
    public void MarkCurrentFailure_WithoutInFlightEntry_IsNoOp()
    {
        // No Record on this flow → nothing to mark; must not throw.
        AuditLog.MarkCurrentFailure("orphan");
    }
}
