using ModelContextProtocol;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

/// <summary>
/// Raises a tool failure as an MCP error (the SDK turns the thrown McpException into a
/// CallToolResponse{IsError=true} with the message preserved) AND records the failure against the
/// in-flight tool's audit entry (CORR-M3) so session/diagnostics reports reflect real outcomes.
/// Usage: <c>throw ToolError.Fail("Element not found.");</c>
/// </summary>
public static class ToolError
{
    public static McpException Fail(string message)
    {
        AuditLog.MarkCurrentFailure(message);
        return new McpException(message);
    }
}
