using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

internal enum GrimoireOfflineTransitionState : byte
{

    Prepared = 1,

    Closing = 2,

    Applying = 3,

    ReopenPrepared = 4,

    Verifying = 5,

    DatabaseReconciliationPending = 6,

    KeepClosed = 7,

    RetirementPending = 8,

}

internal enum GrimoireOfflineTransitionTerminalIntent : byte
{

    Undecided = 1,

    RollbackAndReopen = 2,

    CommitAndReopen = 3,

}

internal enum GrimoireOfflineTransitionHandlerOutcome : byte
{

    NotApplied = 1,

    AppliedAndVerified = 2,

    ReconciliationPending = 3,

    KeepClosed = 4,

}

internal enum GrimoireOfflineTransitionReconciliationStep : byte
{

    CandidateVerified = 1,

    DatabaseTerminalWinner = 2,

    ParentReceiptSatisfied = 3,

    LaneClosed = 4,

    CovenantDispositionInFlight = 5,

    CovenantDispositionVerified = 6,

}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionEpochTuple(
    ulong AcceleratorEpoch,
    ulong KeyReclamationEpoch,
    ulong EnvelopeKeyEpoch);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionBinding(
    Guid OperationId,
    GrimoireOfflineTransitionKind Kind,
    byte PayloadVersion,
    ulong SlotEpoch,
    CovenantDigest EffectDigest,
    Guid SourceDatasetGeneration,
    Guid TargetDatasetGeneration,
    GrimoireOfflineTransitionEpochTuple SourceEpochs,
    GrimoireOfflineTransitionEpochTuple TargetEpochs,
    CovenantDigest DatabaseOperationLaunchBindingDigest,
    ulong ExpectedDatabaseOperationRevision,
    CovenantDigest? ParentReceiptBindingDigest);

internal readonly record struct GrimoireOfflineTransitionAuthenticatedBinding(
    Guid OperationId,
    GrimoireOfflineTransitionKind Kind,
    byte PayloadVersion,
    ulong SlotEpoch);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionClosingEvidence(
    bool AdmissionDenied,
    bool RequestWorkDrained,
    bool OpenAttemptsResolved,
    bool HandlesAndPoolsClosed,
    bool ClosedGenerationProved,
    Guid? ClosedDatasetGeneration)
{

    internal bool IsComplete => AdmissionDenied
        && RequestWorkDrained
        && OpenAttemptsResolved
        && HandlesAndPoolsClosed
        && ClosedGenerationProved
        && ClosedDatasetGeneration is not null;

}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionVerificationEvidence(
    bool MaintenanceLaneOpened,
    bool CandidateVerified,
    bool RuntimeCovenantAuthorityVerified)
{

    internal bool IsComplete => MaintenanceLaneOpened
        && CandidateVerified
        && RuntimeCovenantAuthorityVerified;

}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionReconciliationEvidence(
    GrimoireOfflineTransitionReconciliationStep Step,
    CovenantDigest? DatabaseTerminalWinnerDigest,
    bool ParentReceiptNotRequired,
    CovenantDigest? ParentReceiptDigest,
    bool LaneClosed,
    GrimoireOfflineTransitionTerminalIntent? CovenantDispositionIntent)
{

    internal bool IsComplete =>
        Step is GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified
        && DatabaseTerminalWinnerDigest is { IsValid: true }
        && (ParentReceiptNotRequired || ParentReceiptDigest is { IsValid: true })
        && LaneClosed
        && CovenantDispositionIntent is not null
            and not GrimoireOfflineTransitionTerminalIntent.Undecided;

}

/// <summary>
/// Why a lifecycle stayed closed, and what a later resume has to prove before it may reopen.
/// </summary>
/// <remarks>
/// Nothing under <c>src/</c> constructs one. The lifecycle store this record belongs to is
/// deliberately not activated on any production path, so every instance that exists today comes
/// from a test or from deserializing a payload a test wrote. That is the state this type is
/// designed for rather than an omission: the shape is settled and validated ahead of the producer
/// that will eventually mint it.
///
/// <para><see cref="ExpectedStateDigest"/> is the digest a future resume producer must recompute
/// from the state it actually observes, never copy forward out of the blocker it is answering. The
/// lifecycle validator admits a resolution only when the proof's own canonical (or healthy-catalog)
/// state digest equals this value, so a producer that echoes the stored digest back asserts
/// equality with itself and proves nothing.</para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionBlocker(
    string ErrorCode,
    GrimoireOfflineTransitionState ResumeState,
    CovenantDigest ResolutionBindingDigest,
    CovenantDigest ExpectedStateDigest);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CovenantResetBlockerResolutionEvidence(
    CovenantDigest ResolutionBindingDigest,
    CovenantDigest CanonicalStateDigest);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HealthyCatalogFactoryErasureBlockerResolutionEvidence(
    CovenantDigest ResolutionBindingDigest,
    CovenantDigest HealthyCatalogStateDigest);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionLifecycle(
    GrimoireOfflineTransitionState State,
    GrimoireOfflineTransitionTerminalIntent TerminalIntent,
    GrimoireOfflineTransitionClosingEvidence ClosingEvidence,
    GrimoireOfflineTransitionVerificationEvidence VerificationEvidence,
    GrimoireOfflineTransitionReconciliationEvidence? ReconciliationEvidence,
    GrimoireOfflineTransitionBlocker? Blocker);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionBeforeStateEvidence(
    CovenantDigest SourceStateDigest,
    CovenantDigest EffectEvidenceDigest);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GrimoireOfflineTransitionReplacementEvidence(
    string StagingLeaf,
    CovenantDigest SourcePhysicalIdentityDigest,
    CovenantDigest? StagingPhysicalIdentityDigest,
    CovenantDigest DestinationPhysicalIdentityDigest,
    CovenantDigest OriginalBackupPhysicalIdentityDigest,
    CovenantDigest? StagedContentDigest);

internal interface IGrimoireOfflineTransitionPayload
{

    GrimoireOfflineTransitionBinding Binding { get; }

    GrimoireOfflineTransitionLifecycle Lifecycle { get; }

    CovenantResetPhase LastCompletedPhase { get; }

    CovenantResetPhase? InFlightPhase { get; }

    GrimoireOfflineTransitionBeforeStateEvidence? InFlightBeforeState { get; }

    GrimoireOfflineTransitionReplacementEvidence? ReplacementEvidence { get; }

}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CovenantResetOfflineTransitionPayloadV1(
    GrimoireOfflineTransitionBinding Binding,
    GrimoireOfflineTransitionLifecycle Lifecycle,
    CovenantResetPhase LastCompletedPhase,
    CovenantResetPhase? InFlightPhase,
    GrimoireOfflineTransitionBeforeStateEvidence? InFlightBeforeState,
    GrimoireOfflineTransitionReplacementEvidence? ReplacementEvidence,
    CovenantResetBlockerResolutionEvidence? BlockerResolutionEvidence = null)
    : IGrimoireOfflineTransitionPayload;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HealthyCatalogFactoryErasureOfflineTransitionPayloadV1(
    GrimoireOfflineTransitionBinding Binding,
    GrimoireOfflineTransitionLifecycle Lifecycle,
    CovenantResetPhase LastCompletedPhase,
    CovenantResetPhase? InFlightPhase,
    GrimoireOfflineTransitionBeforeStateEvidence? InFlightBeforeState,
    GrimoireOfflineTransitionReplacementEvidence? ReplacementEvidence,
    bool OrdinaryFactoryContinuationCompleted,
    HealthyCatalogFactoryErasureBlockerResolutionEvidence? BlockerResolutionEvidence = null)
    : IGrimoireOfflineTransitionPayload;
