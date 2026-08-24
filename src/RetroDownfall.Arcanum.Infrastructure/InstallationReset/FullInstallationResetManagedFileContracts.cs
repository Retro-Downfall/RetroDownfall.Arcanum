using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// How far an attested full installation reset has got through the managed workspace files the
/// Grimoire still owns.
/// </summary>
/// <remarks>
/// Four ordered phases, published one at a time into the authenticated active record, because every
/// step past the first is a filesystem effect SQLite cannot roll back. <c>InventoryPrepared</c> fixes
/// the exact set of sources the rest of the operation is allowed to touch;
/// <c>WriteIntentsReconciled</c> means no write intent is still mid-flight;
/// <c>WorkItemsReconciled</c> means every adopted file has been erased or refused, and fixes the
/// work-item inventory that routing produced; and <c>TerminalInventoryVerified</c> means both
/// inventories are accounted for and the counts add up. Nothing may delete the Grimoire before the
/// last one.
/// </remarks>
internal enum FullInstallationResetManagedFileReconciliationPhase : byte
{

    InventoryPrepared = 1,

    WriteIntentsReconciled = 2,

    WorkItemsReconciled = 3,

    TerminalInventoryVerified = 4,

}

/// <summary>
/// Which half of the managed-file inventory a content-free blocker belongs to.
/// </summary>
/// <remarks>
/// The two arms are separated in the preimage so a refused write intent and a refused work item with
/// the same identity cannot produce the same commitment. They are different facts about different
/// rows, and a reconciliation that could not tell them apart would let one stand in for the other.
/// </remarks>
internal enum FullInstallationResetManagedFileBlockerArm : byte
{

    ManualWriteOrphan = 1,

    ManualWorkItemOrphan = 2,

}

/// <summary>
/// One source write intent's terminal outcome, as the reconciliation is allowed to commit to it.
/// </summary>
/// <remarks>
/// The three legal phases are <c>Cleaned</c>, <c>ManualNonrevocable</c>, and <c>Erased</c>. Blocker
/// evidence is present exactly for the manual arm: a safely terminal source that carried it would be
/// asserting an unresolved problem it does not have, and a manual one without it would be asserting a
/// resolution nobody proved.
/// </remarks>
internal sealed record FullInstallationResetManagedSourceClassificationV1(
    Guid SourceWriteOperationId,
    ManagedFileWriteIntentPhase TerminalPhase,
    CovenantDigest? BlockerEvidenceDigest);

/// <summary>
/// One local-erasure work item's terminal outcome, as the reconciliation is allowed to commit to it.
/// </summary>
/// <remarks>
/// A <c>Completed</c> item must name which of the two absence proofs it holds, because that is the
/// difference between "recovery observed the unlink had already committed" and "this run
/// compare-deleted the exact opened handle". A <c>ManualBlocker</c> holds neither and carries blocker
/// evidence instead.
/// </remarks>
internal sealed record FullInstallationResetManagedWorkItemClassificationV1(
    Guid WorkItemId,
    LocalErasureWorkItemState TerminalState,
    LocalErasureDeletionEvidenceCode? DeletionEvidence,
    CovenantDigest? BlockerEvidenceDigest);

/// <summary>
/// The authenticated managed-file reconciliation checkpoint, nested inside the marker-pair checkpoint.
/// </summary>
/// <remarks>
/// Version exactly one. Each head is published by the phase that can actually prove it, and never
/// changes afterwards.
///
/// <para>The source head — count, ordered write-operation identities, and vector digest — is fixed by
/// the first publication at <c>InventoryPrepared</c>, because every source the operation will ever
/// touch already exists in the journal before it starts.</para>
///
/// <para>The work-item head is null until <c>WorkItemsReconciled</c>. It cannot honestly be fixed
/// earlier: routing an adopted source through the shared erasure kernel is what creates its work item,
/// so a vector published at <c>InventoryPrepared</c> would either omit the items the operation is
/// about to create or predict identities nothing has committed to.</para>
///
/// <para>The four terminal counters and the classification digest are null until
/// <c>TerminalInventoryVerified</c> and all nonnull afterward. A partially filled tail is never
/// authenticated, because a reset that could publish three of the four counters could report an
/// inventory that does not add up.</para>
/// </remarks>
internal sealed record FullInstallationResetManagedFileCheckpointV1(
    byte Version,
    FullInstallationResetManagedFileReconciliationPhase Phase,
    ulong SourceCount,
    ImmutableArray<Guid> OrderedSourceWriteOperationIds,
    CovenantDigest SourceWriteIntentVectorDigest,
    ulong? LocalErasureWorkItemCount,
    ImmutableArray<Guid>? OrderedLocalErasureWorkItemIds,
    CovenantDigest? LocalErasureWorkItemVectorDigest,
    ulong? SafeTerminalWriteIntentCount,
    ulong? ManualWriteOrphanCount,
    ulong? CompletedWorkItemCount,
    ulong? ManualWorkItemOrphanCount,
    CovenantDigest? TerminalClassificationDigest);

/// <summary>
/// The closed bounds every managed-file reconciliation vector is validated against before allocation.
/// </summary>
internal static class FullInstallationResetManagedFileBounds
{

    /// <summary>
    /// The same 4,096-entry ceiling the Campaign marker vectors use.
    /// </summary>
    /// <remarks>
    /// Shared deliberately: both vectors ride in the same authenticated envelope, and a managed-file
    /// vector with a larger ceiling would let one half of the payload push the other past the bound
    /// the envelope was sized for.
    /// </remarks>
    internal const int MaximumVectorCount =
        HostToolsMarkerPairResetCheckpointBounds.MaximumVectorCount;

    /// <summary>
    /// Reports whether both identity vectors are initialized and within the closed ceiling.
    /// </summary>
    internal static bool HasValidVectorShape(
        FullInstallationResetManagedFileCheckpointV1 checkpoint)
    {

        ArgumentNullException.ThrowIfNull(checkpoint);

        return !checkpoint.OrderedSourceWriteOperationIds.IsDefault
            && checkpoint.OrderedSourceWriteOperationIds.Length <= MaximumVectorCount
            && (checkpoint.OrderedLocalErasureWorkItemIds is not { } workItems
                || !workItems.IsDefault && workItems.Length <= MaximumVectorCount);

    }

    /// <summary>
    /// Reports whether the work-item head and the terminal tail are each whole or wholly absent.
    /// </summary>
    /// <remarks>
    /// Shape only. Whether the counters actually add up against the inventory is a separate question,
    /// answered where the inventory is in hand rather than here.
    /// </remarks>
    internal static bool HasCoherentTerminalTail(
        FullInstallationResetManagedFileCheckpointV1 checkpoint)
    {

        ArgumentNullException.ThrowIfNull(checkpoint);

        int workItemHead =
            (checkpoint.LocalErasureWorkItemCount is null ? 0 : 1)
            + (checkpoint.OrderedLocalErasureWorkItemIds is null ? 0 : 1)
            + (checkpoint.LocalErasureWorkItemVectorDigest is null ? 0 : 1);

        if (workItemHead is not (0 or 3))
        {

            return false;

        }

        int present =
            (checkpoint.SafeTerminalWriteIntentCount is null ? 0 : 1)
            + (checkpoint.ManualWriteOrphanCount is null ? 0 : 1)
            + (checkpoint.CompletedWorkItemCount is null ? 0 : 1)
            + (checkpoint.ManualWorkItemOrphanCount is null ? 0 : 1)
            + (checkpoint.TerminalClassificationDigest is null ? 0 : 1);

        // The terminal tail cannot exist without the work-item head it counts against.
        return present is 0 or 5
            && (present == 0 || workItemHead == 3);

    }

    /// <summary>
    /// Throws before any deep copy of a checkpoint whose vectors are uninitialized or oversized.
    /// </summary>
    internal static void RequireValidVectorShapeBeforeCopy(
        FullInstallationResetManagedFileCheckpointV1 checkpoint)
    {

        ArgumentNullException.ThrowIfNull(checkpoint);

        if (!HasValidVectorShape(checkpoint))
        {

            throw new ArgumentException(
                "Managed-file reconciliation vectors must be initialized and contain at most 4,096 entries.",
                nameof(checkpoint));

        }

    }

}
