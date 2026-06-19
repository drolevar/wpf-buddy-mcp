using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.IntegrationTests;

/// <summary>
/// R2-14: <see cref="SelectorBuilder.ComputeStability"/> must fold the live match count into a
/// selector's score so that an ambiguous (non-unique) selector can never outrank a genuinely unique
/// one — the exact inversion R2-14 exists to prevent. The lowest unique base stability is 40
/// (ClassName+ControlType), so every non-unique score must stay strictly below 40.
/// </summary>
public class SelectorRankingTests
{
    [Theory]
    [InlineData(98, 1, 98)]   // unique AutomationId → full base score
    [InlineData(40, 1, 40)]   // unique ClassName+ControlType → full base score
    [InlineData(98, -1, 98)]  // unverifiable (live tree unavailable) → trust the base score
    [InlineData(98, 0, 0)]    // resolves to nothing in the live tree
    public void ComputeStability_KeepsOrTrustsBase_ForUniqueOrUnverifiable(int baseStability, int matchCount, int expected)
    {
        Assert.Equal(expected, SelectorBuilder.ComputeStability(baseStability, matchCount));
    }

    [Theory]
    [InlineData(98, 2)]
    [InlineData(98, 5)]
    [InlineData(95, 3)]
    [InlineData(40, 2)]
    public void ComputeStability_NonUnique_AlwaysBelowLowestUniqueBase(int baseStability, int matchCount)
    {
        var score = SelectorBuilder.ComputeStability(baseStability, matchCount);
        Assert.True(score < 40, $"non-unique score {score} must be below the lowest unique base (40)");
        Assert.True(score >= 1, "score must stay positive");
    }

    [Fact]
    public void ComputeStability_DuplicateAutomationId_RanksBelowUniqueClassNameSelector()
    {
        // The R2-14 premise: an element with a duplicated AutomationId (matches 2) but a unique
        // ClassName+ControlType. The unique selector must win.
        var duplicateAutomationId = SelectorBuilder.ComputeStability(98, matchCount: 2);
        var uniqueClassName = SelectorBuilder.ComputeStability(40, matchCount: 1);
        Assert.True(duplicateAutomationId < uniqueClassName,
            $"duplicate AutomationId ({duplicateAutomationId}) must rank below unique ClassName+ControlType ({uniqueClassName})");
    }

    [Fact]
    public void ComputeStability_MoreMatches_RanksLower()
    {
        Assert.True(SelectorBuilder.ComputeStability(98, 2) > SelectorBuilder.ComputeStability(98, 5));
    }
}
