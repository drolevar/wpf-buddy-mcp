using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.IntegrationTests;

/// <summary>Unit tests for the cheap structural fingerprint used by stability/watch polling (EFF-M7).</summary>
public class FingerprintTests
{
    [Fact]
    public void SameTree_ProducesSameFingerprint()
    {
        var a = new List<UiElement> { new() { AutomationId = "x", ControlType = "Button", Name = "OK", IsEnabled = true } };
        var b = new List<UiElement> { new() { AutomationId = "x", ControlType = "Button", Name = "OK", IsEnabled = true } };

        Assert.Equal(UiaAdapter.Fingerprint(a), UiaAdapter.Fingerprint(b));
    }

    [Fact]
    public void ChangedProperty_ProducesDifferentFingerprint()
    {
        var a = new List<UiElement> { new() { AutomationId = "x", ControlType = "Button", Name = "OK", IsEnabled = true } };
        var changed = new List<UiElement> { new() { AutomationId = "x", ControlType = "Button", Name = "OK", IsEnabled = false } };

        Assert.NotEqual(UiaAdapter.Fingerprint(a), UiaAdapter.Fingerprint(changed));
    }
}
