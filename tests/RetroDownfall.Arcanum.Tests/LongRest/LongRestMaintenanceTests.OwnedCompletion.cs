using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Fact]
    public async Task Same_fence_registered_recovery_finishes_durable_completed_stages_before_the_final_operation_transition_without_replay()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, exact: true);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        // Interrupt the actual coordinator only at its final operation CAS, after both stage
        // completion and its final checkpoint committed. This preserves an actual Running row.
        await f.H.ExecuteAsync($"CREATE TEMP TRIGGER fixture_owned_completion_crash BEFORE UPDATE OF State ON LongRunningOperations "
            + $"WHEN NEW.Id = '{f.Operation:N}' AND NEW.State = {(int)LongRunningOperationState.Completed} "
            + "AND (SELECT PhaseCode FROM long_rest_discovery_generations WHERE OperationId = NEW.Id) = 6 "
            + "BEGIN SELECT RAISE(ABORT, 'fixture owned completion crash'); END;");

        try
        {
            SqliteException crash = await Assert.ThrowsAsync<SqliteException>(() => f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

            Assert.Equal(19, crash.SqliteErrorCode);

            Assert.Contains("fixture owned completion crash", crash.Message, StringComparison.Ordinal);
        }
        finally
        {
            await f.H.ExecuteAsync("DROP TRIGGER fixture_owned_completion_crash;");
        }

        LongRunningOperation interrupted = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Running, interrupted.State);

        Assert.NotNull(interrupted.LeaseOwner);

        Assert.Null(interrupted.CompletedAt);

        LongRestSweepCheckpoint checkpoint = LongRestSweepHarness.Require(LongRestSweepCheckpointCodec.Decode(f.Operation, interrupted.CheckpointPayload!));

        Assert.Equal(LongRestSweepPhase.Completed, checkpoint.Phase);

        LongRestDiscoveryGeneration generation = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        Assert.Equal(LongRestSweepPhase.Completed, generation.Phase);

        Assert.Equal(6L, await f.H.ScalarAsync("SELECT PhaseCode FROM long_rest_sweeps;"));

        Assert.Equal(generation.GenerationId, checkpoint.GenerationId);

        Assert.True(f.Handler.SendCount > 0);

        int sends = f.Handler.SendCount;

        string[] before = await OwnedCompletionHistoryAsync(f);

        // The old process has joined and released its claim. Recovery only parks the interrupted sweep: it neither
        // replays a stage nor completes the row, because finishing is the worker's authority.
        await ParkExpiredSweepAsync(f, "owned-completion-recovery");

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(interrupted.AttemptCount + 1, parked.AttemptCount);

        Assert.Equal(sends, f.Handler.SendCount);

        Assert.Equal(before, await OwnedCompletionHistoryAsync(f));

        // The hosted worker readmits it atomically under its own Long Rest lease and finishes the final transition.
        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, progress.Phase);

        Assert.Null(progress.DeferredReason);

        LongRunningOperation final = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Completed, final.State);

        Assert.Equal(parked.AttemptCount + 1, final.AttemptCount);

        Assert.Null(final.TerminalErrorCode);

        Assert.Null(final.LeaseOwner);

        Assert.Null(final.LeaseExpiresAt);

        Assert.Equal(sends, f.Handler.SendCount);

        Assert.Equal(before, await OwnedCompletionHistoryAsync(f));

        Assert.Equal(f.Operation, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None)).Id);
    }

    private static async Task<string[]> OwnedCompletionHistoryAsync(MaintenanceFixture f)
    {
        string[] queries = [CleanCheckpointCanonicalSnapshotSql, CleanCheckpointFinishedAnchorSnapshotSql,
            CleanCheckpointPhysicalEffectSnapshotSql, CleanSummarySnapshotSql, CleanBillingSnapshotSql,
            CleanUnitSnapshotSql, CleanCheckpointInputSnapshotSql, CleanGenerationSnapshotSql, CleanCheckpointSweepSnapshotSql];

        List<string> snapshot = [];

        foreach (string query in queries)
        {
            snapshot.Add(Assert.IsType<string>(await f.H.ScalarAsync(query)));
        }

        return snapshot.ToArray();
    }
}
