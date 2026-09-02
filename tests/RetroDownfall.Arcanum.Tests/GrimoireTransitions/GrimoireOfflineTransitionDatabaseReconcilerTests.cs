using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Tests.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

/// <summary>
/// The one exact terminal write an offline transition makes to its own operation row.
/// </summary>
/// <remarks>
/// The row is reconciliation evidence, not competing phase authority: the journal decided what
/// happened, and this write records it. Which is why every refusal here leaves the row exactly as it
/// was. A missing row, a row belonging to another launch, and a row somebody else already
/// terminalized are three different situations with three different remedies, and the transition
/// stays closed for all of them rather than guessing.
/// </remarks>
public sealed class GrimoireOfflineTransitionDatabaseReconcilerTests
{

    private static readonly Guid Operation = Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid Source = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly Guid Target = Guid.Parse("33333333-3333-4333-8333-333333333333");

    private const string Effect = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const long LaunchRevision = 4;

    private const long ExpectedRevision = 5;

    private readonly FakeTimeProvider _time = Frozen();

    private static FakeTimeProvider Frozen()
    {

        FakeTimeProvider time = new();

        time.SetUtcNow(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));

        return time;

    }

    [Fact]
    public async Task A_completed_transition_terminalizes_its_row_and_rereads_the_winner()
    {

        FakeLongRunningOperationStore store = Store();

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await Reconcile(store);

        Assert.Equal(
            GrimoireOfflineTransitionDatabaseOutcome.Terminalized,
            reconciled.Outcome);

        Assert.True(reconciled.PermitsRetirement);

        Assert.True(reconciled.TerminalWinnerDigest is { IsValid: true });

        LongRunningOperation row = Assert.Single(store.Operations);

        Assert.Equal(LongRunningOperationState.Completed, row.State);

        Assert.Null(row.TerminalErrorCode);

        Assert.Equal(ExpectedRevision + 1, row.Revision);

        Assert.Equal(_time.GetUtcNow(), row.CompletedAt);

    }

    /// <summary>
    /// A crash between the compare-exchange and the journal publication that records it comes back
    /// to the same row and must reach the same conclusion, with the same evidence.
    /// </summary>
    [Fact]
    public async Task An_already_terminal_row_is_idempotent_success_with_the_same_winner_digest()
    {

        FakeLongRunningOperationStore store = Store();

        GrimoireOfflineTransitionDatabaseReconciliation first = await Reconcile(store);

        long settled = Assert.Single(store.Operations).Revision;

        GrimoireOfflineTransitionDatabaseReconciliation second = await Reconcile(store);

        Assert.Equal(
            GrimoireOfflineTransitionDatabaseOutcome.AlreadyTerminal,
            second.Outcome);

        Assert.True(second.PermitsRetirement);

        Assert.Equal(first.TerminalWinnerDigest, second.TerminalWinnerDigest);

        Assert.Equal(settled, Assert.Single(store.Operations).Revision);

    }

    [Fact]
    public async Task A_missing_row_is_never_created_and_never_permits_retirement()
    {

        FakeLongRunningOperationStore store = new(_time);

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await Reconcile(store);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.RowMissing, reconciled.Outcome);

        Assert.False(reconciled.PermitsRetirement);

        Assert.Null(reconciled.TerminalWinnerDigest);

        Assert.Empty(store.Operations);

    }

    /// <summary>
    /// Each immutable field is disturbed on its own, because they all fail closed with the same
    /// outcome: a check that only covered one of them would be indistinguishable from one that
    /// covered all five.
    /// </summary>
    [Theory]
    [InlineData("kind")]
    [InlineData("recovery-policy")]
    [InlineData("checkpoint-version")]
    [InlineData("checkpoint-reference")]
    [InlineData("launch-payload")]
    [InlineData("missing-payload")]
    public async Task A_row_whose_immutable_launch_fields_disagree_is_never_overwritten(string field)
    {

        FakeLongRunningOperationStore store = Store(field);

        LongRunningOperation before = Assert.Single(store.Operations);

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await Reconcile(store);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.RowConflicting, reconciled.Outcome);

        Assert.False(reconciled.PermitsRetirement);

        Assert.Equal(before, Assert.Single(store.Operations));

    }

    /// <summary>
    /// A legacy row cannot be terminalized through the journal path, however well-formed it is: it
    /// never named a target, so no journal was ever bound to it.
    /// </summary>
    [Fact]
    public async Task A_valid_legacy_row_is_conflicting_rather_than_reconcilable()
    {

        FakeLongRunningOperationStore store = Store("legacy");

        LongRunningOperation before = Assert.Single(store.Operations);

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await Reconcile(store);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.RowConflicting, reconciled.Outcome);

        Assert.Equal(before, Assert.Single(store.Operations));

    }

    [Fact]
    public async Task A_row_that_moved_since_the_journal_bound_it_is_never_overwritten()
    {

        FakeLongRunningOperationStore store = Store("revision");

        LongRunningOperation before = Assert.Single(store.Operations);

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await Reconcile(store);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.RevisionMismatch, reconciled.Outcome);

        Assert.False(reconciled.PermitsRetirement);

        Assert.Equal(before, Assert.Single(store.Operations));

    }

    /// <summary>
    /// A row already terminal under a different disposition is somebody else's answer, and replacing
    /// it would erase the only record of what that answer was.
    /// </summary>
    [Theory]
    [InlineData(LongRunningOperationState.Failed)]
    [InlineData(LongRunningOperationState.Abandoned)]
    public async Task A_row_terminal_under_another_disposition_is_a_conflict(
        LongRunningOperationState state)
    {

        FakeLongRunningOperationStore store = Store();

        Seed(store, state, "somebody.else");

        LongRunningOperation before = Assert.Single(store.Operations);

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await Reconcile(store);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.TerminalConflict, reconciled.Outcome);

        Assert.False(reconciled.PermitsRetirement);

        Assert.Equal(before, Assert.Single(store.Operations));

    }

    /// <summary>
    /// A pre-effect failure is proven by the journal, never by the row.
    /// </summary>
    /// <remarks>
    /// An offline phase never rewrites the launch checkpoint, so the row looks identical whether the
    /// family was replaced or never touched. Only the journal knows, and it knows by having recorded
    /// nothing past the phase that preceded every effect.
    /// </remarks>
    [Fact]
    public async Task A_pre_effect_failure_terminalizes_the_row_as_failed()
    {

        FakeLongRunningOperationStore store = Store();

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await Reconcile(
            store,
            GrimoireOfflineTransitionTerminalDisposition.FailedBeforeEffect,
            CovenantResetPhaseMachine.First,
            inFlight: null);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.Terminalized, reconciled.Outcome);

        LongRunningOperation row = Assert.Single(store.Operations);

        Assert.Equal(LongRunningOperationState.Failed, row.State);

        Assert.Equal(
            GrimoireOfflineTransitionDatabaseReconciler.PreEffectFailureCode,
            row.TerminalErrorCode);

    }

    [Theory]
    [InlineData(CovenantResetPhase.CanonicalApplied, null)]
    [InlineData(CovenantResetPhase.ReopenedVerified, null)]
    [InlineData(CovenantResetPhase.InventoryPrepared, CovenantResetPhase.CanonicalApplied)]
    public async Task A_failure_the_journal_cannot_prove_is_pre_effect_is_refused(
        CovenantResetPhase lastCompleted,
        CovenantResetPhase? inFlight)
    {

        FakeLongRunningOperationStore store = Store();

        LongRunningOperation before = Assert.Single(store.Operations);

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await Reconcile(
            store,
            GrimoireOfflineTransitionTerminalDisposition.FailedBeforeEffect,
            lastCompleted,
            inFlight);

        Assert.Equal(
            GrimoireOfflineTransitionDatabaseOutcome.EffectNotProvenAbsent,
            reconciled.Outcome);

        Assert.False(reconciled.PermitsRetirement);

        Assert.Equal(before, Assert.Single(store.Operations));

    }

    /// <summary>
    /// Losing the compare-exchange is not an answer. The winner is reread and validated by the same
    /// rules, so an indistinguishable competing writer that reached the intended terminal is success
    /// and anything else is a conflict.
    /// </summary>
    [Fact]
    public async Task A_lost_compare_exchange_accepts_a_winner_that_reached_the_same_terminal()
    {

        FakeLongRunningOperationStore store = Store();

        store.TryTransitionOverride = _ =>
        {

            Seed(store, LongRunningOperationState.Completed, terminalErrorCode: null);

            return false;

        };

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await Reconcile(store);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.AlreadyTerminal, reconciled.Outcome);

        Assert.True(reconciled.PermitsRetirement);

    }

    [Fact]
    public async Task A_lost_compare_exchange_to_a_different_terminal_is_a_conflict()
    {

        FakeLongRunningOperationStore store = Store();

        store.TryTransitionOverride = _ =>
        {

            Seed(store, LongRunningOperationState.Failed, "somebody.else");

            return false;

        };

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await Reconcile(store);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.TerminalConflict, reconciled.Outcome);

        Assert.False(reconciled.PermitsRetirement);

    }

    /// <summary>
    /// A compare-exchange that reports success over a row that is not the winner it claims proves
    /// nothing, and the transition may not retire on it.
    /// </summary>
    [Fact]
    public async Task A_won_compare_exchange_whose_reread_does_not_confirm_it_is_unproven()
    {

        FakeLongRunningOperationStore store = Store();

        store.TryTransitionOverride = _ => true;

        GrimoireOfflineTransitionDatabaseReconciliation reconciled = await Reconcile(store);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.WinnerUnproven, reconciled.Outcome);

        Assert.False(reconciled.PermitsRetirement);

        Assert.Null(reconciled.TerminalWinnerDigest);

    }

    /// <summary>
    /// The winner digest is a function of the launch and the exact terminal row, so two transitions
    /// that ended the same way over different launches are still distinguishable.
    /// </summary>
    [Fact]
    public async Task The_winner_digest_binds_the_launch_it_terminalized()
    {

        FakeLongRunningOperationStore store = Store();

        GrimoireOfflineTransitionDatabaseReconciliation completed = await Reconcile(store);

        FakeLongRunningOperationStore other = Store();

        GrimoireOfflineTransitionDatabaseReconciliation failed = await Reconcile(
            other,
            GrimoireOfflineTransitionTerminalDisposition.FailedBeforeEffect,
            CovenantResetPhaseMachine.First,
            inFlight: null);

        Assert.NotEqual(completed.TerminalWinnerDigest, failed.TerminalWinnerDigest);

    }

    [Fact]
    public async Task A_journal_with_no_usable_launch_binding_touches_nothing()
    {

        FakeLongRunningOperationStore store = Store();

        LongRunningOperation before = Assert.Single(store.Operations);

        GrimoireOfflineTransitionDatabaseReconciliation reconciled =
            await new GrimoireOfflineTransitionDatabaseReconciler(store, _time).ReconcileAsync(
                Payload(CovenantResetPhaseMachine.First, null) with
                {
                    Binding = Journal() with { ExpectedDatabaseOperationRevision = 0 },
                },
                GrimoireOfflineTransitionTerminalDisposition.Completed,
                CancellationToken.None);

        Assert.Equal(GrimoireOfflineTransitionDatabaseOutcome.JournalUnusable, reconciled.Outcome);

        Assert.False(reconciled.PermitsRetirement);

        Assert.Equal(before, Assert.Single(store.Operations));

    }

    private Task<GrimoireOfflineTransitionDatabaseReconciliation> Reconcile(
        FakeLongRunningOperationStore store) =>
        Reconcile(
            store,
            GrimoireOfflineTransitionTerminalDisposition.Completed,
            CovenantResetPhaseMachine.Last,
            inFlight: null);

    private Task<GrimoireOfflineTransitionDatabaseReconciliation> Reconcile(
        FakeLongRunningOperationStore store,
        GrimoireOfflineTransitionTerminalDisposition disposition,
        CovenantResetPhase lastCompleted,
        CovenantResetPhase? inFlight) =>
        new GrimoireOfflineTransitionDatabaseReconciler(store, _time).ReconcileAsync(
            Payload(lastCompleted, inFlight),
            disposition,
            CancellationToken.None);

    private static CovenantOfflineTransitionLaunchV4 Launch() =>
        new(
            CovenantOfflineTransitionLaunchV4.CurrentVersion,
            Operation,
            LongRunningOperationKinds.DataRetentionMutation,
            nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
            CovenantExclusiveOperation.CovenantReset,
            Effect,
            Source,
            Target,
            new CovenantOfflineTransitionEpochsV1(11, 22, 33),
            new CovenantOfflineTransitionEpochsV1(12, 23, 34),
            LaunchRevision);

    private static GrimoireOfflineTransitionBinding Journal() =>
        GrimoireOfflineTransitionLaunch.JournalBinding(
            GrimoireOfflineTransitionLaunch.FromLaunch(Launch()).Value,
            slotEpoch: 3,
            payloadVersion: 1,
            ExpectedRevision,
            parentReceiptBindingDigest: null).Value;

    private static CovenantResetOfflineTransitionPayloadV1 Payload(
        CovenantResetPhase lastCompleted,
        CovenantResetPhase? inFlight) =>
        new(
            Journal(),
            new GrimoireOfflineTransitionLifecycle(
                GrimoireOfflineTransitionState.DatabaseReconciliationPending,
                GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
                new GrimoireOfflineTransitionClosingEvidence(true, true, true, true, true, Source),
                new GrimoireOfflineTransitionVerificationEvidence(true, true, true),
                new GrimoireOfflineTransitionReconciliationEvidence(
                    GrimoireOfflineTransitionReconciliationStep.CandidateVerified,
                    DatabaseTerminalWinnerDigest: null,
                    ParentReceiptNotRequired: false,
                    ParentReceiptDigest: null,
                    LaneClosed: false,
                    CovenantDispositionIntent: null),
                Blocker: null),
            lastCompleted,
            inFlight,
            InFlightBeforeState: null,
            ReplacementEvidence: null);

    private FakeLongRunningOperationStore Store(string disturbed = "")
    {

        FakeLongRunningOperationStore store = new(_time);

        byte[] payload = disturbed switch
        {

            "launch-payload" => CovenantRecoveryCheckpointCodec.Encode(
                Launch() with { StartingRevision = LaunchRevision + 1 }),

            "legacy" => CovenantRecoveryCheckpointCodec.Encode(
                new DataRetentionMutationCheckpointV3(
                    DataRetentionMutationCheckpointV3.CurrentVersion,
                    Subtype: "reset-memory",
                    Target: "5",
                    new CovenantResetEffectArmV1(
                        Operation,
                        Effect,
                        CovenantExclusiveOperation.CovenantReset,
                        CovenantResetPhaseMachine.First))),

            _ => CovenantRecoveryCheckpointCodec.Encode(Launch()),

        };

        store.Add(
            new LongRunningOperation(
                Operation,
                disturbed is "kind"
                    ? LongRunningOperationKinds.DataRetentionFactoryReset
                    : LongRunningOperationKinds.DataRetentionMutation,
                LongRunningOperationState.Running,
                disturbed is "recovery-policy"
                    ? LongRunningOperationRecoveryPolicy.RestartIdempotently
                    : LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                RootOperationId: null,
                ParentOperationId: null,
                SessionId: null,
                RunId: null,
                InferenceRunId: null,
                BudgetReservationId: null,
                IdempotencyClaimId: null,
                _time.GetUtcNow(),
                StartedAt: _time.GetUtcNow(),
                HeartbeatAt: _time.GetUtcNow(),
                CompletedAt: null,
                LeaseOwner: null,
                LeaseExpiresAt: null,
                AttemptCount: 1,
                CheckpointVersion: disturbed is "checkpoint-version"
                    ? DataRetentionMutationCheckpointV3.CurrentVersion
                    : disturbed is "legacy"
                        ? DataRetentionMutationCheckpointV3.CurrentVersion
                        : CovenantOfflineTransitionLaunchV4.CurrentVersion,
                CheckpointPayload: disturbed is "missing-payload" ? null : payload,
                CheckpointReference: disturbed is "checkpoint-reference"
                    ? "retention-mutation:not-this-one"
                    : CovenantResetCheckpointInitiator.CheckpointReference(
                        LongRunningOperationKinds.DataRetentionMutation,
                        Operation),
                PublicSummary: "Covenant memory reset",
                TerminalErrorCode: null,
                Revision: disturbed is "revision" ? ExpectedRevision + 1 : ExpectedRevision));

        return store;

    }

    private void Seed(
        FakeLongRunningOperationStore store,
        LongRunningOperationState state,
        string? terminalErrorCode)
    {

        LongRunningOperation current = store.Operations.Single();

        store.Add(
            current with
            {
                State = state,
                TerminalErrorCode = terminalErrorCode,
                CompletedAt = _time.GetUtcNow(),
                Revision = current.Revision + 1,
            });

    }

}
