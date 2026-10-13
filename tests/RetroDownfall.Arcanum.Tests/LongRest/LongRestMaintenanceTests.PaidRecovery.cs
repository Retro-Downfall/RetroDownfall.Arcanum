using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Fact]
    public async Task Paid_readmission_by_the_hosted_worker_owns_one_group_for_summary_and_yields_by_parking_for_maintenance()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        // Recovery only parks the interrupted sweep: no provider call and no group belong to it.
        await ParkExpiredSweepAsync(f, "fixture-runtime-recovery");

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0, f.RecordedAdmission.EffectGroupAttempts);

        using CancellationTokenSource stopping = new();

        using LongRestSweepHostedService host = PaidOwnershipHost(f);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IGrimoireClosingOwner? closing = null;

        int groupsAtSend = 0;

        f.Handler.InsideSendWithCancellation = async token =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            if (f.Handler.SendCount != 1) return;

            groupsAtSend = f.RecordedAdmission.EffectGroupAttempts;

            entered.TrySetResult();

            await release.Task.WaitAsync(token);
        };

        Task<Exception?> run = Record.ExceptionAsync(() => host.RunOneAsync(f.Operation, stopping.Token));

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            closing = f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()).Value;

            Task<RetroDownfall.Arcanum.Core.Primitives.Result> drain = f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            Assert.False(drain.IsCompleted);

            release.TrySetResult();

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        }
        finally
        {
            release.TrySetResult();

            await stopping.CancelAsync();

            await run.WaitAsync(TimeSpan.FromSeconds(10));

            if (closing is not null) await closing.DisposeAsync();
        }

        Assert.Equal(1, groupsAtSend);

        Assert.Equal(1, f.RecordedAdmission.EffectGroupAttempts);

        Assert.Equal(1, f.Handler.SendCount);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT sum(PaidCount) FROM long_rest_provider_effects;"));

        // The yield to maintenance is a park under its own free code, never a terminal failure.
        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal("long_rest.maintenance_deferred", parked.TerminalErrorCode);

        Assert.Null(parked.LeaseOwner);
    }

    /// <summary>Expires the fixture worker's lease and runs the real reconciler with the registered pure handler once.</summary>
    internal static async Task ParkExpiredSweepAsync(MaintenanceFixture f, string owner)
    {
        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation AND LeaseOwner IS NOT NULL;",
            ("$operation", f.Operation.ToString("N")));

        LongRunningOperationReconciler reconciler = new(f.Operations, [new LongRestSweepRecoveryHandler()], f.Clock,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync(owner);

        Assert.Equal(1, summary.RequiresAttention);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal("long_rest.interrupted", parked.TerminalErrorCode);

        Assert.Null(parked.LeaseOwner);

        Assert.Null(parked.LeaseExpiresAt);
    }
}
