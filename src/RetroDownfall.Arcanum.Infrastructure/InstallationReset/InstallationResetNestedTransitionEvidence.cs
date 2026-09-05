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

        // A reset that claimed nothing and a journal that names no parent are two authorities over
        // separate work that happen to be open at once — a broader reset in its own phases beside an
        // erasure the running host started for itself. Neither is a downgrade of the other, because a
        // downgrade is only possible where one of them says otherwise, and the host refuses to
        // bootstrap over the journal either way.
        if (outer.NestedTransitionReceipt is null)
        {

            return inner.ParentReceiptBindingDigest is null
                ? InstallationResetNestedTransitionEvidenceOutcome.StandaloneTransition
                : InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired;

        }

        // The receipt names the identity the nested apply was REQUESTED under, which the operation
        // ledger deliberately keeps distinct from the identity it then created — a replay key and a
        // durable operation are different things, and elsewhere the record refuses a completion whose
        // two ids are equal. So the two records are not tied together by comparing those ids, which
        // could never match, but by the binding digest below, which both sides derive from the same
        // claim and different halves of the evidence.
        InstallationResetNestedTransitionReceiptV1 receipt = outer.NestedTransitionReceipt;

        if (inner.ParentReceiptBindingDigest is not { } committed
            || inner.Kind is not GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure)
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
            && InRetirementSuffix(receipt, inner)
            ? InstallationResetNestedTransitionEvidenceOutcome.NestedReceiptStoredRetirementSuffix
            : InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired;

    }

    /// <summary>
    /// Whether the journal has reached the terminalization the stored receipt reports.
    /// </summary>
    /// <remarks>
    /// The test is the terminal winner, not the phase. A completion receipt is published after the
    /// exact terminal compare-exchange is journaled and before the journal records the parent step, so
    /// the state between those two writes is one the protocol guarantees will occur — demanding the
    /// later phase would classify the crossing window itself as two records describing different runs.
    /// A journal that names the same winner has performed the same terminalization, whatever it has
    /// managed to write down since; one that names none, or a different one, has not.
    ///
    /// <para>The three admitted states are the three a journal can be in once its winner is recorded:
    /// still working through the reconciliation suffix, waiting to retire, or parked. A parked journal
    /// belongs here because parking is the resumable state — what remains is still only the suffix.</para>
    /// </remarks>
    private static bool InRetirementSuffix(
        InstallationResetNestedTransitionReceiptV1 receipt,
        InnerJournal inner) =>
        receipt.TerminalWinnerDigest is { IsValid: true } winner
        && inner.TerminalWinnerDigest == winner
        && inner.State is GrimoireOfflineTransitionState.DatabaseReconciliationPending
            or GrimoireOfflineTransitionState.RetirementPending
            or GrimoireOfflineTransitionState.KeepClosed;

}
