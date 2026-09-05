using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>What one installation's two durable maintenance records say when read together.</summary>
/// <remarks>
/// Six admitting answers and one refusal. The refusal is deliberately undifferentiated: which of the
/// disagreeing shapes an installation is in is exactly the kind of detail that does not belong in
/// operator-visible text, and every one of them has the same remedy.
/// </remarks>
internal enum InstallationResetNestedTransitionEvidenceOutcome : byte
{

    /// <summary>Neither record is active. Ordinary launch-gap inspection and bootstrap apply.</summary>
    NeitherActive = 1,

    /// <summary>A reset is active and has not launched its nested transition yet.</summary>
    NestedNotStarted = 2,

    /// <summary>A reset is active, its nested transition finished, and its journal is already gone.</summary>
    NestedRetired = 3,

    /// <summary>A journal is active and belongs to no broader workflow.</summary>
    StandaloneTransition = 4,

    /// <summary>Both are active and the journal is this reset's exact nested arm.</summary>
    NestedBound = 5,

    /// <summary>Both are active, the receipt is stored, and only exact retirement remains.</summary>
    NestedReceiptStoredRetirementSuffix = 6,

    /// <summary>The two records disagree, or one of them cannot be read forward. Stay closed.</summary>
    RecoveryRequired = 7,

}

/// <summary>
/// The one place the installation-reset record and the offline-transition journal are read as a pair.
/// </summary>
/// <remarks>
/// A pure function of already-authenticated evidence: no I/O, no repair, and no preference for either
/// record when they disagree. Both halves are durable authorities over different things — the reset
/// record over the broader workflow, the journal over the nested database transformation — and the
/// whole point of keeping them separate is that neither may be reconstructed from the other. So every
/// disagreement is a refusal rather than a reconciliation.
/// </remarks>
internal static class InstallationResetNestedTransitionEvidence
{

    /// <summary>The installation-reset record, reduced to what the pairing actually reads.</summary>
    internal sealed record OuterRecord(
        Guid OperationId,
        InstallationResetNestedTransitionReceiptV1? NestedTransitionReceipt);

    /// <summary>The offline-transition journal, reduced to what the pairing actually reads.</summary>
    internal sealed record InnerJournal(
        Guid OperationId,
        GrimoireOfflineTransitionKind Kind,
        CovenantDigest EffectDigest,
        CovenantDigest? ParentReceiptBindingDigest,
        GrimoireOfflineTransitionState State,
        GrimoireOfflineTransitionReconciliationStep? ReconciliationStep,
        CovenantDigest? TerminalWinnerDigest);

    internal static InstallationResetNestedTransitionEvidenceOutcome Resolve(
        OuterRecord? outer,
        InnerJournal? inner)
    {

        if (outer is null)
        {

            return inner is null
                ? InstallationResetNestedTransitionEvidenceOutcome.NeitherActive
                : inner.ParentReceiptBindingDigest is null
                    // No broader record to be the nested arm of, and none claimed. Ordinary work.
                    ? InstallationResetNestedTransitionEvidenceOutcome.StandaloneTransition
                    // A journal that names a parent may not be resumed as though it had none: that is
                    // the downgrade the split exists to forbid.
                    : InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired;

        }

        if (inner is null)
        {

            return outer.NestedTransitionReceipt switch
            {
                null => InstallationResetNestedTransitionEvidenceOutcome.NestedNotStarted,
                { Phase: InstallationResetNestedTransitionPhase.Completed } =>
                    InstallationResetNestedTransitionEvidenceOutcome.NestedRetired,

                // Claimed, with the journal that would have said what happened absent. The transition
                // began; the missing journal cannot be read as though it never did.
                _ => InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired,
            };

        }

        if (outer.NestedTransitionReceipt is not { } receipt
            || inner.ParentReceiptBindingDigest is not { } committed
            || inner.Kind is not GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure
            || receipt.NestedOperationId != inner.OperationId)
        {

            return InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired;

        }

        // Recomputed across both records rather than taken from either: the two operation identities
        // come from the outer record and the effect from the journal, so a value that matches proves
        // the halves were produced against one another.
        Result<CovenantDigest> recomputed = GrimoireOfflineTransitionParentReceipt.BindingDigest(
            outer.OperationId,
            receipt.NestedOperationId,
            inner.EffectDigest);

        if (recomputed.IsFailure || recomputed.Value != committed)
        {

            return InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired;

        }

        if (receipt.Phase is not InstallationResetNestedTransitionPhase.Completed)
        {

            return InstallationResetNestedTransitionEvidenceOutcome.NestedBound;

        }

        return receipt.NestedEffectDigest == inner.EffectDigest
            && SameWinner(receipt.TerminalWinnerDigest, inner.TerminalWinnerDigest)
            && InRetirementSuffix(inner)
            ? InstallationResetNestedTransitionEvidenceOutcome.NestedReceiptStoredRetirementSuffix
            : InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired;

    }

    /// <summary>
    /// Whether the journal is at or past the step the stored receipt says it reached.
    /// </summary>
    /// <remarks>
    /// A stored receipt is published only after the exact terminal compare-exchange, so a journal
    /// sitting anywhere earlier contradicts it. Catching such a journal up would mean publishing a
    /// reconciliation suffix the transition never performed — recording a lane it did not close and a
    /// disposition it did not spend — which is worse than staying closed.
    /// </remarks>
    private static bool InRetirementSuffix(InnerJournal inner) =>
        inner.State is GrimoireOfflineTransitionState.RetirementPending
        || (inner.State is GrimoireOfflineTransitionState.DatabaseReconciliationPending
            && inner.ReconciliationStep
                >= GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied);

    /// <summary>
    /// The one fact both records hold independently, compared only when both actually hold it.
    /// </summary>
    /// <remarks>
    /// The journal records its terminal winner several publications before the outer receipt is
    /// stored, so a journal that has not reached that point simply has nothing to compare. What is
    /// forbidden is two present values that differ: that is two records describing two different runs.
    /// </remarks>
    private static bool SameWinner(CovenantDigest? outerWinner, CovenantDigest? innerWinner) =>
        outerWinner is null || innerWinner is null || outerWinner == innerWinner;

}
