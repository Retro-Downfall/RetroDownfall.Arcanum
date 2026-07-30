using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public class TurnBudgetTests
{
    [Fact]
    public void TurnBudget_TryConsumeModelCall_Tracks_Count()
    {
        var budget = new RetroDownfall.Arcanum.Core.Intelligence.TurnBudget(
            new RetroDownfall.Arcanum.Core.Intelligence.TurnLimits(
                MaxModelCalls: 3, MaxToolRounds: 2, MaxToolCalls: 5,
                MaxToolResultTokens: 100, MaxToolResultBytes: 1024,
                MaxElapsedTime: TimeSpan.FromMinutes(5),
                MaxEstimatedCostUsd: 1.0m, MaxReservedCostUsd: 0.5m));

        Assert.True(budget.TryConsumeModelCall(
            new ContextTokenBreakdown
            {
                Provider = "test", Model = "test", Profile = new RetroDownfall.Arcanum.Core.Intelligence.ResolvedModelTokenizationProfile(),
                Components = new System.Collections.ObjectModel.ReadOnlyCollection<RetroDownfall.Arcanum.Core.Intelligence.ContextTokenComponent>(new List<RetroDownfall.Arcanum.Core.Intelligence.ContextTokenComponent>()), InputTokens = 100, ReservedTokens = 10, TotalTokens = 110,
                OverallClassification = RetroDownfall.Arcanum.Core.Intelligence.TokenEstimateClassification.Estimated, SafetyMarginTokens = 5
            }, out var violation));
        Assert.Null(violation);
    }

    [Fact]
    public void TurnBudget_TryConsumeToolRound_Tracks_Rounds()
    {
        var budget = new RetroDownfall.Arcanum.Core.Intelligence.TurnBudget(
            new RetroDownfall.Arcanum.Core.Intelligence.TurnLimits(
                MaxModelCalls: 10, MaxToolRounds: 1, MaxToolCalls: 5,
                MaxToolResultTokens: 100, MaxToolResultBytes: 1024,
                MaxElapsedTime: TimeSpan.FromMinutes(5),
                MaxEstimatedCostUsd: 1.0m, MaxReservedCostUsd: 0.5m));

        Assert.True(budget.TryConsumeToolRound(out var violation));
        Assert.Null(violation);
        Assert.False(budget.TryConsumeToolRound(out violation));
        Assert.NotNull(violation);
    }
}
