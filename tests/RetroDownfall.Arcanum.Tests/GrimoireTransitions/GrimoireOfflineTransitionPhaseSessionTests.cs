using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

/// <summary>
/// The phase authority an erasure records its progress through, driven against the real journal.
/// </summary>
/// <remarks>
/// Every assertion here is about the translation rather than about the journal: the lifecycle
/// validator already refuses an illegal shape, and its own suite proves that it does. What this suite
/// prevents is a phase authority that speaks the validator's language incorrectly — a caller that
/// asks to complete a phase and gets a payload the graph rejects, or worse, one the graph accepts
/// while meaning something else. Those failures are invisible until an erasure is half-way through a
/// database, which is the one place they cannot be investigated.
/// </remarks>
public sealed class GrimoireOfflineTransitionPhaseSessionTests : IDisposable
{

    private static readonly Guid Installation =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid Operation =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly Guid Source =
        Guid.Parse("33333333-3333-4333-8333-333333333333");

    private static readonly Guid Target =
        Guid.Parse("44444444-4444-4444-8444-444444444444");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-offline-transition-phases-" + Guid.NewGuid().ToString("N"));

    private readonly string _guarded;

    private readonly InMemoryOsCredentialStore _credentials = new();

    private readonly ArcanumMaintenanceLock _lock;

    public GrimoireOfflineTransitionPhaseSessionTests()
    {

        Directory.CreateDirectory(_root);

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                _root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

        _guarded = Path.Combine(_root, "arcanum");

        Directory.CreateDirectory(_guarded);

        _lock = ArcanumMaintenanceLock.TryAcquire(_guarded)
            ?? throw new InvalidOperationException("The test could not take its maintenance lock.");

        GrimoireOfflineTransitionJournalLocation location = Value(
            new GrimoireOfflineTransitionJournalFileStore().ResolveLocation(_guarded));

        BackupRestoreJournalInstallationIdentityProvider identities = new(_credentials);

        _ = Value(identities.SeedFromDatabase(_lock, _guarded, location.ProfileNamespace, Installation));

    }

    public void Dispose()
    {

        _lock.Dispose();

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    /// <summary>
    /// A whole committed reset is published as one legal ladder, and only then may retire.
    /// </summary>
    /// <remarks>
    /// Driven end to end rather than a step at a time because the ladder's difficulty is cumulative:
    /// almost every edge forbids changing anything it does not own, so a step that quietly carried a
    /// stale piece of evidence forward is refused several revisions after the mistake was made. A
    /// suite that exercised each edge from a hand-built predecessor would never see that.
    /// </remarks>
    [Fact]
    public async Task A_committed_reset_publishes_the_whole_ladder_and_retires()
    {

        GrimoireOfflineTransitionPhaseSession session = await OpenAsync(
            GrimoireOfflineTransitionKind.CovenantReset);

        await DriveToAppliedAsync(session, CovenantResetPhaseMachine.Ordered);

        Assert.Equal(CovenantResetPhase.SidecarsVerified, session.LastCompletedPhase);

        await CommitAndRetireAsync(session);

    }

    /// <summary>
    /// A factory erasure records its ordinary continuation at the one boundary that admits it.
    /// </summary>
    /// <remarks>
    /// The flag is a one-way sub-state published on its own revision, between the completion of
    /// managed-artifact processing and the beginning of handle closure. That is the only place the
    /// validator accepts it, and it is the only place it is true: before it the continuation has not
    /// run, and after it the phase window can no longer distinguish a run that completed it from one
    /// that crashed before starting.
    /// </remarks>
    [Fact]
    public async Task A_factory_erasure_records_its_continuation_at_its_exact_boundary()
    {

        GrimoireOfflineTransitionPhaseSession session = await OpenAsync(
            GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure);

        await EnterApplyingAsync(session);

        foreach (CovenantResetPhase phase in CovenantResetPhaseMachine.Ordered)
        {

            if (phase is CovenantResetPhase.InventoryPrepared)
            {

                continue;

            }

            if (phase is CovenantResetPhase.ReopenedVerified)
            {

                break;

            }

            if (phase is CovenantResetPhase.HandlesClosed)
            {

                Assert.False(session.OrdinaryFactoryContinuationCompleted);

                Assert.True(
                    (await session.RecordFactoryContinuationAsync(CancellationToken.None)).IsSuccess);

                Assert.True(session.OrdinaryFactoryContinuationCompleted);

            }

            await RunPhaseAsync(session, phase);

        }

        await CommitAndRetireAsync(session);

        Assert.True(session.OrdinaryFactoryContinuationCompleted);

    }

    /// <summary>
    /// A reset has nowhere to record a factory continuation, and is refused rather than obliged.
    /// </summary>
    /// <remarks>
    /// The two payloads are separate strict records because the kind decides what an erasure
    /// preserves. A caller asking a reset to remember a factory fact has confused them, and answering
    /// that question with a silent success would let the confusion travel.
    /// </remarks>
    [Fact]
    public async Task A_reset_refuses_to_record_a_factory_continuation_it_cannot_carry()
    {

        GrimoireOfflineTransitionPhaseSession session = await OpenAsync(
            GrimoireOfflineTransitionKind.CovenantReset);

        await EnterApplyingAsync(session);

        Result recorded = await session.RecordFactoryContinuationAsync(CancellationToken.None);

        Assert.True(recorded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, recorded.Error.Code);

    }

    /// <summary>
    /// A transition proved to have touched no storage rolls back without entering the phase ladder.
    /// </summary>
    /// <remarks>
    /// The rollback arm is the one the closing proof authorizes directly, and it stays at the launch's
    /// own phase throughout. A rollback that had advanced a phase would be claiming an effect it is
    /// about to say never happened.
    /// </remarks>
    [Fact]
    public async Task A_proven_pre_effect_rollback_reaches_retirement_without_applying_anything()
    {

        GrimoireOfflineTransitionPhaseSession session = await OpenAsync(
            GrimoireOfflineTransitionKind.CovenantReset);

        Assert.True((await session.EnterClosingAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await session.RecordClosedAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await session.PrepareReopenAsync(
            GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

        Assert.Equal(CovenantResetPhase.InventoryPrepared, session.LastCompletedPhase);

        await VerifyReconcileAndRetireAsync(
            session,
            GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen);

    }

    /// <summary>
    /// Parking records the exact state a resume has to reach, and keeps the journal.
    /// </summary>
    /// <remarks>
    /// The resume state is read from where the transition actually stood rather than supplied by the
    /// caller, because a park that recorded an intended destination instead of the real one would let
    /// a resume re-enter a state the transition had already left.
    /// </remarks>
    [Fact]
    public async Task Parking_records_the_state_the_transition_stood_in()
    {

        GrimoireOfflineTransitionPhaseSession session = await OpenAsync(
            GrimoireOfflineTransitionKind.CovenantReset);

        await EnterApplyingAsync(session);

        await RunPhaseAsync(session, CovenantResetPhase.CanonicalApplied);

        Assert.True((await session.ParkAsync(
            Digest(0x51),
            Digest(0x52),
            CancellationToken.None)).IsSuccess);

        Assert.Equal(GrimoireOfflineTransitionState.KeepClosed, session.State);

        Assert.Equal(GrimoireOfflineTransitionHandlerOutcome.KeepClosed, session.Outcome);

        GrimoireOfflineTransitionBlocker blocker = session.Current.Payload.Lifecycle.Blocker!;

        Assert.Equal(GrimoireOfflineTransitionState.Applying, blocker.ResumeState);

        Assert.Equal(Digest(0x52), blocker.ExpectedStateDigest);

        Assert.Equal(CovenantResetPhase.CanonicalApplied, session.LastCompletedPhase);

    }

    /// <summary>
    /// Retirement is refused until the whole reconciliation suffix has been published.
    /// </summary>
    /// <remarks>
    /// Each suffix step is a separate revision because each is a separate fact somebody could crash
    /// between. Retiring early would discard the only record that says which of them had happened.
    /// </remarks>
    [Fact]
    public async Task Retirement_is_refused_before_the_reconciliation_suffix_is_complete()
    {

        GrimoireOfflineTransitionPhaseSession session = await OpenAsync(
            GrimoireOfflineTransitionKind.CovenantReset);

        await DriveToAppliedAsync(session, CovenantResetPhaseMachine.Ordered);

        Assert.True((await session.PrepareReopenAsync(
            GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
            CancellationToken.None)).IsSuccess);

        Assert.True((await session.EnterVerifyingAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await session.RecordVerificationAsync(true, true, true, CancellationToken.None)).IsSuccess);

        Assert.True((await session.BeginReconciliationAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await session.RecordTerminalWinnerAsync(Digest(0x61), CancellationToken.None)).IsSuccess);

        // The lane is still open and the disposition unspent, so there is nothing to retire yet.
        Assert.True((await session.PrepareRetirementAsync(CancellationToken.None)).IsFailure);

        Assert.True((await session.RetireAsync(CancellationToken.None)).IsFailure);

    }

    private async Task<GrimoireOfflineTransitionPhaseSession> OpenAsync(
        GrimoireOfflineTransitionKind kind)
    {

        GrimoireOfflineTransitionLifecycleStore lifecycle = new(
            new GrimoireOfflineTransitionJournalStore(_credentials),
            GrimoireOfflineTransitionHandlerRegistry.Production);

        GrimoireOfflineTransitionTypedPublication opened = Value(await lifecycle.BeginBoundAsync(
            _lock,
            _guarded,
            Installation,
            Operation,
            kind,
            payloadVersion: 1,
            slotEpoch => Result<IGrimoireOfflineTransitionPayload>.Success(Prepared(kind, slotEpoch)),
            CancellationToken.None));

        return new GrimoireOfflineTransitionPhaseSession(lifecycle, _lock, opened);

    }

    private static async Task EnterApplyingAsync(GrimoireOfflineTransitionPhaseSession session)
    {

        Assert.True((await session.EnterClosingAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await session.RecordClosedAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await session.EnterApplyingAsync(CancellationToken.None)).IsSuccess);

    }

    private static async Task DriveToAppliedAsync(
        GrimoireOfflineTransitionPhaseSession session,
        IReadOnlyList<CovenantResetPhase> ordered)
    {

        await EnterApplyingAsync(session);

        foreach (CovenantResetPhase phase in ordered)
        {

            if (phase is CovenantResetPhase.InventoryPrepared)
            {

                continue;

            }

            // The journal's applying ceiling is the last phase that touches storage. What the reopen
            // proves is verification evidence rather than a phase, so the tenth code never appears.
            if (phase is CovenantResetPhase.ReopenedVerified)
            {

                break;

            }

            await RunPhaseAsync(session, phase);

        }

    }

    private static async Task RunPhaseAsync(
        GrimoireOfflineTransitionPhaseSession session,
        CovenantResetPhase phase)
    {

        Assert.True(
            (await session.BeginPhaseAsync(
                phase,
                new GrimoireOfflineTransitionBeforeStateEvidence(Digest(0x21), Digest(0x22)),
                CancellationToken.None)).IsSuccess,
            "begin " + phase);

        Assert.Equal(phase, session.InFlightPhase);

        Assert.True(
            (await session.CompletePhaseAsync(phase, CancellationToken.None)).IsSuccess,
            "complete " + phase);

        Assert.Null(session.InFlightPhase);

        Assert.Equal(phase, session.LastCompletedPhase);

    }

    private static async Task CommitAndRetireAsync(GrimoireOfflineTransitionPhaseSession session)
    {

        Assert.True((await session.PrepareReopenAsync(
            GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
            CancellationToken.None)).IsSuccess);

        await VerifyReconcileAndRetireAsync(
            session,
            GrimoireOfflineTransitionTerminalIntent.CommitAndReopen);

    }

    private static async Task VerifyReconcileAndRetireAsync(
        GrimoireOfflineTransitionPhaseSession session,
        GrimoireOfflineTransitionTerminalIntent intent)
    {

        Assert.True((await session.EnterVerifyingAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await session.RecordVerificationAsync(true, false, false, CancellationToken.None)).IsSuccess);

        Assert.True((await session.RecordVerificationAsync(true, true, false, CancellationToken.None)).IsSuccess);

        Assert.True((await session.RecordVerificationAsync(true, true, true, CancellationToken.None)).IsSuccess);

        Assert.True((await session.BeginReconciliationAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await session.RecordTerminalWinnerAsync(Digest(0x31), CancellationToken.None)).IsSuccess);

        Assert.True((await session.RecordParentReceiptAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await session.RecordLaneClosedAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await session.BeginCovenantDispositionAsync(CancellationToken.None)).IsSuccess);

        Assert.Equal(
            intent,
            session.Current.Payload.Lifecycle.ReconciliationEvidence!.CovenantDispositionIntent);

        Assert.True((await session.CompleteCovenantDispositionAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await session.PrepareRetirementAsync(CancellationToken.None)).IsSuccess);

        Assert.Equal(GrimoireOfflineTransitionState.RetirementPending, session.State);

        Assert.True((await session.RetireAsync(CancellationToken.None)).IsSuccess);

    }

    private static IGrimoireOfflineTransitionPayload Prepared(
        GrimoireOfflineTransitionKind kind,
        ulong slotEpoch)
    {

        GrimoireOfflineTransitionBinding binding = new(
            Operation,
            kind,
            PayloadVersion: 1,
            slotEpoch,
            Digest(0x11),
            Source,
            Target,
            new GrimoireOfflineTransitionEpochTuple(1, 2, 3),
            new GrimoireOfflineTransitionEpochTuple(2, 3, 4),
            Digest(0x12),
            ExpectedDatabaseOperationRevision: 4,
            ParentReceiptBindingDigest: null);

        GrimoireOfflineTransitionLifecycle lifecycle = new(
            GrimoireOfflineTransitionState.Prepared,
            GrimoireOfflineTransitionTerminalIntent.Undecided,
            new GrimoireOfflineTransitionClosingEvidence(false, false, false, false, false, null),
            new GrimoireOfflineTransitionVerificationEvidence(false, false, false),
            ReconciliationEvidence: null,
            Blocker: null);

        return kind is GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure
            ? new HealthyCatalogFactoryErasureOfflineTransitionPayloadV1(
                binding,
                lifecycle,
                CovenantResetPhase.InventoryPrepared,
                InFlightPhase: null,
                InFlightBeforeState: null,
                ReplacementEvidence: null,
                OrdinaryFactoryContinuationCompleted: false)
            : new CovenantResetOfflineTransitionPayloadV1(
                binding,
                lifecycle,
                CovenantResetPhase.InventoryPrepared,
                InFlightPhase: null,
                InFlightBeforeState: null,
                ReplacementEvidence: null);

    }

    private static CovenantDigest Digest(byte value) => new(Enumerable.Repeat(value, 32).ToArray());

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.Error.Code + ":" + result.Error.Message);

        return result.Value;

    }

}
