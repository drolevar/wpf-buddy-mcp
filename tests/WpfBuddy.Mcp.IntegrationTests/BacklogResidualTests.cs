using ModelContextProtocol.Protocol;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;
using WpfBuddy.Mcp.Server.Tools;

namespace WpfBuddy.Mcp.IntegrationTests;

/// <summary>
/// Covers the three test-coverage gaps the 2026-06-19 verification sweep flagged:
/// TEST-SHOT (screenshots return image content), TEST-SELMATCH (selector name matching),
/// TEST-DIFF (snapshot diff stable keying).
/// </summary>
public class BacklogResidualTests
{
    // TEST-SHOT: the screenshot tools must deliver a PNG as MCP *image* content (CORR-H3), not text,
    // so the model can actually view it.
    [Fact]
    public void Screenshot_ImageResult_IsImageContent()
    {
        var png = new byte[] { 1, 2, 3, 4 };
        var resp = ScreenshotTools.ImageResult(png);

        Assert.False(resp.IsError);
        var content = Assert.Single(resp.Content);
        Assert.Equal("image", content.Type);
        Assert.Equal("image/png", content.MimeType);
        Assert.Equal(Convert.ToBase64String(png), content.Data);
    }

    // TEST-SELMATCH: name matching honors equals (default) / contains / startsWith / regex (FUNC-M3),
    // case-insensitively, and never throws on a bad regex.
    [Theory]
    [InlineData("Save", "Save", "equals", true)]
    [InlineData("Save", "save", null, true)]            // null mode → equals, case-insensitive
    [InlineData("Save", "Sav", "equals", false)]
    [InlineData("Save As", "ave", "contains", true)]
    [InlineData("Save As", "xyz", "contains", false)]
    [InlineData("Save As", "Sav", "startsWith", true)]
    [InlineData("Save As", "As", "startsWith", false)]
    [InlineData("Item 42", "Item \\d+", "regex", true)]
    [InlineData("Item AB", "Item \\d+", "regex", false)]
    [InlineData("anything", "(unclosed", "regex", false)] // invalid regex → false, not an exception
    public void MatchesName_HonorsMode(string actual, string needle, string? mode, bool expected)
    {
        Assert.Equal(expected, UiaAdapter.MatchesName(actual, needle, mode));
    }

    // TEST-DIFF: diffing keys elements by a STABLE identity (AutomationId when present, else a
    // structural path) — CORR-H4 — so an unchanged tree yields no add/remove and a moved element with
    // an AutomationId keeps the same key.
    [Fact]
    public void KeyElements_AutomationId_IsPositionStable()
    {
        var before = new List<UiElement>
        {
            new() { AutomationId = "btnSave", ControlType = "Button", Name = "Save" },
            new() { ControlType = "Text", Name = "Label" },
        };
        // Same element, moved to a different position (and a sibling inserted before it).
        var after = new List<UiElement>
        {
            new() { ControlType = "Text", Name = "Label" },
            new() { ControlType = "Text", Name = "Extra" },
            new() { AutomationId = "btnSave", ControlType = "Button", Name = "Save" },
        };

        var beforeKeys = SnapshotTools.KeyElements(before).Keys;
        var afterKeys = SnapshotTools.KeyElements(after).Keys;

        // The AutomationId-keyed element survives the move (no spurious add/remove for it).
        Assert.Contains("aid:btnSave", beforeKeys);
        Assert.Contains("aid:btnSave", afterKeys);
    }

    [Fact]
    public void KeyElements_IdenticalTree_ProducesIdenticalKeys()
    {
        static List<UiElement> Tree() =>
        [
            new() { AutomationId = "a", ControlType = "Button", Name = "A" },
            new() { ControlType = "Text", Name = "T", Children = [ new() { ControlType = "Text", Name = "child" } ] },
        ];

        var k1 = SnapshotTools.KeyElements(Tree()).Keys.OrderBy(k => k).ToList();
        var k2 = SnapshotTools.KeyElements(Tree()).Keys.OrderBy(k => k).ToList();

        Assert.Equal(k1, k2);   // unchanged tree → identical key sets → empty diff
    }
}
