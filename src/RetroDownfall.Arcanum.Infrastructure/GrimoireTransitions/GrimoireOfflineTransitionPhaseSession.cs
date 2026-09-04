using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

/// <summary>
/// The durable phase authority of one offline transition, from its opening publication to retirement.
/// </summary>
/// <remarks>
/// An erasure cannot record its own progress in the database it is erasing. It cannot write "I have
/// proved this database closed" into that database, and it cannot renew a lease through a connection
/// whose absence it is about to prove. So progress lives in the authenticated journal beside the
/// installation lock, and this is the only thing that writes it.
///
/// <para>Every step here is one journal revision, and the shape of each is fixed by the lifecycle
/// validator rather than by this type. What this type owns is the translation: a caller that has just
/// finished a phase says so in phase terms, and the exact payload that represents it — binding
/// untouched, every piece of evidence it does not own preserved exactly — is assembled here. A caller
/// hand-building those payloads would be a second author of the same rules.</para>
///
/// <para>Effects are published twice: once in flight with the evidence needed to resolve ambiguity
/// afterwards, and once complete. A crash before the first means the effect may not have begun; a
/// crash after it never permits assuming it did. That is why the pair cannot be collapsed into one
/// revision, and why the validator refuses a payload that tries.</para>
/// </remarks>
internal sealed class GrimoireOfflineTransitionPhaseSession
{

    private readonly GrimoireOfflineTransitionLifecycleStore _lifecycle;

    private readonly ArcanumMaintenanceLock _heldInstallationLock;

    private GrimoireOfflineTransitionTypedPublication _current;

    internal GrimoireOfflineTransitionPhaseSession(
        GrimoireOfflineTransitionLifecycleStore lifecycle,
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionTypedPublication current)
    {

        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

        _heldInstallationLock = heldInstallationLock
            ?? throw new ArgumentNullException(nameof(heldInstallationLock));

        _current = current ?? throw new ArgumentNullException(nameof(current));

    }

    /// <summary>The publication a caller resumes from, and the only one it may reason about.</summary>
    internal GrimoireOfflineTransitionTypedPublication Current => _current;

    /// <summary>
    /// How far the reconciliation suffix has been published, or null before it opens.
    /// </summary>
    /// <remarks>
    /// Exposed so a resumed run can rejoin the suffix where it stopped rather than replaying it. The
    /// steps are strictly ordered and each is one revision, so the recorded value is the whole answer
    /// to what still has to happen.
    /// </remarks>
    internal GrimoireOfflineTransitionReconciliationStep? ReconciliationStep =>
        _current.Payload.Lifecycle.ReconciliationEvidence?.Step;

    /// <summary>The last phase this transition proved complete.</summary>
    internal CovenantResetPhase LastCompletedPhase => _current.Payload.LastCompletedPhase;

    /// <summary>The phase that was in flight when this transition last published, if any.</summary>
    internal CovenantResetPhase? InFlightPhase => _current.Payload.InFlightPhase;

    /// <summary>Where the shared lifecycle stands.</summary>
    internal GrimoireOfflineTransitionState State => _current.Payload.Lifecycle.State;

    /// <summary>What the registry's handler makes of the current publication.</summary>
    internal GrimoireOfflineTransitionHandlerOutcome Outcome => _current.Outcome;

    /// <summary>
    /// Whether the healthy-catalog factory erasure's ordinary continuation has run.
    /// </summary>
    /// <remarks>
    /// A one-way sub-state rather than an inference from the phase window. The continuation sits
    /// between two phases rather than being one, so "did it run" was previously answered by comparing
    /// the recorded phase against the phase after it — which is the same answer for a run that
    /// completed the continuation and one that crashed before starting it.
    /// </remarks>
    internal bool OrdinaryFactoryContinuationCompleted =>
        _current.Payload is HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 { OrdinaryFactoryContinuationCompleted: true };

    /// <summary>Moves from the opening publication into closing, changing nothing else.</summary>
    internal Task<Result> EnterClosingAsync(CancellationToken cancellationToken) =>
        AdvanceAsync(
            payload => WithLifecycle(
                payload,
                payload.Lifecycle with { State = GrimoireOfflineTransitionState.Closing }),
            cancellationToken);

    /// <summary>
    /// Records that admission is denied, work is drained, and no handle or pool can race the effect.
    /// </summary>
    /// <remarks>
    /// The closed generation is the launch's own source generation and no other. A different one
    /// would be a proof about a database this transition was not launched against, which authorizes
    /// neither the effects that follow nor the rollback that might replace them.
    /// </remarks>
    internal Task<Result> RecordClosedAsync(CancellationToken cancellationToken) =>
        AdvanceAsync(
            payload => WithLifecycle(
                payload,
                payload.Lifecycle with
                {
                    ClosingEvidence = new GrimoireOfflineTransitionClosingEvidence(
                        AdmissionDenied: true,
                        RequestWorkDrained: true,
                        OpenAttemptsResolved: true,
                        HandlesAndPoolsClosed: true,
                        ClosedGenerationProved: true,
                        ClosedDatasetGeneration: payload.Binding.SourceDatasetGeneration),
                }),
            cancellationToken);

    /// <summary>Moves from a complete closing proof into the phase ladder.</summary>
    internal Task<Result> EnterApplyingAsync(CancellationToken cancellationToken) =>
        AdvanceAsync(
            payload => WithLifecycle(
                payload,
                payload.Lifecycle with { State = GrimoireOfflineTransitionState.Applying }),
            cancellationToken);

    /// <summary>Publishes that one phase is about to run, before anything it does can happen.</summary>
    internal Task<Result> BeginPhaseAsync(
        CovenantResetPhase phase,
        CancellationToken cancellationToken) =>
        AdvanceAsync(
            payload => WithPhases(
                payload,
                payload.LastCompletedPhase,
                phase,
                BeforeState(payload.Binding, phase)),
            cancellationToken);

    /// <summary>Publishes that the phase in flight is proved complete.</summary>
    internal Task<Result> CompletePhaseAsync(
        CovenantResetPhase phase,
        CancellationToken cancellationToken) =>
        AdvanceAsync(
            payload => WithPhases(payload, phase, inFlightPhase: null, inFlightBeforeState: null),
            cancellationToken);

    /// <summary>Records the one-way fact that the ordinary factory continuation has run.</summary>
    internal Task<Result> RecordFactoryContinuationAsync(CancellationToken cancellationToken) =>
        AdvanceAsync(WithFactoryContinuation, cancellationToken);

    /// <summary>
    /// Selects the one terminal intent this transition will ever carry, and prepares the reopen.
    /// </summary>
    /// <remarks>
    /// Selected here rather than at the disposition because this is the edge that proves which one is
    /// legal: arriving from a complete phase ladder is a commit, and arriving from a closing proof
    /// with no effect behind it is a rollback. Deciding later would mean deciding from state that no
    /// longer distinguishes them.
    /// </remarks>
    internal Task<Result> PrepareReopenAsync(
        GrimoireOfflineTransitionTerminalIntent intent,
        CancellationToken cancellationToken) =>
        intent is not GrimoireOfflineTransitionTerminalIntent.CommitAndReopen
            and not GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen
            ? Task.FromResult(Unresumable())
            : AdvanceAsync(
                payload => WithLifecycle(
                    payload,
                    payload.Lifecycle with
                    {
                        State = GrimoireOfflineTransitionState.ReopenPrepared,
                        TerminalIntent = intent,
                    }),
                cancellationToken);

    /// <summary>Moves into the private verification lane, changing nothing else.</summary>
    internal Task<Result> EnterVerifyingAsync(CancellationToken cancellationToken) =>
        AdvanceAsync(
            payload => WithLifecycle(
                payload,
                payload.Lifecycle with { State = GrimoireOfflineTransitionState.Verifying }),
            cancellationToken);

    /// <summary>
    /// Advances the verification evidence, which is this phase's in-flight and completed protocol.
    /// </summary>
    /// <remarks>
    /// Three monotone flags rather than a separate in-flight phase: the lane opening, the candidate
    /// proof and the live authority publication each become true exactly once and never go back, so
    /// the revision that carries the second one is itself the record that the first completed. A
    /// separate in-flight shape would need a payload version to carry it and would prove nothing the
    /// ordering does not already.
    /// </remarks>
    internal Task<Result> RecordVerificationAsync(
        bool maintenanceLaneOpened,
        bool candidateVerified,
        bool runtimeCovenantAuthorityVerified,
        CancellationToken cancellationToken) =>
        AdvanceAsync(
            payload => WithLifecycle(
                payload,
                payload.Lifecycle with
                {
                    VerificationEvidence = new GrimoireOfflineTransitionVerificationEvidence(
                        maintenanceLaneOpened,
                        candidateVerified,
                        runtimeCovenantAuthorityVerified),
                }),
            cancellationToken);

    /// <summary>Opens the reconciliation suffix on the candidate proof that precedes it.</summary>
    internal Task<Result> BeginReconciliationAsync(CancellationToken cancellationToken) =>
        AdvanceAsync(
            payload => WithLifecycle(
                payload,
                payload.Lifecycle with
                {
                    State = GrimoireOfflineTransitionState.DatabaseReconciliationPending,
                    ReconciliationEvidence = new GrimoireOfflineTransitionReconciliationEvidence(
                        GrimoireOfflineTransitionReconciliationStep.CandidateVerified,
                        DatabaseTerminalWinnerDigest: null,
                        ParentReceiptNotRequired: false,
                        ParentReceiptDigest: null,
                        LaneClosed: false,
                        CovenantDispositionIntent: null),
                }),
            cancellationToken);

    /// <summary>Records the exact terminal row this transition reconciled against.</summary>
    internal Task<Result> RecordTerminalWinnerAsync(
        CovenantDigest winnerDigest,
        CancellationToken cancellationToken) =>
        AdvanceReconciliationAsync(
            GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner,
            evidence => evidence with { DatabaseTerminalWinnerDigest = winnerDigest },
            cancellationToken);

    /// <summary>
    /// Records the parent receipt, or that this transition is not the nested arm of one.
    /// </summary>
    /// <remarks>
    /// The absent case is stated rather than left blank. A step that simply carried no receipt would
    /// read identically whether none was required or one was owed and never published, and only the
    /// first of those may go on to retire the journal.
    /// </remarks>
    internal Task<Result> RecordParentReceiptAsync(CancellationToken cancellationToken) =>
        AdvanceReconciliationAsync(
            GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied,
            evidence => _current.Payload.Binding.ParentReceiptBindingDigest is { } parent
                ? evidence with { ParentReceiptNotRequired = false, ParentReceiptDigest = parent }
                : evidence with { ParentReceiptNotRequired = true, ParentReceiptDigest = null },
            cancellationToken);

    /// <summary>Records that every maintenance handle is physically closed and the lane is empty.</summary>
    internal Task<Result> RecordLaneClosedAsync(CancellationToken cancellationToken) =>
        AdvanceReconciliationAsync(
            GrimoireOfflineTransitionReconciliationStep.LaneClosed,
            evidence => evidence with { LaneClosed = true },
            cancellationToken);

    /// <summary>Publishes the one Covenant disposition as in flight, before it is spent.</summary>
    internal Task<Result> BeginCovenantDispositionAsync(CancellationToken cancellationToken) =>
        AdvanceReconciliationAsync(
            GrimoireOfflineTransitionReconciliationStep.CovenantDispositionInFlight,
            evidence => evidence with
            {
                CovenantDispositionIntent = _current.Payload.Lifecycle.TerminalIntent,
            },
            cancellationToken);

    /// <summary>Publishes that the one Covenant disposition was spent and verified.</summary>
    internal Task<Result> CompleteCovenantDispositionAsync(CancellationToken cancellationToken) =>
        AdvanceReconciliationAsync(
            GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified,
            evidence => evidence,
            cancellationToken);

    /// <summary>Moves to retirement-pending once the whole suffix is proved.</summary>
    internal Task<Result> PrepareRetirementAsync(CancellationToken cancellationToken) =>
        AdvanceAsync(
            payload => WithLifecycle(
                payload,
                payload.Lifecycle with { State = GrimoireOfflineTransitionState.RetirementPending }),
            cancellationToken);

    /// <summary>
    /// Parks the transition with a content-free blocker and the exact state a resume must reach.
    /// </summary>
    /// <remarks>
    /// The two digests are stored separately because they answer different questions, and because a
    /// resume that echoed the recorded value back would be asserting equality with itself.
    ///
    /// <para>Both are currently derived from the binding and the phase this transition stopped at,
    /// which binds the blocker to exactly this transition and no further. That is all they can carry
    /// until something resumes from a park: the resume side is what recomputes an expected state from
    /// what it actually observes, and deriving a richer value here before that exists would be
    /// laying a trap for whoever writes it — a digest over an observation nothing checks reads as a
    /// proof and is not one.</para>
    /// </remarks>
    internal Task<Result> ParkAsync(CancellationToken cancellationToken)
    {

        GrimoireOfflineTransitionState resumeState = _current.Payload.Lifecycle.State;

        CovenantResetPhase phase = _current.Payload.LastCompletedPhase;

        CovenantDigest resolutionBindingDigest = PhaseDigest(
            BlockerResolutionDomain,
            _current.Payload.Binding,
            phase);

        CovenantDigest expectedStateDigest = PhaseDigest(
            BlockerExpectedStateDomain,
            _current.Payload.Binding,
            phase);

        // Any earlier resolution is dropped with the same revision that records the new park. A park
        // is a question waiting to be answered, and a proof left lying in it would be the answer to a
        // park that has already been lifted - which the lifecycle refuses to hold, and rightly: the
        // next resume has to produce its own proof rather than inherit one.
        return AdvanceAsync(
            payload => WithClearedBlockerResolution(
                payload,
                payload.Lifecycle with
                {
                    State = GrimoireOfflineTransitionState.KeepClosed,
                    Blocker = new GrimoireOfflineTransitionBlocker(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        resumeState,
                        resolutionBindingDigest,
                        expectedStateDigest),
                }),
            cancellationToken);

    }

    /// <summary>
    /// Lifts a park, returning the transition to the state it was parked from.
    /// </summary>
    /// <remarks>
    /// Parking is what a transition does when it cannot finish and must not guess; it is not an
    /// ending. Without a way back out the journal would keep an installation closed for a reason that
    /// may have already gone away — a store that refused one write, a lock somebody else was holding
    /// — and the only remedy left would be to discard the journal, which is the one record of what
    /// this erasure did.
    ///
    /// <para>The proof is recomputed rather than remembered. Both blocker digests are functions of
    /// the binding and the phase the park recorded, so a process that comes back with the same
    /// journal derives the same pair, and one holding a different journal cannot. That is the whole
    /// check: it says this is the transition that parked and it is resuming at the point it parked
    /// from, and it deliberately says nothing about whether the underlying obstacle has cleared,
    /// because the resumed work re-establishes that for itself before it does anything.</para>
    /// </remarks>
    internal Task<Result> ResumeFromParkAsync(CancellationToken cancellationToken)
    {

        if (_current.Payload.Lifecycle is not
            {
                State: GrimoireOfflineTransitionState.KeepClosed,
                Blocker: { } blocker,
            })
        {

            return Task.FromResult(
                Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.LifecycleConflict,
                        "Only a parked offline transition can be resumed from a park.")));

        }

        CovenantDigest resolutionBindingDigest = PhaseDigest(
            BlockerResolutionDomain,
            _current.Payload.Binding,
            _current.Payload.LastCompletedPhase);

        CovenantDigest expectedStateDigest = PhaseDigest(
            BlockerExpectedStateDomain,
            _current.Payload.Binding,
            _current.Payload.LastCompletedPhase);

        return AdvanceAsync(
            payload => WithResolvedBlocker(
                payload,
                payload.Lifecycle with
                {
                    State = blocker.ResumeState,
                    Blocker = null,
                },
                resolutionBindingDigest,
                expectedStateDigest),
            cancellationToken);

    }

    /// <summary>Retires the journal, which is legal only from an exact retirement-pending suffix.</summary>
    internal Task<Result> RetireAsync(CancellationToken cancellationToken) =>
        _lifecycle.RetireAsync(_heldInstallationLock, _current, cancellationToken);

    /// <summary>
    /// The evidence an in-flight publication carries, derived rather than supplied.
    /// </summary>
    /// <remarks>
    /// Derived because a caller-supplied value can be wrong, and this one has exactly one correct
    /// answer: it binds the in-flight record to this transition and this phase, so a payload cannot
    /// carry evidence belonging to another. Two digests rather than one because they answer different
    /// questions - which state this ran against, and which effect it was - and a single value would
    /// collapse a phase that ran against the wrong state into one that ran the wrong effect.
    ///
    /// <para>It deliberately carries nothing observed. What actually happened is resolved against the
    /// database by the launch classifier, which compares the live generation and epoch tuple with the
    /// pair the launch committed to; a digest of a reading taken before the effect could not answer
    /// that, because the reading it describes is the one the effect was about to invalidate.</para>
    /// </remarks>
    private static GrimoireOfflineTransitionBeforeStateEvidence BeforeState(
        GrimoireOfflineTransitionBinding binding,
        CovenantResetPhase phase) =>
        new(
            PhaseDigest(SourceStateDomain, binding, phase),
            PhaseDigest(EffectEvidenceDomain, binding, phase));

    private static CovenantDigest PhaseDigest(
        string domain,
        GrimoireOfflineTransitionBinding binding,
        CovenantResetPhase phase)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(domain));

        hash.AppendData([0]);

        hash.AppendData(binding.OperationId.ToByteArray(bigEndian: true));

        hash.AppendData([(byte)binding.Kind, binding.PayloadVersion, (byte)phase]);

        hash.AppendData(binding.EffectDigest.Bytes);

        hash.AppendData(binding.SourceDatasetGeneration.ToByteArray(bigEndian: true));

        hash.AppendData(binding.TargetDatasetGeneration.ToByteArray(bigEndian: true));

        return new CovenantDigest(hash.GetHashAndReset());

    }

    private Task<Result> AdvanceReconciliationAsync(
        GrimoireOfflineTransitionReconciliationStep step,
        Func<GrimoireOfflineTransitionReconciliationEvidence, GrimoireOfflineTransitionReconciliationEvidence> advance,
        CancellationToken cancellationToken) =>
        _current.Payload.Lifecycle.ReconciliationEvidence is not { } evidence
            ? Task.FromResult(Unresumable())
            : AdvanceAsync(
                payload => WithLifecycle(
                    payload,
                    payload.Lifecycle with
                    {
                        ReconciliationEvidence = advance(evidence) with { Step = step },
                    }),
                cancellationToken);

    private async Task<Result> AdvanceAsync(
        Func<IGrimoireOfflineTransitionPayload, Result<IGrimoireOfflineTransitionPayload>> rewrite,
        CancellationToken cancellationToken)
    {

        Result<IGrimoireOfflineTransitionPayload> next = rewrite(_current.Payload);

        if (next.IsFailure)
        {

            return Result.Failure(next.Error);

        }

        Result<GrimoireOfflineTransitionTypedPublication> advanced = await _lifecycle
            .AdvanceAsync(_heldInstallationLock, _current, next.Value, cancellationToken)
            .ConfigureAwait(false);

        if (advanced.IsFailure)
        {

            return Result.Failure(advanced.Error);

        }

        _current = advanced.Value;

        return Result.Success();

    }

    /// <summary>
    /// Replaces the shared lifecycle, leaving everything a payload's kind owns exactly as it was.
    /// </summary>
    /// <remarks>
    /// A type switch rather than an interface member, because the two payloads are separate strict
    /// records on purpose: there is deliberately no universal cross-kind shape, and adding a mutation
    /// member to the interface would create one. An unrecognized payload refuses rather than passing
    /// through — a shape this build cannot rewrite is one it cannot claim to have advanced.
    /// </remarks>
    private static Result<IGrimoireOfflineTransitionPayload> WithLifecycle(
        IGrimoireOfflineTransitionPayload payload,
        GrimoireOfflineTransitionLifecycle lifecycle) =>
        payload switch
        {

            CovenantResetOfflineTransitionPayloadV1 reset =>
                Result<IGrimoireOfflineTransitionPayload>.Success(reset with { Lifecycle = lifecycle }),

            HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factory =>
                Result<IGrimoireOfflineTransitionPayload>.Success(factory with { Lifecycle = lifecycle }),

            _ => Unresumable<IGrimoireOfflineTransitionPayload>(),

        };

    /// <summary>
    /// Publishes a resumed lifecycle together with the proof that admits the resumption.
    /// </summary>
    /// <remarks>
    /// One rewrite rather than two, because the validator reads the state change and the proof in the
    /// same revision: a payload that moved out of a park without carrying the evidence is refused,
    /// and one that carried the evidence without moving would be recording a resolution for a park it
    /// is still sitting in.
    /// </remarks>
    private static Result<IGrimoireOfflineTransitionPayload> WithResolvedBlocker(
        IGrimoireOfflineTransitionPayload payload,
        GrimoireOfflineTransitionLifecycle lifecycle,
        CovenantDigest resolutionBindingDigest,
        CovenantDigest expectedStateDigest) =>
        payload switch
        {

            CovenantResetOfflineTransitionPayloadV1 reset =>
                Result<IGrimoireOfflineTransitionPayload>.Success(
                    reset with
                    {
                        Lifecycle = lifecycle,
                        BlockerResolutionEvidence = new CovenantResetBlockerResolutionEvidence(
                            resolutionBindingDigest,
                            expectedStateDigest),
                    }),

            HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factory =>
                Result<IGrimoireOfflineTransitionPayload>.Success(
                    factory with
                    {
                        Lifecycle = lifecycle,
                        BlockerResolutionEvidence =
                            new HealthyCatalogFactoryErasureBlockerResolutionEvidence(
                                resolutionBindingDigest,
                                expectedStateDigest),
                    }),

            _ => Unresumable<IGrimoireOfflineTransitionPayload>(),

        };

    /// <summary>Publishes a parked lifecycle, dropping any resolution the previous park was given.</summary>
    private static Result<IGrimoireOfflineTransitionPayload> WithClearedBlockerResolution(
        IGrimoireOfflineTransitionPayload payload,
        GrimoireOfflineTransitionLifecycle lifecycle) =>
        payload switch
        {

            CovenantResetOfflineTransitionPayloadV1 reset =>
                Result<IGrimoireOfflineTransitionPayload>.Success(
                    reset with { Lifecycle = lifecycle, BlockerResolutionEvidence = null }),

            HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factory =>
                Result<IGrimoireOfflineTransitionPayload>.Success(
                    factory with { Lifecycle = lifecycle, BlockerResolutionEvidence = null }),

            _ => Unresumable<IGrimoireOfflineTransitionPayload>(),

        };

    private static Result<IGrimoireOfflineTransitionPayload> WithPhases(
        IGrimoireOfflineTransitionPayload payload,
        CovenantResetPhase lastCompletedPhase,
        CovenantResetPhase? inFlightPhase,
        GrimoireOfflineTransitionBeforeStateEvidence? inFlightBeforeState) =>
        payload switch
        {

            CovenantResetOfflineTransitionPayloadV1 reset =>
                Result<IGrimoireOfflineTransitionPayload>.Success(
                    reset with
                    {
                        LastCompletedPhase = lastCompletedPhase,
                        InFlightPhase = inFlightPhase,
                        InFlightBeforeState = inFlightBeforeState,
                    }),

            HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factory =>
                Result<IGrimoireOfflineTransitionPayload>.Success(
                    factory with
                    {
                        LastCompletedPhase = lastCompletedPhase,
                        InFlightPhase = inFlightPhase,
                        InFlightBeforeState = inFlightBeforeState,
                    }),

            _ => Unresumable<IGrimoireOfflineTransitionPayload>(),

        };

    /// <summary>
    /// Advances the factory continuation flag, which only a factory erasure has.
    /// </summary>
    /// <remarks>
    /// A Covenant reset refuses rather than silently succeeding. Its payload has nowhere to record
    /// the fact, and a caller asking a reset to remember it has confused the two kinds — which is
    /// worth a refusal, because the kind decides what an erasure preserves.
    /// </remarks>
    private static Result<IGrimoireOfflineTransitionPayload> WithFactoryContinuation(
        IGrimoireOfflineTransitionPayload payload) =>
        payload is HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factory
            ? Result<IGrimoireOfflineTransitionPayload>.Success(
                factory with { OrdinaryFactoryContinuationCompleted = true })
            : Unresumable<IGrimoireOfflineTransitionPayload>();

    private const string BlockerResolutionDomain =
        "arcanum.grimoire.offline-transition.blocker-resolution.v1";

    private const string BlockerExpectedStateDomain =
        "arcanum.grimoire.offline-transition.blocker-expected-state.v1";

    private const string SourceStateDomain =
        "arcanum.grimoire.offline-transition.phase-source-state.v1";

    private const string EffectEvidenceDomain =
        "arcanum.grimoire.offline-transition.phase-effect-evidence.v1";

    private static Result Unresumable() => new Error(
        ErrorCodes.Covenant.ManualRecoveryRequired,
        "The authenticated offline transition payload cannot be recovered by this build.");

    private static Result<T> Unresumable<T>() => Result<T>.Failure(Unresumable().Error);

}
