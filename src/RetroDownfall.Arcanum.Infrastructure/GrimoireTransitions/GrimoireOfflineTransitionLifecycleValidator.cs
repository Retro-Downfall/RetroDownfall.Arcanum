using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

internal interface IGrimoireOfflineTransitionHandler
{

    GrimoireOfflineTransitionKind Kind { get; }

    byte PayloadVersion { get; }

    Result<IGrimoireOfflineTransitionPayload> Decode(
        ReadOnlySpan<byte> payloadBytes,
        GrimoireOfflineTransitionAuthenticatedBinding expectedBinding);

    Result<byte[]> Encode(IGrimoireOfflineTransitionPayload payload);

    Result ValidateAdvance(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next);

    GrimoireOfflineTransitionHandlerOutcome ResolveOutcome(
        IGrimoireOfflineTransitionPayload payload);

}

internal sealed partial class CovenantResetOfflineTransitionHandlerV1
{

    internal Result ValidateAdvance(
        CovenantResetOfflineTransitionPayloadV1 current,
        CovenantResetOfflineTransitionPayloadV1 next)
    {

        bool proved = current.Lifecycle.State is GrimoireOfflineTransitionState.KeepClosed
            && current.Lifecycle.Blocker is { } blocker
            && next.BlockerResolutionEvidence is { } proof
            && proof.ResolutionBindingDigest == blocker.ResolutionBindingDigest
            && proof.CanonicalStateDigest == blocker.ResolutionBindingDigest;

        bool evidencePreserved = current.BlockerResolutionEvidence
                == next.BlockerResolutionEvidence
            || proved && current.BlockerResolutionEvidence is null;

        return GrimoireOfflineTransitionLifecycleValidator.ValidateAdvance(
            current,
            next,
            evidencePreserved,
            kindEvidenceAdvanced: false,
            blockerResolutionProved: proved);

    }

    internal GrimoireOfflineTransitionHandlerOutcome ResolveOutcome(
        CovenantResetOfflineTransitionPayloadV1 payload) =>
        GrimoireOfflineTransitionLifecycleValidator.ResolveOutcome(payload);

}

internal sealed partial class HealthyCatalogFactoryErasureOfflineTransitionHandlerV1
{

    internal Result ValidateAdvance(
        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 current,
        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 next)
    {

        bool proved = current.Lifecycle.State is GrimoireOfflineTransitionState.KeepClosed
            && current.Lifecycle.Blocker is { } blocker
            && next.BlockerResolutionEvidence is { } proof
            && proof.ResolutionBindingDigest == blocker.ResolutionBindingDigest
            && proof.HealthyCatalogStateDigest == blocker.ResolutionBindingDigest;

        bool continuationPreserved = current.OrdinaryFactoryContinuationCompleted
            == next.OrdinaryFactoryContinuationCompleted;

        bool continuationAdvanced = !current.OrdinaryFactoryContinuationCompleted
            && next.OrdinaryFactoryContinuationCompleted
            && current.Lifecycle.State is GrimoireOfflineTransitionState.Applying
            && next.Lifecycle.State is GrimoireOfflineTransitionState.Applying
            && current.LastCompletedPhase is CovenantResetPhase.ManagedArtifactsProcessed
            && next.LastCompletedPhase == current.LastCompletedPhase
            && current.InFlightPhase is null
            && next.InFlightPhase is null;

        bool evidencePreserved = continuationPreserved
            && (current.BlockerResolutionEvidence == next.BlockerResolutionEvidence
                || proved && current.BlockerResolutionEvidence is null);

        return GrimoireOfflineTransitionLifecycleValidator.ValidateAdvance(
            current,
            next,
            evidencePreserved,
            continuationAdvanced
                && current.BlockerResolutionEvidence == next.BlockerResolutionEvidence,
            proved);

    }

    internal GrimoireOfflineTransitionHandlerOutcome ResolveOutcome(
        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 payload) =>
        GrimoireOfflineTransitionLifecycleValidator.ResolveOutcome(payload);

}

internal static class GrimoireOfflineTransitionLifecycleValidator
{

    private static readonly GrimoireOfflineTransitionClosingEvidence EmptyClosing =
        new(false, false, false, false, false, null);

    private static readonly GrimoireOfflineTransitionVerificationEvidence EmptyVerification =
        new(false, false, false);

    internal static Result ValidateAdvance(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next,
        bool kindEvidencePreserved,
        bool kindEvidenceAdvanced,
        bool blockerResolutionProved)
    {

        if (!ValidPayload(current)
            || !ValidPayload(next)
            || current.Binding != next.Binding
            || !kindEvidencePreserved && !kindEvidenceAdvanced
            || !ValidTerminalIntentAdvance(current.Lifecycle, next.Lifecycle))
        {

            return Conflict();

        }

        GrimoireOfflineTransitionState from = current.Lifecycle.State;

        GrimoireOfflineTransitionState to = next.Lifecycle.State;

        bool valid = (from, to) switch
        {
            (GrimoireOfflineTransitionState.Prepared,
                GrimoireOfflineTransitionState.Closing) => SameEvidence(current, next),
            (GrimoireOfflineTransitionState.Closing,
                GrimoireOfflineTransitionState.Closing) => ClosingAdvances(current, next),
            (GrimoireOfflineTransitionState.Closing,
                GrimoireOfflineTransitionState.Applying) =>
                current.Lifecycle.ClosingEvidence.IsComplete && SameEvidence(current, next),
            (GrimoireOfflineTransitionState.Closing,
                GrimoireOfflineTransitionState.ReopenPrepared) =>
                current.Lifecycle.ClosingEvidence.IsComplete
                && current.LastCompletedPhase is CovenantResetPhase.InventoryPrepared
                && next.Lifecycle.TerminalIntent
                    is GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen
                && SameEvidence(current, next),
            (GrimoireOfflineTransitionState.Applying,
                GrimoireOfflineTransitionState.Applying) =>
                kindEvidencePreserved && ApplyingAdvances(current, next)
                || kindEvidenceAdvanced && SameEvidence(current, next),
            (GrimoireOfflineTransitionState.Applying,
                GrimoireOfflineTransitionState.ReopenPrepared) =>
                current.LastCompletedPhase is CovenantResetPhase.SidecarsVerified
                && current.InFlightPhase is null
                && next.Lifecycle.TerminalIntent
                    is GrimoireOfflineTransitionTerminalIntent.CommitAndReopen
                && SameEvidence(current, next),
            (GrimoireOfflineTransitionState.ReopenPrepared,
                GrimoireOfflineTransitionState.Verifying) => SameEvidence(current, next),
            (GrimoireOfflineTransitionState.Verifying,
                GrimoireOfflineTransitionState.Verifying) => VerificationAdvances(current, next),
            (GrimoireOfflineTransitionState.Verifying,
                GrimoireOfflineTransitionState.DatabaseReconciliationPending) =>
                current.Lifecycle.VerificationEvidence.IsComplete
                && SameEffectAndClosingEvidence(current, next)
                && current.Lifecycle.VerificationEvidence
                    == next.Lifecycle.VerificationEvidence
                && current.Lifecycle.ReconciliationEvidence is null
                && IsOpeningReconciliation(next),
            (GrimoireOfflineTransitionState.DatabaseReconciliationPending,
                GrimoireOfflineTransitionState.DatabaseReconciliationPending) =>
                ReconciliationAdvances(current, next),
            (GrimoireOfflineTransitionState.DatabaseReconciliationPending,
                GrimoireOfflineTransitionState.RetirementPending) =>
                ReconciliationComplete(current) && SameEvidence(current, next),
            (_, GrimoireOfflineTransitionState.KeepClosed) =>
                CanEnterKeepClosed(current, next),
            (GrimoireOfflineTransitionState.KeepClosed, _) =>
                CanResumeFromKeepClosed(current, next, blockerResolutionProved),
            _ => false,
        };

        return valid ? Result.Success() : Conflict();

    }

    internal static GrimoireOfflineTransitionHandlerOutcome ResolveOutcome(
        IGrimoireOfflineTransitionPayload payload) => payload.Lifecycle.State switch
        {
            GrimoireOfflineTransitionState.KeepClosed =>
                GrimoireOfflineTransitionHandlerOutcome.KeepClosed,
            GrimoireOfflineTransitionState.DatabaseReconciliationPending
                or GrimoireOfflineTransitionState.RetirementPending =>
                GrimoireOfflineTransitionHandlerOutcome.ReconciliationPending,
            GrimoireOfflineTransitionState.Verifying
                when payload.Lifecycle.VerificationEvidence.IsComplete =>
                GrimoireOfflineTransitionHandlerOutcome.AppliedAndVerified,
            _ => GrimoireOfflineTransitionHandlerOutcome.NotApplied,
        };

    internal static bool RetirementReady(IGrimoireOfflineTransitionPayload payload) =>
        payload.Lifecycle.State is GrimoireOfflineTransitionState.RetirementPending
        && ReconciliationComplete(payload);

    private static bool ReconciliationComplete(IGrimoireOfflineTransitionPayload payload) =>
        payload.Lifecycle.ReconciliationEvidence is { } evidence
        && evidence.Step
            is GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified
        && EvidenceMatchesStep(payload, evidence);

    internal static bool ValidPayload(IGrimoireOfflineTransitionPayload payload)
    {

        if (payload is null
            || payload.Binding is null
            || payload.Lifecycle is null
            || payload.Binding.OperationId == Guid.Empty
            || !Enum.IsDefined(payload.Binding.Kind)
            || payload.Binding.PayloadVersion == 0
            || payload.Binding.SlotEpoch == 0
            || !payload.Binding.EffectDigest.IsValid
            || payload.Binding.SourceDatasetGeneration == Guid.Empty
            || payload.Binding.TargetDatasetGeneration == Guid.Empty
            || payload.Binding.SourceEpochs is null
            || payload.Binding.TargetEpochs is null
            || !payload.Binding.DatabaseOperationLaunchBindingDigest.IsValid
            || payload.Binding.ParentReceiptBindingDigest is { IsValid: false }
            || !Enum.IsDefined(payload.Lifecycle.State)
            || !Enum.IsDefined(payload.Lifecycle.TerminalIntent)
            || payload.Lifecycle.ClosingEvidence is null
            || payload.Lifecycle.VerificationEvidence is null
            || !CovenantResetPhaseMachine.IsDeclared(payload.LastCompletedPhase)
            || payload.InFlightPhase is { } phase
                && !CovenantResetPhaseMachine.IsDeclared(phase)
            || (payload.InFlightPhase is null) != (payload.InFlightBeforeState is null)
            || payload.InFlightBeforeState is not null
                && (!payload.InFlightBeforeState.SourceStateDigest.IsValid
                    || !payload.InFlightBeforeState.EffectEvidenceDigest.IsValid)
            || !ValidReplacement(payload.ReplacementEvidence)
            || !ValidLifecycle(payload.Lifecycle)
            || !ValidKindEvidence(payload)
            || !StateEvidenceCoherent(payload))
        {

            return false;

        }

        return true;

    }

    private static bool ValidKindEvidence(IGrimoireOfflineTransitionPayload payload) => payload switch
    {
        CovenantResetOfflineTransitionPayloadV1 reset =>
            reset.Lifecycle.State is GrimoireOfflineTransitionState.KeepClosed
                ? reset.BlockerResolutionEvidence is null
                : reset.BlockerResolutionEvidence is null
            || reset.BlockerResolutionEvidence.ResolutionBindingDigest.IsValid
                && reset.BlockerResolutionEvidence.CanonicalStateDigest.IsValid,
        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factory =>
            factory.Lifecycle.State is GrimoireOfflineTransitionState.KeepClosed
                ? factory.BlockerResolutionEvidence is null
                : factory.BlockerResolutionEvidence is null
            || factory.BlockerResolutionEvidence.ResolutionBindingDigest.IsValid
                && factory.BlockerResolutionEvidence.HealthyCatalogStateDigest.IsValid,
        _ => true,
    };

    private static bool StateEvidenceCoherent(IGrimoireOfflineTransitionPayload payload)
    {

        GrimoireOfflineTransitionLifecycle lifecycle = payload.Lifecycle;

        if (!ClosingCoherent(lifecycle.ClosingEvidence)
            || !VerificationCoherent(lifecycle.VerificationEvidence))
        {

            return false;

        }

        GrimoireOfflineTransitionState state = lifecycle.State
            is GrimoireOfflineTransitionState.KeepClosed
            ? lifecycle.Blocker!.ResumeState
            : lifecycle.State;

        if (state is not GrimoireOfflineTransitionState.Applying
            && payload.InFlightPhase is not null)
        {

            return false;

        }

        bool noVerification = lifecycle.VerificationEvidence == EmptyVerification;

        return state switch
        {
            GrimoireOfflineTransitionState.Prepared =>
                lifecycle.TerminalIntent is GrimoireOfflineTransitionTerminalIntent.Undecided
                && lifecycle.ClosingEvidence == EmptyClosing
                && noVerification
                && lifecycle.ReconciliationEvidence is null
                && payload.LastCompletedPhase is CovenantResetPhase.InventoryPrepared
                && payload.ReplacementEvidence is null,
            GrimoireOfflineTransitionState.Closing =>
                lifecycle.TerminalIntent is GrimoireOfflineTransitionTerminalIntent.Undecided
                && noVerification
                && lifecycle.ReconciliationEvidence is null
                && payload.LastCompletedPhase is CovenantResetPhase.InventoryPrepared
                && payload.ReplacementEvidence is null,
            GrimoireOfflineTransitionState.Applying =>
                lifecycle.TerminalIntent is GrimoireOfflineTransitionTerminalIntent.Undecided
                && lifecycle.ClosingEvidence.IsComplete
                && noVerification
                && lifecycle.ReconciliationEvidence is null,
            GrimoireOfflineTransitionState.ReopenPrepared =>
                TerminalShape(payload) && noVerification
                && lifecycle.ReconciliationEvidence is null,
            GrimoireOfflineTransitionState.Verifying =>
                TerminalShape(payload) && lifecycle.ReconciliationEvidence is null,
            GrimoireOfflineTransitionState.DatabaseReconciliationPending =>
                TerminalShape(payload)
                && lifecycle.VerificationEvidence.IsComplete
                && lifecycle.ReconciliationEvidence is { } evidence
                && EvidenceMatchesStep(payload, evidence),
            GrimoireOfflineTransitionState.RetirementPending =>
                TerminalShape(payload)
                && lifecycle.VerificationEvidence.IsComplete
                && RetirementReady(payload),
            _ => false,
        };

    }

    private static bool TerminalShape(IGrimoireOfflineTransitionPayload payload) =>
        payload.Lifecycle.ClosingEvidence.IsComplete
        && payload.InFlightPhase is null
        && payload.Lifecycle.TerminalIntent switch
        {
            GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen =>
                payload.LastCompletedPhase is CovenantResetPhase.InventoryPrepared,
            GrimoireOfflineTransitionTerminalIntent.CommitAndReopen =>
                payload.LastCompletedPhase is CovenantResetPhase.SidecarsVerified,
            _ => false,
        };

    private static bool ClosingCoherent(GrimoireOfflineTransitionClosingEvidence evidence) =>
        (!evidence.RequestWorkDrained || evidence.AdmissionDenied)
        && (!evidence.OpenAttemptsResolved || evidence.RequestWorkDrained)
        && (!evidence.HandlesAndPoolsClosed || evidence.OpenAttemptsResolved)
        && (!evidence.ClosedGenerationProved || evidence.HandlesAndPoolsClosed)
        && evidence.ClosedGenerationProved
            == (evidence.ClosedDatasetGeneration is not null);

    private static bool VerificationCoherent(
        GrimoireOfflineTransitionVerificationEvidence evidence) =>
        (!evidence.CandidateVerified || evidence.MaintenanceLaneOpened)
        && (!evidence.RuntimeCovenantAuthorityVerified || evidence.CandidateVerified);

    private static bool ValidLifecycle(GrimoireOfflineTransitionLifecycle lifecycle)
    {

        if (lifecycle.ReconciliationEvidence is { } reconciliation
            && (!Enum.IsDefined(reconciliation.Step)
                || reconciliation.DatabaseTerminalWinnerDigest is { IsValid: false }
                || reconciliation.ParentReceiptDigest is { IsValid: false }
                || reconciliation.CovenantDispositionIntent
                    is GrimoireOfflineTransitionTerminalIntent.Undecided))
        {

            return false;

        }

        if (lifecycle.Blocker is { } blocker
            && (string.IsNullOrWhiteSpace(blocker.ErrorCode)
                || blocker.ErrorCode.Length > 128
                || blocker.ResumeState is GrimoireOfflineTransitionState.Prepared
                    or GrimoireOfflineTransitionState.KeepClosed
                    or GrimoireOfflineTransitionState.RetirementPending
                || !blocker.ResolutionBindingDigest.IsValid))
        {

            return false;

        }

        return lifecycle.State is GrimoireOfflineTransitionState.KeepClosed
            ? lifecycle.Blocker is not null
            : lifecycle.Blocker is null;

    }

    private static bool ValidReplacement(
        GrimoireOfflineTransitionReplacementEvidence? replacement) => replacement is null
        || !string.IsNullOrWhiteSpace(replacement.StagingLeaf)
            && replacement.StagingLeaf.Length <= 255
            && Path.GetFileName(replacement.StagingLeaf) == replacement.StagingLeaf
            && replacement.SourcePhysicalIdentityDigest.IsValid
            && replacement.StagingPhysicalIdentityDigest is not { IsValid: false }
            && replacement.DestinationPhysicalIdentityDigest.IsValid
            && replacement.OriginalBackupPhysicalIdentityDigest.IsValid
            && replacement.StagedContentDigest is not { IsValid: false };

    private static bool ValidTerminalIntentAdvance(
        GrimoireOfflineTransitionLifecycle current,
        GrimoireOfflineTransitionLifecycle next)
    {

        if (current.TerminalIntent == next.TerminalIntent)
        {

            return true;

        }

        if (current.TerminalIntent is not GrimoireOfflineTransitionTerminalIntent.Undecided)
        {

            return false;

        }

        return (current.State, next.State, next.TerminalIntent) switch
        {
            (GrimoireOfflineTransitionState.Closing,
                GrimoireOfflineTransitionState.ReopenPrepared,
                GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen) => true,
            (GrimoireOfflineTransitionState.Applying,
                GrimoireOfflineTransitionState.ReopenPrepared,
                GrimoireOfflineTransitionTerminalIntent.CommitAndReopen) => true,
            _ => false,
        };

    }

    private static bool ClosingAdvances(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next) =>
        SameEffectEvidence(current, next)
        && current.Lifecycle.VerificationEvidence == next.Lifecycle.VerificationEvidence
        && current.Lifecycle.ReconciliationEvidence == next.Lifecycle.ReconciliationEvidence
        && current.Lifecycle.Blocker == next.Lifecycle.Blocker
        && ClosingMonotonic(current.Lifecycle.ClosingEvidence, next.Lifecycle.ClosingEvidence)
        && current.Lifecycle.ClosingEvidence != next.Lifecycle.ClosingEvidence;

    private static bool ClosingMonotonic(
        GrimoireOfflineTransitionClosingEvidence current,
        GrimoireOfflineTransitionClosingEvidence next) =>
        (!current.AdmissionDenied || next.AdmissionDenied)
        && (!current.RequestWorkDrained || next.RequestWorkDrained)
        && (!current.OpenAttemptsResolved || next.OpenAttemptsResolved)
        && (!current.HandlesAndPoolsClosed || next.HandlesAndPoolsClosed)
        && (!current.ClosedGenerationProved || next.ClosedGenerationProved)
        && (current.ClosedDatasetGeneration is null
            || current.ClosedDatasetGeneration == next.ClosedDatasetGeneration);

    private static bool ApplyingAdvances(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next)
    {

        if (!SameLifecycleEvidence(current, next)
            || current.ReplacementEvidence is not null
                && !ReplacementMonotonic(current.ReplacementEvidence, next.ReplacementEvidence))
        {

            return false;

        }

        if (current.LastCompletedPhase == next.LastCompletedPhase)
        {

            if (current.InFlightPhase is null && next.InFlightPhase is { } begun)
            {

                return (byte)begun == (byte)current.LastCompletedPhase + 1
                    && next.InFlightBeforeState is not null
                    && current.ReplacementEvidence == next.ReplacementEvidence;

            }

            return current.InFlightPhase is not null
                && current.InFlightPhase == next.InFlightPhase
                && current.InFlightBeforeState == next.InFlightBeforeState
                && current.ReplacementEvidence != next.ReplacementEvidence;

        }

        return current.InFlightPhase == next.LastCompletedPhase
            && current.InFlightBeforeState is not null
            && next.InFlightPhase is null
            && next.InFlightBeforeState is null
            && current.ReplacementEvidence == next.ReplacementEvidence
            && (byte)next.LastCompletedPhase == (byte)current.LastCompletedPhase + 1;

    }

    private static bool ReplacementMonotonic(
        GrimoireOfflineTransitionReplacementEvidence current,
        GrimoireOfflineTransitionReplacementEvidence? next) => next is not null
        && current.StagingLeaf == next.StagingLeaf
        && current.SourcePhysicalIdentityDigest == next.SourcePhysicalIdentityDigest
        && current.DestinationPhysicalIdentityDigest == next.DestinationPhysicalIdentityDigest
        && current.OriginalBackupPhysicalIdentityDigest
            == next.OriginalBackupPhysicalIdentityDigest
        && (current.StagingPhysicalIdentityDigest is null
            || current.StagingPhysicalIdentityDigest == next.StagingPhysicalIdentityDigest)
        && (current.StagedContentDigest is null
            || current.StagedContentDigest == next.StagedContentDigest);

    private static bool VerificationAdvances(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next)
    {

        GrimoireOfflineTransitionVerificationEvidence from =
            current.Lifecycle.VerificationEvidence;

        GrimoireOfflineTransitionVerificationEvidence to =
            next.Lifecycle.VerificationEvidence;

        return SameEffectAndClosingEvidence(current, next)
            && current.Lifecycle.ReconciliationEvidence
                == next.Lifecycle.ReconciliationEvidence
            && current.Lifecycle.Blocker == next.Lifecycle.Blocker
            && (!from.MaintenanceLaneOpened || to.MaintenanceLaneOpened)
            && (!from.CandidateVerified || to.CandidateVerified)
            && (!from.RuntimeCovenantAuthorityVerified
                || to.RuntimeCovenantAuthorityVerified)
            && from != to;

    }

    private static bool IsOpeningReconciliation(IGrimoireOfflineTransitionPayload payload) =>
        payload.Lifecycle.ReconciliationEvidence is { } evidence
        && evidence.Step is GrimoireOfflineTransitionReconciliationStep.CandidateVerified
        && EvidenceMatchesStep(payload, evidence);

    private static bool ReconciliationAdvances(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next)
    {

        GrimoireOfflineTransitionReconciliationEvidence? from =
            current.Lifecycle.ReconciliationEvidence;

        GrimoireOfflineTransitionReconciliationEvidence? to =
            next.Lifecycle.ReconciliationEvidence;

        return from is not null
            && to is not null
            && (byte)to.Step == (byte)from.Step + 1
            && SameEffectAndClosingEvidence(current, next)
            && current.Lifecycle.VerificationEvidence == next.Lifecycle.VerificationEvidence
            && current.Lifecycle.Blocker == next.Lifecycle.Blocker
            && (from.DatabaseTerminalWinnerDigest is null
                || from.DatabaseTerminalWinnerDigest == to.DatabaseTerminalWinnerDigest)
            && (!from.ParentReceiptNotRequired || to.ParentReceiptNotRequired)
            && (from.ParentReceiptDigest is null
                || from.ParentReceiptDigest == to.ParentReceiptDigest)
            && (!from.LaneClosed || to.LaneClosed)
            && (from.CovenantDispositionIntent is null
                || from.CovenantDispositionIntent == to.CovenantDispositionIntent)
            && EvidenceMatchesStep(current, from)
            && EvidenceMatchesStep(next, to);

    }

    private static bool EvidenceMatchesStep(
        IGrimoireOfflineTransitionPayload payload,
        GrimoireOfflineTransitionReconciliationEvidence evidence)
    {

        bool parentBound = payload.Binding.ParentReceiptBindingDigest is not null;

        bool exactParent = evidence.Step
            >= GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied
            ? parentBound
                ? !evidence.ParentReceiptNotRequired
                    && evidence.ParentReceiptDigest
                        == payload.Binding.ParentReceiptBindingDigest
                : evidence.ParentReceiptNotRequired && evidence.ParentReceiptDigest is null
            : !evidence.ParentReceiptNotRequired && evidence.ParentReceiptDigest is null;

        bool exactTerminal = evidence.Step
            >= GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner
            ? evidence.DatabaseTerminalWinnerDigest is { IsValid: true }
            : evidence.DatabaseTerminalWinnerDigest is null;

        bool exactLane = evidence.LaneClosed
            == (evidence.Step >= GrimoireOfflineTransitionReconciliationStep.LaneClosed);

        bool exactDisposition = evidence.Step
            >= GrimoireOfflineTransitionReconciliationStep.CovenantDispositionInFlight
            ? evidence.CovenantDispositionIntent == payload.Lifecycle.TerminalIntent
            : evidence.CovenantDispositionIntent is null;

        return exactParent && exactTerminal && exactLane && exactDisposition;

    }

    private static bool CanEnterKeepClosed(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next) =>
        current.Lifecycle.State is not GrimoireOfflineTransitionState.Prepared
            and not GrimoireOfflineTransitionState.KeepClosed
            and not GrimoireOfflineTransitionState.RetirementPending
        && (current.Lifecycle.State is not GrimoireOfflineTransitionState.Closing
            || current.Lifecycle.ClosingEvidence.IsComplete)
        && next.Lifecycle.Blocker is { } blocker
        && blocker.ResumeState == current.Lifecycle.State
        && SameEvidenceExceptBlocker(current, next);

    private static bool CanResumeFromKeepClosed(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next,
        bool blockerResolutionProved) =>
        blockerResolutionProved
        && current.Lifecycle.Blocker is { } blocker
        && next.Lifecycle.State == blocker.ResumeState
        && next.Lifecycle.Blocker is null
        && SameEvidenceExceptBlocker(current, next);

    private static bool SameEvidence(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next) =>
        SameEffectEvidence(current, next) && SameLifecycleEvidence(current, next);

    private static bool SameEvidenceExceptBlocker(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next) =>
        SameEffectEvidence(current, next)
        && current.Lifecycle.TerminalIntent == next.Lifecycle.TerminalIntent
        && current.Lifecycle.ClosingEvidence == next.Lifecycle.ClosingEvidence
        && current.Lifecycle.VerificationEvidence == next.Lifecycle.VerificationEvidence
        && current.Lifecycle.ReconciliationEvidence == next.Lifecycle.ReconciliationEvidence;

    private static bool SameEffectAndClosingEvidence(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next) =>
        SameEffectEvidence(current, next)
        && current.Lifecycle.ClosingEvidence == next.Lifecycle.ClosingEvidence;

    private static bool SameEffectEvidence(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next) =>
        current.LastCompletedPhase == next.LastCompletedPhase
        && current.InFlightPhase == next.InFlightPhase
        && current.InFlightBeforeState == next.InFlightBeforeState
        && current.ReplacementEvidence == next.ReplacementEvidence;

    private static bool SameLifecycleEvidence(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next) =>
        current.Lifecycle.ClosingEvidence == next.Lifecycle.ClosingEvidence
        && current.Lifecycle.VerificationEvidence == next.Lifecycle.VerificationEvidence
        && current.Lifecycle.ReconciliationEvidence == next.Lifecycle.ReconciliationEvidence
        && current.Lifecycle.Blocker == next.Lifecycle.Blocker;

    private static Result Conflict() => new Error(
        ErrorCodes.Covenant.ManualRecoveryRequired,
        "The authenticated offline transition cannot be advanced by this build.");

}
