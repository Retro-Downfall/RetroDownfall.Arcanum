using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Recovery parks a crashed sweep, and an operator's retry is parked the same way, by the Pure Park Handler, which has no dependencies and so
// cannot count. The park is counted where the worker readmits the sweep, once, so a crash shows in arcanum.long_rest.sweep.parked as it does in
// the sweep's own history. A park the worker stamped itself was counted when it was stamped, and is not counted again at its readmission.
public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData("crash")]
    [InlineData("retry")]
    public async Task A_park_by_recovery_is_counted_where_the_worker_readmits_it_and_nowhere_before(string origin)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        using AsyncFlowMetricCapture metrics = new(ParkedMetric, ReadmittedMetric);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation AND LeaseOwner IS NOT NULL;",
            ("$operation", f.Operation.ToString("N")));

        LongRunningOperationReconciler reconciler = Reconciler(f);

        _ = await reconciler.ReconcileNowAsync("recovery");

        if (origin == "retry")
        {
            LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

            Assert.True(await f.Operations.ResetForRetryAsync(f.Operation, parked.Revision, DateTimeOffset.UtcNow));

            _ = await reconciler.ReconcileNowAsync("retry-recovery");
        }

        string expected = origin == "crash" ? LongRestParkCodes.Interrupted : LongRestParkCodes.ReadmitRequested;

        Assert.Equal(expected, (await f.Operations.GetAsync(f.Operation))!.TerminalErrorCode);

        // The handler parked it and counted nothing; the worker has not yet seen it.
        Assert.Equal(0L, metrics.Sum(ParkedMetric));

        Assert.Equal(0L, metrics.Sum(ReadmittedMetric));

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, progress.Phase);

        Assert.Equal(1L, metrics.Sum(ReadmittedMetric));

        // Counted once, as a park the sweep resumes from.
        Assert.Equal(1L, metrics.Sum(ParkedMetric, "class", "resume_later"));

        Assert.Equal(1L, metrics.Sum(ParkedMetric));
    }
}
