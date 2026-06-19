using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.IntegrationTests;

/// <summary>Verifies generated C# escapes tool-controlled strings (CORR-M5) so they can't break/inject source.</summary>
public class CodeGenEscapingTests
{
    [Fact]
    public void GeneratedTestCode_EscapesQuotesAndNewlines()
    {
        var svc = new RecordingService(new SessionManager());
        var model = new RecordingModel
        {
            Name = "T",
            Steps =
            {
                new RecordingStep
                {
                    Action = "set_value",
                    Value = "a\"b\nc",
                    Selector = new ElementCriteria { AutomationId = "id1" }
                }
            }
        };

        var code = svc.GenerateTestCode(model);

        // The value must appear fully escaped (\" and \n), not as a raw quote/newline that
        // would terminate or inject into the generated string literal.
        Assert.Contains("a\\\"b\\nc", code);
    }
}
