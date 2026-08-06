using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Issue #40 requires each recovery handler to be idempotent under repeated startup invocation and
/// to never claim work it cannot prove happened. Every case here runs recovery twice.
/// </summary>
public sealed class RecoveryHandlerTests
{
    private static LongRunningOperation Operation(
        string kind,
        LongRunningOperationRecoveryPolicy policy,
        Guid? inferenceRunId = null,
        Guid? budgetReservationId = null,
        Guid? idempotencyClaimId = null,
        Guid? runId = null)
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);

        return store.Seed(
            kind,
            policy,
            budgetReservationId: budgetReservationId,
            inferenceRunId: inferenceRunId,
            runId: runId) with
        {
            IdempotencyClaimId = idempotencyClaimId,
        };
    }

    // ---- inference-run -----------------------------------------------------------------------

    [Fact]
    public async Task Inference_recovery_abandons_the_run_releases_the_reservation_and_kills_replay()
    {
        Guid runId = Guid.NewGuid();
        Guid reservationId = Guid.NewGuid();
        FakeTurnRunWriter runs = new();
        FakeBudgetReservationService reservations = new();
        FakeIdempotencyClaimStore claims = new();
        runs.SeedRun(runId, InferenceRunStatus.Running);
        IdempotencyClaim partial = claims.Seed(IdempotencyClaimState.Running, terminalStreamComplete: false);

        InferenceRunRecoveryHandler handler = new(
            runs,
            reservations,
            claims,
            new FakeTimeProvider(),
            NullLogger<InferenceRunRecoveryHandler>.Instance);
        LongRunningOperation operation = Operation(
            LongRunningOperationKinds.InferenceRun,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            inferenceRunId: runId,
            budgetReservationId: reservationId,
            idempotencyClaimId: partial.Id);

        LongRunningOperationRecoveryResult first = await handler.RecoverAsync(operation, CancellationToken.None);
        LongRunningOperationRecoveryResult second = await handler.RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, first.State);
        Assert.Equal(LongRunningOperationState.Completed, second.State);
        Assert.Equal(InferenceRunStatus.Abandoned, runs.StatusOf(runId));
        Assert.Equal(IdempotencyClaimState.Abandoned, claims.StateOf(partial.Id)!.State);
        // Release is unconditional and idempotent by design: a pass that died between abandoning the
        // run and releasing its reservation must not leave the daily budget permanently consumed.
        Assert.Equal([reservationId, reservationId], reservations.Released);
    }

    /// <summary>
    /// "Mark incomplete claims non-replayable *unless terminal bytes were fully captured*." A claim
    /// that did capture them is a legitimate cached response and must survive recovery.
    /// </summary>
    [Fact]
    public async Task Inference_recovery_preserves_a_claim_whose_terminal_bytes_were_captured()
    {
        Guid runId = Guid.NewGuid();
        FakeTurnRunWriter runs = new();
        FakeBudgetReservationService reservations = new();
        FakeIdempotencyClaimStore claims = new();
        runs.SeedRun(runId, InferenceRunStatus.Running);
        IdempotencyClaim complete = claims.Seed(IdempotencyClaimState.Completed, terminalStreamComplete: true);

        InferenceRunRecoveryHandler handler = new(
            runs,
            reservations,
            claims,
            new FakeTimeProvider(),
            NullLogger<InferenceRunRecoveryHandler>.Instance);

        _ = await handler.RecoverAsync(
            Operation(
                LongRunningOperationKinds.InferenceRun,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                inferenceRunId: runId,
                idempotencyClaimId: complete.Id),
            CancellationToken.None);

        Assert.Empty(claims.AbandonedClaims);
        Assert.Equal(IdempotencyClaimState.Completed, claims.StateOf(complete.Id)!.State);
    }

    /// <summary>
    /// Recovery must never downgrade a run that genuinely finished before the crash landed.
    /// </summary>
    [Fact]
    public async Task Inference_recovery_does_not_downgrade_an_already_completed_run()
    {
        Guid runId = Guid.NewGuid();
        FakeTurnRunWriter runs = new();
        runs.SeedRun(runId, InferenceRunStatus.Completed);

        InferenceRunRecoveryHandler handler = new(
            runs,
            new FakeBudgetReservationService(),
            new FakeIdempotencyClaimStore(),
            new FakeTimeProvider(),
            NullLogger<InferenceRunRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult result = await handler.RecoverAsync(
            Operation(
                LongRunningOperationKinds.InferenceRun,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                inferenceRunId: runId),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, result.State);
        Assert.Equal(InferenceRunStatus.Completed, runs.StatusOf(runId));
    }

    [Fact]
    public async Task Inference_recovery_without_a_run_link_requires_operator_repair()
    {
        InferenceRunRecoveryHandler handler = new(
            new FakeTurnRunWriter(),
            new FakeBudgetReservationService(),
            new FakeIdempotencyClaimStore(),
            new FakeTimeProvider(),
            NullLogger<InferenceRunRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult result = await handler.RecoverAsync(
            Operation(
                LongRunningOperationKinds.InferenceRun,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);
        Assert.Equal(LongRunningOperationErrorCodes.MissingOperationLink, result.ErrorCode);
    }

    // ---- subagent ----------------------------------------------------------------------------

    /// <summary>
    /// Requirement 8: a crashed child is abandoned, its reservation released once, and it is never
    /// restarted — restarting a subagent from a ledger row is how a recursion storm begins.
    /// </summary>
    [Fact]
    public async Task Subagent_recovery_abandons_the_child_without_restarting_or_rebilling_it()
    {
        Guid reservationId = Guid.NewGuid();
        FakeBudgetReservationService reservations = new();
        FakeTurnRunWriter runs = new();

        SubagentRecoveryHandler handler = new(
            reservations,
            runs,
            NullLogger<SubagentRecoveryHandler>.Instance);
        LongRunningOperation operation = Operation(
            LongRunningOperationKinds.Subagent,
            LongRunningOperationRecoveryPolicy.AbandonSafely,
            budgetReservationId: reservationId,
            runId: Guid.NewGuid());

        LongRunningOperationRecoveryResult first = await handler.RecoverAsync(operation, CancellationToken.None);
        LongRunningOperationRecoveryResult second = await handler.RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, first.State);
        Assert.Equal(LongRunningOperationState.Abandoned, second.State);
        Assert.Equal(
            LongRunningOperationRecoveryOutcomes.SubagentChildAbandoned,
            first.ErrorCode);
        Assert.Empty(runs.Billed);
        Assert.Equal([reservationId, reservationId], reservations.Released);
    }

    // ---- idempotency-claim -------------------------------------------------------------------

    [Fact]
    public async Task Claim_recovery_abandons_a_stranded_running_claim()
    {
        FakeIdempotencyClaimStore claims = new();
        IdempotencyClaim stranded = claims.Seed(IdempotencyClaimState.Running, terminalStreamComplete: false);

        IdempotencyClaimRecoveryHandler handler = new(
            claims,
            new FakeTimeProvider(),
            NullLogger<IdempotencyClaimRecoveryHandler>.Instance);
        LongRunningOperation operation = Operation(
            LongRunningOperationKinds.IdempotencyClaim,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            idempotencyClaimId: stranded.Id);

        _ = await handler.RecoverAsync(operation, CancellationToken.None);
        LongRunningOperationRecoveryResult second = await handler.RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, second.State);
        Assert.Equal(IdempotencyClaimState.Abandoned, claims.StateOf(stranded.Id)!.State);
        Assert.Single(claims.AbandonedClaims);
    }

    [Fact]
    public async Task Claim_recovery_leaves_a_replayable_claim_alone()
    {
        FakeIdempotencyClaimStore claims = new();
        IdempotencyClaim replayable = claims.Seed(IdempotencyClaimState.Completed, terminalStreamComplete: true);

        IdempotencyClaimRecoveryHandler handler = new(
            claims,
            new FakeTimeProvider(),
            NullLogger<IdempotencyClaimRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult result = await handler.RecoverAsync(
            Operation(
                LongRunningOperationKinds.IdempotencyClaim,
                LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                idempotencyClaimId: replayable.Id),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, result.State);
        Assert.Empty(claims.AbandonedClaims);
    }

    // ---- apprentice --------------------------------------------------------------------------

    /// <summary>
    /// An Apprentice is durable and owns its own resume path (§5.7). Recovery hands it back rather
    /// than replaying it, and closes the ledger row so it does not strand.
    /// </summary>
    [Fact]
    public async Task Apprentice_recovery_defers_to_the_durable_checkpoint_resume_path()
    {
        Guid apprenticeId = Guid.NewGuid();
        FakeApprenticeRepository apprentices = new();
        _ = apprentices.Seed(apprenticeId, ApprenticeStatus.Running.ToString(), """{"CurrentStep":3}""");

        ApprenticeRecoveryHandler handler = new(
            apprentices,
            NullLogger<ApprenticeRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult result = await handler.RecoverAsync(
            Operation(
                LongRunningOperationKinds.Apprentice,
                LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
                runId: apprenticeId),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, result.State);
    }

    [Fact]
    public async Task Apprentice_recovery_abandons_a_ledger_row_whose_apprentice_is_gone()
    {
        ApprenticeRecoveryHandler handler = new(
            new FakeApprenticeRepository(),
            NullLogger<ApprenticeRecoveryHandler>.Instance);

        LongRunningOperationRecoveryResult result = await handler.RecoverAsync(
            Operation(
                LongRunningOperationKinds.Apprentice,
                LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
                runId: Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, result.State);
        Assert.Equal(LongRunningOperationRecoveryOutcomes.ApprenticeMissing, result.ErrorCode);
    }

    // ---- workspace-index ---------------------------------------------------------------------

    /// <summary>
    /// Indexing is idempotent by file identity and content hash, so recovery closes the row and lets
    /// the ordinary indexer re-enumerate rather than replaying a partial pass.
    /// </summary>
    [Fact]
    public async Task Workspace_index_recovery_is_restartable_and_repeatable()
    {
        WorkspaceIndexRecoveryHandler handler = new(
            NullLogger<WorkspaceIndexRecoveryHandler>.Instance);
        LongRunningOperation operation = Operation(
            LongRunningOperationKinds.WorkspaceIndex,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);

        LongRunningOperationRecoveryResult first = await handler.RecoverAsync(operation, CancellationToken.None);
        LongRunningOperationRecoveryResult second = await handler.RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Completed, first.State);
        Assert.Equal(LongRunningOperationState.Completed, second.State);
    }
}
