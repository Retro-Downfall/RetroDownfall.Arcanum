using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Fact]
    public async Task Progress_committed_during_the_last_readmission_resets_the_budget_before_the_park_so_the_next_fault_keeps_its_own_code()
    {
        ManualWallClockTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(clock: clock, protectedSources: false);

        await ParkExpiredSweepAsync(f, "progress-budget-recovery");

        // Three failures are already spent, so this readmission takes the budget to the last allowed one.
        await f.H.ExecuteAsync("UPDATE long_rest_sweeps SET ReadmitCount = 3, ParkedAt = NULL;");

        ProgressThenFailingMaintenance maintenance = new(f, ErrorCodes.LongRest.Unavailable);

        LongRestSweepCoordinator coordinator = CleanReviewCoordinator(f, f.RecordedAdmission, maintenance);

        LongRestSweepHarness.Require(await ResumeAdmittedWithAsync(coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

        Assert.Equal(1, maintenance.Calls);

        // The accepted unit reset the durable budget while the run was still going; the park must read that, not the count
        // the readmission left behind.
        Assert.Equal(0L, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal(ErrorCodes.LongRest.Unavailable, parked.TerminalErrorCode);

        // Still resumable: once the first cooldown has passed the worker is offered it again.
        clock.Advance(LongRestFaultPolicy.Cooldown(0));

        Assert.Equal(f.Operation, LongRestSweepHarness.Require(await new LongRestSweepStore(f.H.Saga.Context, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);
    }

    [Fact]
    public async Task A_resumable_fault_with_no_progress_on_the_last_readmission_still_exhausts_the_budget()
    {
        ManualWallClockTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(clock: clock, protectedSources: false);

        await ParkExpiredSweepAsync(f, "no-progress-budget-recovery");

        await f.H.ExecuteAsync("UPDATE long_rest_sweeps SET ReadmitCount = 3, ParkedAt = NULL;");

        FailingMaintenance failing = new(ErrorCodes.LongRest.Unavailable);

        LongRestSweepCoordinator coordinator = CleanReviewCoordinator(f, f.RecordedAdmission, failing);

        LongRestSweepHarness.Require(await ResumeAdmittedWithAsync(coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

        Assert.Equal(4L, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));

        Assert.Equal(LongRestParkCodes.ReadmitExhausted, (await f.Operations.GetAsync(f.Operation))!.TerminalErrorCode);
    }

    [Fact]
    public async Task The_public_session_shell_never_readmits_a_parked_clean_sweep_because_only_the_hosted_worker_owns_it()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        await ParkExpiredSweepAsync(f, "shell-authority-recovery");

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        string before = await ParkedSnapshotAsync(f);

        LongRestSweepProgress deferred = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Deferred, deferred.Phase);

        Assert.Equal(LongRestParkCodes.Interrupted, deferred.DeferredReason);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        Assert.Equal(parked.AttemptCount, (await f.Operations.GetAsync(f.Operation))!.AttemptCount);

        // The same row is readmitted by the worker's own door, which proves the refusal was about authority and not state.
        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        LongRestSweepProgress finished = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, finished.Phase);
    }

    [Fact]
    public async Task A_same_boot_epoch_only_change_retires_a_parked_sweep_because_its_generation_can_no_longer_revalidate()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        await ParkExpiredSweepAsync(f, "epoch-aba-recovery");

        Guid parkedId = f.Operation;

        LongRestConfigurationStamp prior = f.Epoch.Capture().Stamp;

        // A -> B -> A inside one boot: the value digest is back to the parked one, but the epoch moved twice.
        ArcanumSettings other = LongRestConfigurationEpochTests.Settings();

        other.Cost.Pricing.DefaultPricing!.InputPer1M = 17m;

        f.Options.Set(other);

        f.Options.Set(LongRestConfigurationEpochTests.Settings());

        LongRestConfigurationStamp current = f.Epoch.Capture().Stamp;

        Assert.Equal(prior.BootId, current.BootId);

        Assert.Equal(prior.ValueDigest, current.ValueDigest);

        Assert.Equal(prior.Epoch + 2, current.Epoch);

        LongRunningOperationDto replacement = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None));

        Assert.NotEqual(parkedId, replacement.Id);

        Assert.Equal(LongRunningOperationState.Pending, replacement.State);

        LongRunningOperation retired = (await f.Operations.GetAsync(parkedId))!;

        Assert.Equal(LongRunningOperationState.Abandoned, retired.State);

        Assert.Equal(LongRestParkCodes.StaleConfiguration, retired.TerminalErrorCode);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_sweeps WHERE PhaseCode NOT IN (6, 7, 9);"));
    }

    /// <summary>Commits one accepted unit's worth of progress (the budget reset) and then fails the sweep with a chosen code.</summary>
    private sealed class ProgressThenFailingMaintenance(MaintenanceFixture f, string code) : ILongRestAdmittedMaintenance
    {
        internal int Calls;

        public async Task<Result<LongRestSweepProgress>> ProcessAdmittedAsync(Guid operationId, string workerOwner,
            LongRestMaintenanceCapability? capability, IGrimoireWorkLease admission, CancellationToken ct)
        {
            Calls++;

            await f.H.ExecuteAsync("UPDATE long_rest_sweeps SET ReadmitCount = 0 WHERE OperationId = $id;", ("$id", operationId.ToString("N")));

            return Result<LongRestSweepProgress>.Failure(new Error(code, "Fixture failure after progress."));
        }
    }
}
