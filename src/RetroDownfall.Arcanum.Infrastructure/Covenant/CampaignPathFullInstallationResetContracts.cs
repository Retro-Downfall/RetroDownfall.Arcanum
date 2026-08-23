using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

internal sealed class CampaignPathFullInstallationResetInventory
{

    private CampaignPathFullInstallationResetInventory(
        Guid ownerOperationId,
        ImmutableArray<CampaignMarkerInventoryEntryV1> entries,
        CovenantDigest inventoryDigest)
    {

        OwnerOperationId = ownerOperationId;

        Entries = entries;

        InventoryDigest = inventoryDigest;

    }

    internal Guid OwnerOperationId { get; }

    internal ImmutableArray<CampaignMarkerInventoryEntryV1> Entries { get; }

    internal CovenantDigest InventoryDigest { get; }

    internal static Result<CampaignPathFullInstallationResetInventory> Create(
        Guid ownerOperationId,
        ImmutableArray<CampaignMarkerInventoryEntryV1> entries,
        CovenantDigest inventoryDigest)
    {

        if (ownerOperationId == Guid.Empty
            || entries.IsDefault
            || entries.Length > HostToolsMarkerPairResetCheckpointBounds.MaximumVectorCount
            || !inventoryDigest.IsValid)
        {

            return Invalid<CampaignPathFullInstallationResetInventory>();

        }

        ImmutableArray<CampaignMarkerInventoryEntryV1> copied = CopyEntries(entries);

        Result<CovenantDigest> recalculated =
            FullInstallationResetMarkerPairResetDigests.CampaignInventory(copied);

        if (recalculated.IsFailure || recalculated.Value != inventoryDigest)
        {

            return Invalid<CampaignPathFullInstallationResetInventory>();

        }

        return new CampaignPathFullInstallationResetInventory(
            ownerOperationId,
            copied,
            CopyDigest(inventoryDigest));

    }

    private static ImmutableArray<CampaignMarkerInventoryEntryV1> CopyEntries(
        ImmutableArray<CampaignMarkerInventoryEntryV1> entries)
    {

        ImmutableArray<CampaignMarkerInventoryEntryV1>.Builder copied =
            ImmutableArray.CreateBuilder<CampaignMarkerInventoryEntryV1>(entries.Length);

        foreach (CampaignMarkerInventoryEntryV1 entry in entries)
        {

            if (entry is null)
            {

                return default;

            }

            copied.Add(new CampaignMarkerInventoryEntryV1(
                entry.CampaignId,
                entry.PriorPathRevision,
                CopyDigest(entry.MarkerDigest),
                CopyDigest(entry.IndexedPhysicalIdentityDigest),
                CopyDigest(entry.CanonicalDisplayPathDigest),
                CopyDigest(entry.SameHandleOwnershipEvidenceDigest)));

        }

        return copied.MoveToImmutable();

    }

    private static CovenantDigest CopyDigest(CovenantDigest digest) =>
        digest.IsValid ? new CovenantDigest(digest.Bytes) : default;

    private static Result<T> Invalid<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Covenant.IntegrityFailure,
            "The full-installation reset Campaign cleanup evidence is invalid."));

}

internal sealed class CampaignPathFullInstallationResetCleanupPreparation
{

    private CampaignPathFullInstallationResetCleanupPreparation(
        Guid ownerOperationId,
        CovenantDigest ownerEffectDigest,
        CampaignPathFullInstallationResetInventory inventory)
    {

        OwnerOperationId = ownerOperationId;

        OwnerEffectDigest = ownerEffectDigest;

        Inventory = inventory;

    }

    internal Guid OwnerOperationId { get; }

    internal CovenantDigest OwnerEffectDigest { get; }

    internal CampaignPathFullInstallationResetInventory Inventory { get; }

    internal static Result<CampaignPathFullInstallationResetCleanupPreparation> Create(
        Guid ownerOperationId,
        CovenantDigest ownerEffectDigest,
        CampaignPathFullInstallationResetInventory inventory)
    {

        if (ownerOperationId == Guid.Empty
            || !ownerEffectDigest.IsValid
            || inventory is null
            || inventory.OwnerOperationId != ownerOperationId)
        {

            return Invalid<CampaignPathFullInstallationResetCleanupPreparation>();

        }

        Result<CampaignPathFullInstallationResetInventory> copiedInventory =
            CampaignPathFullInstallationResetInventory.Create(
                inventory.OwnerOperationId,
                inventory.Entries,
                inventory.InventoryDigest);

        if (copiedInventory.IsFailure)
        {

            return Result<CampaignPathFullInstallationResetCleanupPreparation>.Failure(
                copiedInventory.Error);

        }

        return new CampaignPathFullInstallationResetCleanupPreparation(
            ownerOperationId,
            new CovenantDigest(ownerEffectDigest.Bytes),
            copiedInventory.Value);

    }

    private static Result<T> Invalid<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Covenant.IntegrityFailure,
            "The full-installation reset Campaign cleanup evidence is invalid."));

}

internal sealed class CampaignPathFullInstallationResetCleanupReceipt
{

    private CampaignPathFullInstallationResetCleanupReceipt(
        Guid ownerOperationId,
        CovenantDigest ownerEffectDigest,
        ImmutableArray<Guid> orderedMarkerIntentIds,
        CovenantDigest markerIntentVectorDigest,
        ulong deletedCount,
        ulong orphanCount)
    {

        OwnerOperationId = ownerOperationId;

        OwnerEffectDigest = ownerEffectDigest;

        OrderedMarkerIntentIds = orderedMarkerIntentIds;

        MarkerIntentVectorDigest = markerIntentVectorDigest;

        MarkerIntentCount = checked((ulong)orderedMarkerIntentIds.Length);

        DeletedCount = deletedCount;

        OrphanCount = orphanCount;

    }

    internal Guid OwnerOperationId { get; }

    internal CovenantDigest OwnerEffectDigest { get; }

    internal ulong MarkerIntentCount { get; }

    internal ImmutableArray<Guid> OrderedMarkerIntentIds { get; }

    internal CovenantDigest MarkerIntentVectorDigest { get; }

    internal ulong DeletedCount { get; }

    internal ulong OrphanCount { get; }

    internal static Result<CampaignPathFullInstallationResetCleanupReceipt> CreatePrepared(
        Guid ownerOperationId,
        CovenantDigest ownerEffectDigest,
        ImmutableArray<Guid> orderedMarkerIntentIds,
        CovenantDigest markerIntentVectorDigest) =>
        Create(
            ownerOperationId,
            ownerEffectDigest,
            orderedMarkerIntentIds,
            markerIntentVectorDigest,
            deletedCount: 0,
            orphanCount: 0,
            terminal: false);

    internal static Result<CampaignPathFullInstallationResetCleanupReceipt> CreateTerminal(
        Guid ownerOperationId,
        CovenantDigest ownerEffectDigest,
        ImmutableArray<Guid> orderedMarkerIntentIds,
        CovenantDigest markerIntentVectorDigest,
        ulong deletedCount,
        ulong orphanCount) =>
        Create(
            ownerOperationId,
            ownerEffectDigest,
            orderedMarkerIntentIds,
            markerIntentVectorDigest,
            deletedCount,
            orphanCount,
            terminal: true);

    private static Result<CampaignPathFullInstallationResetCleanupReceipt> Create(
        Guid ownerOperationId,
        CovenantDigest ownerEffectDigest,
        ImmutableArray<Guid> orderedMarkerIntentIds,
        CovenantDigest markerIntentVectorDigest,
        ulong deletedCount,
        ulong orphanCount,
        bool terminal)
    {

        if (ownerOperationId == Guid.Empty
            || !ownerEffectDigest.IsValid
            || orderedMarkerIntentIds.IsDefault
            || orderedMarkerIntentIds.Length
                > HostToolsMarkerPairResetCheckpointBounds.MaximumVectorCount
            || !markerIntentVectorDigest.IsValid)
        {

            return Invalid<CampaignPathFullInstallationResetCleanupReceipt>();

        }

        ImmutableArray<Guid>.Builder copiedBuilder =
            ImmutableArray.CreateBuilder<Guid>(orderedMarkerIntentIds.Length);

        foreach (Guid intentId in orderedMarkerIntentIds)
        {

            copiedBuilder.Add(intentId);

        }

        ImmutableArray<Guid> copied = copiedBuilder.MoveToImmutable();

        Result<CovenantDigest> recalculated =
            FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(copied);

        bool countsValid;

        try
        {

            countsValid = !terminal
                ? deletedCount == 0 && orphanCount == 0
                : checked(deletedCount + orphanCount)
                    == checked((ulong)copied.Length);

        }
        catch (OverflowException)
        {

            countsValid = false;

        }

        if (recalculated.IsFailure
            || recalculated.Value != markerIntentVectorDigest
            || !countsValid)
        {

            return Invalid<CampaignPathFullInstallationResetCleanupReceipt>();

        }

        return new CampaignPathFullInstallationResetCleanupReceipt(
            ownerOperationId,
            new CovenantDigest(ownerEffectDigest.Bytes),
            copied,
            new CovenantDigest(markerIntentVectorDigest.Bytes),
            deletedCount,
            orphanCount);

    }

    private static Result<T> Invalid<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Covenant.IntegrityFailure,
            "The full-installation reset Campaign cleanup evidence is invalid."));

}

internal static class CampaignPathFullInstallationResetContractComparer
{

    internal static bool InventoryEquals(
        CampaignPathFullInstallationResetInventory? left,
        CampaignPathFullInstallationResetInventory? right)
    {

        if (left is null || right is null)
        {

            return left is null && right is null;

        }

        if (!InventoryIsValid(left)
            || !InventoryIsValid(right)
            || left.OwnerOperationId != right.OwnerOperationId
            || !DigestEquals(left.InventoryDigest, right.InventoryDigest)
            || left.Entries.Length != right.Entries.Length)
        {

            return false;

        }

        for (int index = 0; index < left.Entries.Length; index++)
        {

            CampaignMarkerInventoryEntryV1 first = left.Entries[index];

            CampaignMarkerInventoryEntryV1 second = right.Entries[index];

            if (first.CampaignId != second.CampaignId
                || first.PriorPathRevision != second.PriorPathRevision
                || !DigestEquals(first.MarkerDigest, second.MarkerDigest)
                || !DigestEquals(
                    first.IndexedPhysicalIdentityDigest,
                    second.IndexedPhysicalIdentityDigest)
                || !DigestEquals(
                    first.CanonicalDisplayPathDigest,
                    second.CanonicalDisplayPathDigest)
                || !DigestEquals(
                    first.SameHandleOwnershipEvidenceDigest,
                    second.SameHandleOwnershipEvidenceDigest))
            {

                return false;

            }

        }

        return true;

    }

    internal static bool PreparationEquals(
        CampaignPathFullInstallationResetCleanupPreparation? left,
        CampaignPathFullInstallationResetCleanupPreparation? right)
    {

        if (left is null || right is null)
        {

            return left is null && right is null;

        }

        return PreparationIsValid(left)
            && PreparationIsValid(right)
            && left.OwnerOperationId == right.OwnerOperationId
            && DigestEquals(left.OwnerEffectDigest, right.OwnerEffectDigest)
            && InventoryEquals(left.Inventory, right.Inventory);

    }

    internal static bool ReceiptEquals(
        CampaignPathFullInstallationResetCleanupReceipt? left,
        CampaignPathFullInstallationResetCleanupReceipt? right)
    {

        if (left is null || right is null)
        {

            return left is null && right is null;

        }

        if (!ReceiptIsValid(left)
            || !ReceiptIsValid(right)
            || left.OwnerOperationId != right.OwnerOperationId
            || !DigestEquals(left.OwnerEffectDigest, right.OwnerEffectDigest)
            || left.MarkerIntentCount != right.MarkerIntentCount
            || !DigestEquals(
                left.MarkerIntentVectorDigest,
                right.MarkerIntentVectorDigest)
            || left.DeletedCount != right.DeletedCount
            || left.OrphanCount != right.OrphanCount
            || left.OrderedMarkerIntentIds.Length
                != right.OrderedMarkerIntentIds.Length)
        {

            return false;

        }

        for (int index = 0; index < left.OrderedMarkerIntentIds.Length; index++)
        {

            if (left.OrderedMarkerIntentIds[index]
                != right.OrderedMarkerIntentIds[index])
            {

                return false;

            }

        }

        return true;

    }

    private static bool InventoryIsValid(
        CampaignPathFullInstallationResetInventory inventory) =>
        CampaignPathFullInstallationResetInventory.Create(
            inventory.OwnerOperationId,
            inventory.Entries,
            inventory.InventoryDigest).IsSuccess;

    private static bool PreparationIsValid(
        CampaignPathFullInstallationResetCleanupPreparation preparation) =>
        CampaignPathFullInstallationResetCleanupPreparation.Create(
            preparation.OwnerOperationId,
            preparation.OwnerEffectDigest,
            preparation.Inventory).IsSuccess;

    private static bool ReceiptIsValid(
        CampaignPathFullInstallationResetCleanupReceipt receipt) =>
        receipt.DeletedCount == 0
            && receipt.OrphanCount == 0
            ? CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
                receipt.OwnerOperationId,
                receipt.OwnerEffectDigest,
                receipt.OrderedMarkerIntentIds,
                receipt.MarkerIntentVectorDigest).IsSuccess
            : CampaignPathFullInstallationResetCleanupReceipt.CreateTerminal(
                receipt.OwnerOperationId,
                receipt.OwnerEffectDigest,
                receipt.OrderedMarkerIntentIds,
                receipt.MarkerIntentVectorDigest,
                receipt.DeletedCount,
                receipt.OrphanCount).IsSuccess;

    private static bool DigestEquals(CovenantDigest left, CovenantDigest right) =>
        left.IsValid
        && right.IsValid
        && left.Bytes.AsSpan().SequenceEqual(right.Bytes);

}
