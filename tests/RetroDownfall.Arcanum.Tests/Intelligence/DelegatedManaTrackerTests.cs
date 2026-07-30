using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class DelegatedManaTrackerTests
{
    [Fact]
    public void RecordUsage_TracksProviderReportedTokensAndCost()
    {
        DelegatedManaTracker tracker = new(
            maxTokens: 1_000,
            maxCostUsd: 1.00m,
            maxTurns: 3);

        tracker.BeginModelCall();
        tracker.RecordUsage(
            new UsageDetails
            {
                InputTokenCount = 300,
                OutputTokenCount = 200,
                TotalTokenCount = 500,
            },
            costUsd: 0.25m);

        DelegatedManaUsage usage = tracker.GetUsage();

        Assert.Equal(500, usage.Tokens);
        Assert.Equal(0.25m, usage.CostUsd);
        Assert.Equal(1, usage.ModelCalls);
        Assert.False(usage.Exhausted);
    }

    [Fact]
    public void RecordUsage_WhenTokenCeilingExceeded_ThrowsBudgetExhausted()
    {
        DelegatedManaTracker tracker = new(
            maxTokens: 1_000,
            maxCostUsd: null,
            maxTurns: 3);

        tracker.BeginModelCall();

        BudgetExhaustedException exception = Assert.Throws<BudgetExhaustedException>(
            () => tracker.RecordUsage(
                new UsageDetails
                {
                    InputTokenCount = 900,
                    OutputTokenCount = 101,
                    TotalTokenCount = 1_001,
                },
                costUsd: 0m));

        Assert.Equal(DelegatedBudgetExhaustionReason.Tokens, exception.Reason);
        Assert.Equal(1_001, exception.Usage.Tokens);
        Assert.True(tracker.GetUsage().Exhausted);
    }

    [Fact]
    public void BeginModelCall_WhenTurnCeilingExceeded_ThrowsBeforeProviderCall()
    {
        DelegatedManaTracker tracker = new(
            maxTokens: null,
            maxCostUsd: 1.00m,
            maxTurns: 1);

        tracker.BeginModelCall();

        BudgetExhaustedException exception = Assert.Throws<BudgetExhaustedException>(
            tracker.BeginModelCall);

        Assert.Equal(DelegatedBudgetExhaustionReason.Turns, exception.Reason);
        Assert.Equal(1, exception.Usage.ModelCalls);
    }

    [Fact]
    public async Task RecordUsage_IsThreadSafe()
    {
        DelegatedManaTracker tracker = new(
            maxTokens: 100_000,
            maxCostUsd: 100m,
            maxTurns: 1_000);

        Task[] writes = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() =>
            {
                tracker.BeginModelCall();
                tracker.RecordUsage(
                    new UsageDetails
                    {
                        InputTokenCount = 2,
                        OutputTokenCount = 3,
                        TotalTokenCount = 5,
                    },
                    costUsd: 0.01m);
            }))
            .ToArray();

        await Task.WhenAll(writes);

        DelegatedManaUsage usage = tracker.GetUsage();

        Assert.Equal(500, usage.Tokens);
        Assert.Equal(1.00m, usage.CostUsd);
        Assert.Equal(100, usage.ModelCalls);
    }
}
