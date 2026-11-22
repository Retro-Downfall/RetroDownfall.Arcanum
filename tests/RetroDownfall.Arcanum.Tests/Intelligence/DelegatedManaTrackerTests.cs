using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class DelegatedManaTrackerTests
{
    [Fact]
    public void RecordUsage_TracksProviderReportedTokensAndCost()
    {
        DelegatedManaTracker tracker = new();

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

    }

    [Fact]
    public void BeginModelCall_HasNoArbitraryCallCeiling()
    {
        DelegatedManaTracker tracker = new();

        for (int i = 0; i < 100; i++)
        {

            tracker.BeginModelCall();

        }

        Assert.Equal(100, tracker.GetUsage().ModelCalls);

    }

    [Fact]
    public async Task RecordUsage_IsThreadSafe()
    {
        DelegatedManaTracker tracker = new();

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
