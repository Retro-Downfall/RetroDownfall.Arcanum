using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// The cost guard of decision reuse. Paid adjudication is the expensive call, and k memories that name one another are one question.
public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    public async Task Mutual_neighbours_cost_one_adjudication(int sourceCount)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, sourceCount: sourceCount,
            sourceText: index => $"alpha beta{index} gamma{index} note");

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = f.Handler.LastBody!.Contains("long_rest_adjudication", StringComparison.Ordinal)
                ? "{\"kind\":\"NoChange\",\"survivorVersionId\":null}" : "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        using AsyncFlowMetricCapture metrics = new("arcanum.long_rest.sweep.reused_decisions");

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        // Every memory has the same neighbours, so every memory's group is the same set: one request, not one per memory.
        Assert.Equal(1, f.Payloads.Count(static payload => payload.Kind == LongRestProviderPayloadKind.Adjudication));

        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_adjudications;"));

        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3;"));

        Assert.Equal(sourceCount - 1L, metrics.Sum("arcanum.long_rest.sweep.reused_decisions"));

        Assert.Equal(0L, await UnsettledEffectsAsync(f));
    }
}
