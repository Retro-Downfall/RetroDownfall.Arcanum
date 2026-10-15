using System.Net;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Park policy: a restart-generation fault retires a clean sweep so a fresh request starts a new generation (a protected one is
// retired by a fresh request that carries live turn authority, see FreshAuthorityRetire); the park, readmission and budget metrics
// count exactly what happened; a contested in-process claim is OperationBusy and writes nothing.
public sealed partial class LongRestMaintenanceTests
{
    private const string ParkedMetric = "arcanum.long_rest.sweep.parked";

    private const string ReadmittedMetric = "arcanum.long_rest.sweep.readmitted";

    private const string ExhaustedMetric = "arcanum.long_rest.sweep.budget_exhausted";

    [Theory]
    [InlineData("LongRest.StaleInput")]
    [InlineData("LongRest.StaleFence")]
    [InlineData("LongRest.CurrentnessLost")]
    [InlineData("LongRest.ConfigurationChanged")]
    public async Task A_restart_generation_fault_retires_the_clean_sweep_so_the_next_request_starts_a_new_generation(string code)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        using AsyncFlowMetricCapture metrics = new(ParkedMetric);

        await ParkExpiredSweepAsync(f, "restart-generation-recovery");

        FailingMaintenance failing = new(code);

        LongRestSweepCoordinator coordinator = CleanReviewCoordinator(f, f.RecordedAdmission, failing);

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await ResumeAdmittedWithAsync(coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

        Assert.Equal(code, progress.DeferredReason);

        // Not parked: retired through the Abandoned path, with its terminal code, no lease, and a completion instant.
        LongRunningOperation retired = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, retired.State);

        Assert.Equal(code, retired.TerminalErrorCode);

        Assert.Null(retired.LeaseOwner);

        Assert.NotNull(retired.CompletedAt);

        Assert.Equal(1, metrics.Measurements.Count(static m => m.Instrument == ParkedMetric));

        Assert.Equal(1, metrics.Sum(ParkedMetric, "class", "restart_generation"));

        // The worker does not pick it up again, and the next request replaces it with a new generation in one transaction.
        Assert.Null(LongRestSweepHarness.Require(await new LongRestSweepStore(f.H.Saga.Context, f.Clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);

        Result<LongRunningOperationDto> replaced = await coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(replaced.IsSuccess, replaced.IsFailure ? replaced.Error.Code : null);

        Assert.NotEqual(f.Operation, replaced.Value.Id);

        Assert.Equal(LongRunningOperationState.Pending, replaced.Value.State);

        Assert.Equal((long)LongRestSweepPhase.Retired, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '{f.Operation:N}';"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_sweeps WHERE PhaseCode NOT IN (6, 7, 9);"));
    }

    [Fact]
    public async Task A_protected_restart_generation_fault_is_parked_by_the_sweep_and_a_request_without_authority_does_not_free_it()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        LongRestSweepCoordinator coordinator = CleanReviewCoordinator(f, f.RecordedAdmission, new FailingMaintenance(ErrorCodes.LongRest.StaleFence));

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None));

        Assert.Equal(ErrorCodes.LongRest.StaleFence, progress.DeferredReason);

        // A faulting protected sweep keeps its park: the coordinator never retires it, because only the authority of a Session turn may.
        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal(ErrorCodes.LongRest.StaleFence, parked.TerminalErrorCode);

        // A fresh request that carries no turn authority does not clear it: a protected park is resumable only as
        // authorized_request_required, so the request is refused and writes nothing. The request that does carry live authority is
        // the Fresh-Authority Retire contract, pinned in LongRestMaintenanceTests.FreshAuthorityRetire.
        Result<LongRunningOperationDto> request = await coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(request.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.SweepParked, request.Error.Code);

        Assert.Equal(f.Operation, (await f.Operations.GetAsync(f.Operation))!.Id);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await f.Operations.GetAsync(f.Operation))!.State);
    }

    [Fact]
    public async Task The_park_readmission_and_exhaustion_metrics_count_exactly_the_transitions_that_happened()
    {
        ManualWallClockTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(clock: clock, protectedSources: false);

        using AsyncFlowMetricCapture metrics = new(ParkedMetric, ReadmittedMetric, ExhaustedMetric);

        await ParkExpiredSweepAsync(f, "metrics-recovery");

        FailingMaintenance failing = new(ErrorCodes.LongRest.Unavailable);

        LongRestSweepCoordinator coordinator = CleanReviewCoordinator(f, f.RecordedAdmission, failing);

        for (int readmission = 1; readmission <= LongRestFaultPolicy.MaxReadmissions; readmission++)
        {
            LongRestSweepHarness.Require(await ResumeAdmittedWithAsync(coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

            Assert.Equal(readmission, metrics.Sum(ReadmittedMetric));

            clock.Advance(LongRestFaultPolicy.Cooldown(Math.Min(readmission, LongRestFaultPolicy.MaxReadmissions - 1)));
        }

        // The crash park recovery wrote is counted once, when the worker first readmits it; then four failed readmissions, each parked once, the last
        // of which spent the budget and became a needs-operator park.
        Assert.Equal(LongRestFaultPolicy.MaxReadmissions + 1, metrics.Measurements.Count(static m => m.Instrument == ParkedMetric));

        Assert.Equal(LongRestFaultPolicy.MaxReadmissions, metrics.Sum(ParkedMetric, "class", "resume_later"));

        Assert.Equal(1, metrics.Sum(ParkedMetric, "class", "needs_operator"));

        Assert.Equal(1, metrics.Sum(ExhaustedMetric));

        Assert.Equal(LongRestParkCodes.ReadmitExhausted, (await f.Operations.GetAsync(f.Operation))!.TerminalErrorCode);
    }

    [Fact]
    public async Task A_resume_while_another_executor_owns_the_operation_is_OperationBusy_and_writes_nothing()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        Assert.True(f.Ownership.TryClaim(f.Operation, out Guid token));

        try
        {
            string before = await ParkedSnapshotAsync(f);

            long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

            Result<LongRestSweepProgress> busy = await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None);

            Assert.True(busy.IsFailure);

            Assert.Equal(ErrorCodes.LongRest.OperationBusy, busy.Error.Code);

            Assert.Equal(0, f.Handler.SendCount);

            Assert.Equal(before, await ParkedSnapshotAsync(f));

            Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);
        }
        finally
        {
            f.Ownership.Release(f.Operation, token);
        }

        // Once the other executor lets go, the same call proceeds.
        SummaryOrDecision(f);

        Assert.Equal(LongRestSweepPhase.Completed, LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None)).Phase);
    }

    [Fact]
    public async Task A_resume_that_cannot_lease_a_row_held_by_a_live_owner_is_OperationBusy_not_unavailable()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: true, protectedSources: false);

        // The fixture's worker holds a live lease on the Running row: a second executor cannot lease it.
        Result<LongRestSweepProgress> busy = await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None);

        Assert.True(busy.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.OperationBusy, busy.Error.Code);

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task A_request_that_must_replace_a_sweep_another_executor_owns_is_OperationBusy_and_writes_nothing()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        await ParkExpiredSweepAsync(f, "busy-request-recovery");

        FailingMaintenance failing = new(ErrorCodes.LongRest.StaleFence);

        LongRestSweepCoordinator coordinator = CleanReviewCoordinator(f, f.RecordedAdmission, failing);

        LongRestSweepHarness.Require(await ResumeAdmittedWithAsync(coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

        Assert.Equal(LongRunningOperationState.Abandoned, (await f.Operations.GetAsync(f.Operation))!.State);

        // Retiring the abandoned sweep needs the in-process ownership claim, which another executor holds right now.
        LongRunningOperationOwnership ownership = new();

        LongRestSweepCoordinator contested = new(f.Operations, new LongRestSweepStore(f.H.Saga.Context, f.Clock), f.H.Store, f.Epoch, CleanReviewFacts(f, null),
            failing, f.Gate, ownership, new LongRestSweepQueue(), f.Services.GetRequiredService<IServiceScopeFactory>(), f.Clock, f.RecordedAdmission);

        Assert.True(ownership.TryClaim(f.Operation, out Guid token));

        long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Result<LongRunningOperationDto> request = await contested.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(request.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.OperationBusy, request.Error.Code);

        Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);

        ownership.Release(f.Operation, token);

        Assert.True((await contested.RequestAsync(f.Scope, f.Partition, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task A_protected_provider_failure_parks_for_a_fresh_request_and_the_retry_discloses_and_sends_under_a_new_ordinal()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        f.Handler.Status = HttpStatusCode.InternalServerError;

        LongRestSweepProgress failed = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None));

        // A protected sweep has no budget and no worker door, so whatever resumable fault parked it says a fresh request must.
        Assert.Equal(LongRestParkCodes.AuthorizedRequestRequired, failed.DeferredReason);

        long disclosures = (long)(await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"))!;

        Assert.Equal(1L, disclosures);

        EffectRow settled = Assert.Single(await EffectRowsAsync(f));

        Assert.Equal((long)LongRestDispatchAttemptState.Failed, settled.State);

        Assert.Equal(ErrorCodes.LongRest.TransportUnknown, settled.FailureCode);

        Assert.True(settled.CompletionNull);

        // A request for the scope returns the parked sweep unchanged; a fresh capability readmits it.
        Assert.Equal(f.Operation, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None)).Id);

        f.Handler.Status = HttpStatusCode.OK;

        SummaryOrDecision(f);

        LongRestSweepProgress completed = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, completed.Phase);

        // The retry is a distinct physical send with its own disclosure receipt: one receipt per send, ordinal two.
        Assert.Contains(await EffectRowsAsync(f), static row => row.Failures == 1 && row.Sent == 3 && row.Paid == 2);

        Assert.Equal((long)f.Handler.SendCount, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));
    }
}
