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
        CovenantResetOfflineTransitionPayloadV1 next) =>
        GrimoireOfflineTransitionLifecycleValidator.ValidateAdvance(current, next);

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

        if (current.OrdinaryFactoryContinuationCompleted
            && !next.OrdinaryFactoryContinuationCompleted)
        {

            return Conflict();

        }

        return GrimoireOfflineTransitionLifecycleValidator.ValidateAdvance(current, next);

    }

    internal GrimoireOfflineTransitionHandlerOutcome ResolveOutcome(
        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 payload) =>
        GrimoireOfflineTransitionLifecycleValidator.ResolveOutcome(payload);

    private static Result Conflict() => new Error(
        ErrorCodes.Covenant.ManualRecoveryRequired,
        "The authenticated offline transition cannot be advanced by this build.");

}

internal static class GrimoireOfflineTransitionLifecycleValidator
{

    internal static Result ValidateAdvance(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next)
    {

        if (!ValidPayload(current)
            || !ValidPayload(next)
            || current.Binding != next.Binding
            || !ValidTerminalIntentAdvance(current.Lifecycle, next.Lifecycle))
        {

            return Conflict();

        }

        GrimoireOfflineTransitionState from = current.Lifecycle.State;

        GrimoireOfflineTransitionState to = next.Lifecycle.State;

        bool valid = (from, to) switch
        {
            (GrimoireOfflineTransitionState.Prepared,
                GrimoireOfflineTransitionState.Closing) =>
                PreparedToClosing(current, next),
            (GrimoireOfflineTransitionState.Closing,
                GrimoireOfflineTransitionState.Closing) =>
                ClosingAdvances(current, next),
            (GrimoireOfflineTransitionState.Closing,
                GrimoireOfflineTransitionState.Applying) =>
                current.Lifecycle.ClosingEvidence.IsComplete
                && current.LastCompletedPhase == next.LastCompletedPhase
                && current.InFlightPhase == next.InFlightPhase
                && current.InFlightBeforeState == next.InFlightBeforeState
                && current.ReplacementEvidence == next.ReplacementEvidence,
            (GrimoireOfflineTransitionState.Closing,
                GrimoireOfflineTransitionState.ReopenPrepared) =>
                current.Lifecycle.ClosingEvidence.IsComplete
                && current.LastCompletedPhase is CovenantResetPhase.InventoryPrepared
                && current.InFlightPhase is null
                && current.InFlightBeforeState is null
                && current.ReplacementEvidence is null
                && next.Lifecycle.TerminalIntent
                    is GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen,
            (GrimoireOfflineTransitionState.Applying,
                GrimoireOfflineTransitionState.Applying) =>
                ApplyingAdvances(current, next),
            (GrimoireOfflineTransitionState.Applying,
                GrimoireOfflineTransitionState.ReopenPrepared) =>
                current.LastCompletedPhase is CovenantResetPhase.SidecarsVerified
                && current.InFlightPhase is null
                && current.InFlightBeforeState is null
                && next.Lifecycle.TerminalIntent
                    is GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
            (GrimoireOfflineTransitionState.ReopenPrepared,
                GrimoireOfflineTransitionState.Verifying) => true,
            (GrimoireOfflineTransitionState.Verifying,
                GrimoireOfflineTransitionState.Verifying) =>
                VerificationAdvances(current, next),
            (GrimoireOfflineTransitionState.Verifying,
                GrimoireOfflineTransitionState.DatabaseReconciliationPending) =>
                current.Lifecycle.VerificationEvidence.IsComplete
                && IsOpeningReconciliation(next.Lifecycle.ReconciliationEvidence),
            (GrimoireOfflineTransitionState.DatabaseReconciliationPending,
                GrimoireOfflineTransitionState.DatabaseReconciliationPending) =>
                ReconciliationAdvances(current, next),
            (GrimoireOfflineTransitionState.DatabaseReconciliationPending,
                GrimoireOfflineTransitionState.RetirementPending) =>
                current.Lifecycle.ReconciliationEvidence is { IsComplete: true }
                && next.Lifecycle.ReconciliationEvidence
                    == current.Lifecycle.ReconciliationEvidence,
            (_, GrimoireOfflineTransitionState.KeepClosed) =>
                CanEnterKeepClosed(current, next),
            (GrimoireOfflineTransitionState.KeepClosed, _) =>
                CanResumeFromKeepClosed(current, next),
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
            || payload.InFlightPhase is { } inFlight
                && !CovenantResetPhaseMachine.IsDeclared(inFlight)
            || (payload.InFlightPhase is null) != (payload.InFlightBeforeState is null)
            || payload.InFlightBeforeState is not null
                && (!payload.InFlightBeforeState.SourceStateDigest.IsValid
                    || !payload.InFlightBeforeState.EffectEvidenceDigest.IsValid)
            || !ValidReplacement(payload.ReplacementEvidence)
            || !ValidLifecycle(payload.Lifecycle)
            || !StateEvidenceCoherent(payload))
        {

            return false;

        }

        return true;

    }

    private static bool StateEvidenceCoherent(IGrimoireOfflineTransitionPayload payload)
    {

        GrimoireOfflineTransitionLifecycle lifecycle = payload.Lifecycle;

        GrimoireOfflineTransitionClosingEvidence closing = lifecycle.ClosingEvidence;

        GrimoireOfflineTransitionVerificationEvidence verifying =
            lifecycle.VerificationEvidence;

        if ((closing.RequestWorkDrained && !closing.AdmissionDenied)
            || (closing.OpenAttemptsResolved && !closing.RequestWorkDrained)
            || (closing.HandlesAndPoolsClosed && !closing.OpenAttemptsResolved)
            || (closing.ClosedGenerationProved && !closing.HandlesAndPoolsClosed)
            || (verifying.CandidateVerified && !verifying.MaintenanceLaneOpened)
            || (verifying.RuntimeCovenantAuthorityVerified && !verifying.CandidateVerified))
        {

            return false;

        }

        if (lifecycle.State is GrimoireOfflineTransitionState.KeepClosed)
        {

            GrimoireOfflineTransitionState blocked = lifecycle.Blocker!.ResumeState;

            return blocked is GrimoireOfflineTransitionState.Closing
                    or GrimoireOfflineTransitionState.Applying
                ? lifecycle.TerminalIntent
                    is GrimoireOfflineTransitionTerminalIntent.Undecided
                : lifecycle.TerminalIntent
                    is not GrimoireOfflineTransitionTerminalIntent.Undecided;

        }

        bool noVerification = verifying == new GrimoireOfflineTransitionVerificationEvidence(
            false,
            false,
            false);

        return lifecycle.State switch
        {
            GrimoireOfflineTransitionState.Prepared =>
                lifecycle.TerminalIntent
                    is GrimoireOfflineTransitionTerminalIntent.Undecided
                && closing == new GrimoireOfflineTransitionClosingEvidence(
                    false,
                    false,
                    false,
                    false,
                    false,
                    null)
                && noVerification
                && lifecycle.ReconciliationEvidence is null
                && payload.LastCompletedPhase is CovenantResetPhase.InventoryPrepared
                && payload.InFlightPhase is null
                && payload.InFlightBeforeState is null
                && payload.ReplacementEvidence is null,
            GrimoireOfflineTransitionState.Closing
                or GrimoireOfflineTransitionState.Applying =>
                lifecycle.TerminalIntent
                    is GrimoireOfflineTransitionTerminalIntent.Undecided
                && noVerification
                && lifecycle.ReconciliationEvidence is null,
            GrimoireOfflineTransitionState.ReopenPrepared =>
                lifecycle.TerminalIntent
                    is not GrimoireOfflineTransitionTerminalIntent.Undecided
                && closing.IsComplete
                && noVerification
                && lifecycle.ReconciliationEvidence is null
                && payload.InFlightPhase is null
                && payload.InFlightBeforeState is null,
            GrimoireOfflineTransitionState.Verifying =>
                lifecycle.TerminalIntent
                    is not GrimoireOfflineTransitionTerminalIntent.Undecided
                && closing.IsComplete
                && lifecycle.ReconciliationEvidence is null,
            GrimoireOfflineTransitionState.DatabaseReconciliationPending =>
                lifecycle.TerminalIntent
                    is not GrimoireOfflineTransitionTerminalIntent.Undecided
                && closing.IsComplete
                && verifying.IsComplete
                && lifecycle.ReconciliationEvidence is { } reconciliation
                && EvidenceMatchesStep(reconciliation),
            GrimoireOfflineTransitionState.RetirementPending =>
                lifecycle.TerminalIntent
                    is not GrimoireOfflineTransitionTerminalIntent.Undecided
                && closing.IsComplete
                && verifying.IsComplete
                && lifecycle.ReconciliationEvidence is { IsComplete: true } reconciliation
                && EvidenceMatchesStep(reconciliation),
            _ => false,
        };

    }

    private static bool ValidLifecycle(GrimoireOfflineTransitionLifecycle lifecycle)
    {

        if (lifecycle.ClosingEvidence.ClosedGenerationProved
            != (lifecycle.ClosingEvidence.ClosedDatasetGeneration is not null))
        {

            return false;

        }

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
            : lifecycle.Blocker is null || lifecycle.Blocker.ResolutionProved;

    }

    private static bool ValidReplacement(GrimoireOfflineTransitionReplacementEvidence? replacement)
    {

        if (replacement is null)
        {

            return true;

        }

        return !string.IsNullOrWhiteSpace(replacement.StagingLeaf)
            && replacement.StagingLeaf.Length <= 255
            && Path.GetFileName(replacement.StagingLeaf) == replacement.StagingLeaf
            && replacement.SourcePhysicalIdentityDigest.IsValid
            && replacement.StagingPhysicalIdentityDigest is not { IsValid: false }
            && replacement.DestinationPhysicalIdentityDigest.IsValid
            && replacement.OriginalBackupPhysicalIdentityDigest.IsValid
            && replacement.StagedContentDigest is not { IsValid: false };

    }

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

    private static bool PreparedToClosing(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next) =>
        current.InFlightPhase is null
        && next.InFlightPhase is null
        && current.Lifecycle.Blocker is null
        && next.Lifecycle.Blocker is null;

    private static bool ClosingAdvances(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next) =>
        ClosingMonotonic(
            current.Lifecycle.ClosingEvidence,
            next.Lifecycle.ClosingEvidence)
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

        if (current.ReplacementEvidence is not null
            && !ReplacementMonotonic(current.ReplacementEvidence, next.ReplacementEvidence))
        {

            return false;

        }

        if (current.LastCompletedPhase == next.LastCompletedPhase)
        {

            if (current.InFlightPhase is null && next.InFlightPhase is { } begun)
            {

                return (byte)begun == (byte)current.LastCompletedPhase + 1
                    && next.InFlightBeforeState is not null;

            }

            return current.InFlightPhase is not null
                && current.InFlightPhase == next.InFlightPhase
                && current.InFlightBeforeState == next.InFlightBeforeState
                && current.ReplacementEvidence != next.ReplacementEvidence;

        }

        bool completedInFlight = current.InFlightPhase == next.LastCompletedPhase
            && current.InFlightBeforeState is not null
            && next.InFlightPhase is null
            && next.InFlightBeforeState is null;

        bool directCompletedAdvance = current.InFlightPhase is null
            && next.InFlightPhase is null
            && (byte)next.LastCompletedPhase == (byte)current.LastCompletedPhase + 1;

        return (completedInFlight || directCompletedAdvance)
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

        return (!from.MaintenanceLaneOpened || to.MaintenanceLaneOpened)
            && (!from.CandidateVerified || to.CandidateVerified)
            && (!from.RuntimeCovenantAuthorityVerified
                || to.RuntimeCovenantAuthorityVerified)
            && from != to;

    }

    private static bool IsOpeningReconciliation(
        GrimoireOfflineTransitionReconciliationEvidence? evidence) => evidence is
        {
            Step: GrimoireOfflineTransitionReconciliationStep.CandidateVerified,
            DatabaseTerminalWinnerDigest: null,
            ParentReceiptDigest: null,
            LaneClosed: false,
            CovenantDispositionIntent: null,
        };

    private static bool ReconciliationAdvances(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next)
    {

        GrimoireOfflineTransitionReconciliationEvidence? from =
            current.Lifecycle.ReconciliationEvidence;

        GrimoireOfflineTransitionReconciliationEvidence? to =
            next.Lifecycle.ReconciliationEvidence;

        if (from is null || to is null || (byte)to.Step != (byte)from.Step + 1)
        {

            return false;

        }

        return (from.DatabaseTerminalWinnerDigest is null
                || from.DatabaseTerminalWinnerDigest == to.DatabaseTerminalWinnerDigest)
            && (!from.ParentReceiptNotRequired || to.ParentReceiptNotRequired)
            && (from.ParentReceiptDigest is null
                || from.ParentReceiptDigest == to.ParentReceiptDigest)
            && (!from.LaneClosed || to.LaneClosed)
            && (from.CovenantDispositionIntent is null
                || from.CovenantDispositionIntent == to.CovenantDispositionIntent)
            && EvidenceMatchesStep(to);

    }

    private static bool EvidenceMatchesStep(
        GrimoireOfflineTransitionReconciliationEvidence evidence)
    {

        if (evidence.Step >= GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner
            && evidence.DatabaseTerminalWinnerDigest is not { IsValid: true })
        {

            return false;

        }

        if (evidence.Step >= GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied
            && !evidence.ParentReceiptNotRequired
            && evidence.ParentReceiptDigest is not { IsValid: true })
        {

            return false;

        }

        if (evidence.Step >= GrimoireOfflineTransitionReconciliationStep.LaneClosed
            && !evidence.LaneClosed)
        {

            return false;

        }

        return evidence.Step <
                GrimoireOfflineTransitionReconciliationStep.CovenantDispositionInFlight
            || evidence.CovenantDispositionIntent is not null
                and not GrimoireOfflineTransitionTerminalIntent.Undecided;

    }

    private static bool CanEnterKeepClosed(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next)
    {

        if (current.Lifecycle.State is GrimoireOfflineTransitionState.Prepared
            or GrimoireOfflineTransitionState.KeepClosed
            or GrimoireOfflineTransitionState.RetirementPending
            || next.Lifecycle.Blocker is not { ResolutionProved: false } blocker
            || blocker.ResumeState != current.Lifecycle.State
            || current.Lifecycle.TerminalIntent != next.Lifecycle.TerminalIntent
            || current.InFlightPhase != next.InFlightPhase
            || current.InFlightBeforeState != next.InFlightBeforeState
            || current.ReplacementEvidence != next.ReplacementEvidence)
        {

            return false;

        }

        return current.Lifecycle.State is not GrimoireOfflineTransitionState.Closing
            || current.Lifecycle.ClosingEvidence.IsComplete;

    }

    private static bool CanResumeFromKeepClosed(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next)
    {

        GrimoireOfflineTransitionBlocker? blocker = current.Lifecycle.Blocker;

        return blocker is not null
            && next.Lifecycle.State == blocker.ResumeState
            && next.Lifecycle.Blocker == blocker with { ResolutionProved = true }
            && current.Lifecycle.TerminalIntent == next.Lifecycle.TerminalIntent
            && current.InFlightPhase == next.InFlightPhase
            && current.InFlightBeforeState == next.InFlightBeforeState
            && current.ReplacementEvidence == next.ReplacementEvidence;

    }

    private static Result Conflict() => new Error(
        ErrorCodes.Covenant.ManualRecoveryRequired,
        "The authenticated offline transition cannot be advanced by this build.");

}
