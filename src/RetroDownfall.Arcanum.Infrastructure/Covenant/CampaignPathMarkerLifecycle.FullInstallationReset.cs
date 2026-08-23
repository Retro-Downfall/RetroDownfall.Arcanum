using System.Collections.Immutable;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using FullInstallationResetMarkerCleanupAuthority =
    RetroDownfall.Arcanum.Infrastructure.InstallationReset.HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

internal sealed partial class CampaignPathMarkerLifecycle
{

    public async Task<Result<CampaignPathFullInstallationResetInventory>>
        InventoryFullInstallationResetCleanupAsync(
            Guid ownerOperationId,
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken)
    {

        if (_recoveryKeys is null
            || ownerOperationId == Guid.Empty
            || liveCoreConnection is null)
        {

            return Inert<CampaignPathFullInstallationResetInventory>();

        }

        bool retainRoots = false;

        try
        {

            Result<List<FullResetRegisteredRoot>> registered =
                await ReadFullResetRegisteredRootsAsync(
                    liveCoreConnection,
                    cancellationToken).ConfigureAwait(false);

            if (registered.IsFailure)
            {

                return Inert<CampaignPathFullInstallationResetInventory>();

            }

            if (registered.Value.Count == 0)
            {

                ImmutableArray<CampaignMarkerInventoryEntryV1> empty = [];

                Result<CovenantDigest> emptyDigest =
                    FullInstallationResetMarkerPairResetDigests.CampaignInventory(empty);

                Result<CampaignPathFullInstallationResetInventory> emptyInventory =
                    emptyDigest.IsSuccess
                        ? CampaignPathFullInstallationResetInventory.Create(
                            ownerOperationId,
                            empty,
                            emptyDigest.Value)
                        : Inert<CampaignPathFullInstallationResetInventory>();

                retainRoots = emptyInventory.IsSuccess;

                return emptyInventory;

            }

            if (!TryPreflightExistingRootIdentityKey())
            {

                return Inert<CampaignPathFullInstallationResetInventory>();

            }

            ImmutableArray<CampaignMarkerInventoryEntryV1>.Builder entries =
                ImmutableArray.CreateBuilder<CampaignMarkerInventoryEntryV1>(
                    registered.Value.Count);

            foreach (FullResetRegisteredRoot root in registered.Value)
            {

                Result<CampaignPathMarkerRootAuthority> opened =
                    await CampaignPathMarkerRootAuthority.Instance.OpenExistingAsync(
                        _rootOpener,
                        root.CampaignId,
                        root.Revision,
                        root.IndexedIdentityDigest,
                        root.DisplayPath,
                        cancellationToken).ConfigureAwait(false);

                if (opened.IsFailure)
                {

                    return Inert<CampaignPathFullInstallationResetInventory>();

                }

                FullResetRetainedRootKey retainedKey = new(
                    ownerOperationId,
                    root.CampaignId);

                if (!TryRetainFullResetRoot(retainedKey, opened.Value))
                {

                    await opened.Value.DisposeAsync().ConfigureAwait(false);

                    return Inert<CampaignPathFullInstallationResetInventory>();

                }

                Result<MarkerOwnershipEvidence> proof = await ProveMarkerOwnershipAsync(
                    opened.Value,
                    root.CampaignId,
                    root.Revision,
                    cancellationToken).ConfigureAwait(false);

                Result<CovenantDigest> displayPathDigest =
                    FullInstallationResetMarkerPairResetDigests.CampaignDisplayPath(
                        root.DisplayPath);

                Result<CovenantDigest> ownershipDigest = proof.IsSuccess
                    ? FullInstallationResetMarkerPairResetDigests.SameHandleOwnership(
                        root.CampaignId,
                        root.Revision,
                        proof.Value.MarkerDigest,
                        root.IndexedIdentityDigest,
                        opened.Value.PhysicalIdentityDigest,
                        proof.Value.RootVolumeId,
                        proof.Value.RootFileId)
                    : Result<CovenantDigest>.Failure(proof.Error);

                if (proof.IsFailure
                    || displayPathDigest.IsFailure
                    || ownershipDigest.IsFailure)
                {

                    return Inert<CampaignPathFullInstallationResetInventory>();

                }

                entries.Add(new CampaignMarkerInventoryEntryV1(
                    root.CampaignId,
                    root.Revision,
                    proof.Value.MarkerDigest,
                    root.IndexedIdentityDigest,
                    displayPathDigest.Value,
                    ownershipDigest.Value));

            }

            ImmutableArray<CampaignMarkerInventoryEntryV1> orderedEntries =
                entries.MoveToImmutable();

            Result<CovenantDigest> inventoryDigest =
                FullInstallationResetMarkerPairResetDigests.CampaignInventory(orderedEntries);

            Result<CampaignPathFullInstallationResetInventory> inventory =
                inventoryDigest.IsSuccess
                    ? CampaignPathFullInstallationResetInventory.Create(
                        ownerOperationId,
                        orderedEntries,
                        inventoryDigest.Value)
                    : Inert<CampaignPathFullInstallationResetInventory>();

            retainRoots = inventory.IsSuccess;

            return inventory;

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception exception) when (
            exception is SqliteException
                or IOException
                or InvalidOperationException
                or ArgumentException
                or NotSupportedException
                or OverflowException)
        {

            return Inert<CampaignPathFullInstallationResetInventory>();

        }
        finally
        {

            if (!retainRoots)
            {

                await ReleaseRetainedRootsAsync(ownerOperationId).ConfigureAwait(false);

            }

        }

    }

    private bool TryPreflightExistingRootIdentityKey()
    {

        Span<byte> key = stackalloc byte[32];

        try
        {

            return _recoveryKeys!.TryCopyExistingRootIdentityKey(key);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(key);

        }

    }

    private static async Task<Result<List<FullResetRegisteredRoot>>>
        ReadFullResetRegisteredRootsAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {

        List<FullResetRegisteredRoot> roots = [];

        HashSet<Guid> campaigns = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT CampaignId, PolicyVersion, Revision, DisplayPath, PhysicalIdentityDigest
            FROM campaign_path_identities;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (roots.Count == HostToolsMarkerPairResetCheckpointBounds.MaximumVectorCount)
            {

                return Inert<List<FullResetRegisteredRoot>>();

            }

            if (reader.GetValue(0) is not string campaignText
                || !Guid.TryParse(campaignText, out Guid campaignId)
                || campaignId == Guid.Empty
                || !campaigns.Add(campaignId)
                || reader.GetValue(1) is not long policyVersion
                || policyVersion != CampaignPathIdentityPolicy.Version
                || reader.GetValue(2) is not long revision
                || revision <= 0
                || reader.GetValue(3) is not string displayPath
                || !IsCanonicalDisplayPath(displayPath)
                || reader.GetValue(4) is not byte[] identityBytes
                || identityBytes.Length != CovenantLimits.DigestBytes)
            {

                return Inert<List<FullResetRegisteredRoot>>();

            }

            roots.Add(new FullResetRegisteredRoot(
                campaignId,
                revision,
                displayPath,
                new CovenantDigest(identityBytes)));

        }

        roots.Sort(static (left, right) =>
            FullInstallationResetCanonicalEvidenceV1.CompareGuid(
                left.CampaignId,
                right.CampaignId));

        return roots;

    }

    private static bool IsCanonicalDisplayPath(string displayPath)
    {

        if (string.IsNullOrWhiteSpace(displayPath) || displayPath.Length > 4096)
        {
            return false;
        }

        try
        {

            return string.Equals(
                Path.GetFullPath(displayPath),
                displayPath,
                StringComparison.Ordinal);

        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {

            return false;

        }

    }

    public async Task<Result> RevalidateFullInstallationResetInventoryAsync(
        CampaignPathFullInstallationResetInventory inventory,
        SqliteConnection liveCoreConnection,
        CancellationToken cancellationToken)
    {

        Guid ownerOperationId = inventory?.OwnerOperationId ?? Guid.Empty;

        bool retainRoots = false;

        try
        {

            if (_recoveryKeys is null
                || inventory is null
                || liveCoreConnection is null)
            {

                return Inert();

            }

            Result<CampaignPathFullInstallationResetInventory> frozen =
                CampaignPathFullInstallationResetInventory.Create(
                    inventory.OwnerOperationId,
                    inventory.Entries,
                    inventory.InventoryDigest);

            if (frozen.IsFailure)
            {

                return Inert();

            }

            Result<List<FullResetRegisteredRoot>> registered =
                await ReadFullResetRegisteredRootsAsync(
                    liveCoreConnection,
                    cancellationToken).ConfigureAwait(false);

            if (registered.IsFailure
                || registered.Value.Count != frozen.Value.Entries.Length)
            {

                return Inert();

            }

            ImmutableArray<CampaignMarkerInventoryEntryV1>.Builder reproducedEntries =
                ImmutableArray.CreateBuilder<CampaignMarkerInventoryEntryV1>(
                    registered.Value.Count);

            for (int index = 0; index < registered.Value.Count; index++)
            {

                FullResetRegisteredRoot root = registered.Value[index];

                CampaignMarkerInventoryEntryV1 expected = frozen.Value.Entries[index];

                Result<CovenantDigest> displayPathDigest =
                    FullInstallationResetMarkerPairResetDigests.CampaignDisplayPath(
                        root.DisplayPath);

                if (displayPathDigest.IsFailure
                    || root.CampaignId != expected.CampaignId
                    || root.Revision != expected.PriorPathRevision
                    || root.IndexedIdentityDigest != expected.IndexedPhysicalIdentityDigest
                    || displayPathDigest.Value != expected.CanonicalDisplayPathDigest)
                {

                    return Inert();

                }

                FullResetRetainedRootKey retainedKey = new(
                    ownerOperationId,
                    root.CampaignId);

                if (!_fullResetRetainedRoots.TryGetValue(
                        retainedKey,
                        out CampaignPathMarkerRootAuthority? retained)
                    || retained.CampaignId != root.CampaignId
                    || retained.PathRevision != root.Revision
                    || retained.PhysicalIdentityDigest != root.IndexedIdentityDigest)
                {

                    return Inert();

                }

                Result<MarkerOwnershipEvidence> proof = await ProveMarkerOwnershipAsync(
                    retained,
                    root.CampaignId,
                    root.Revision,
                    cancellationToken).ConfigureAwait(false);

                Result<CovenantDigest> ownershipDigest = proof.IsSuccess
                    ? FullInstallationResetMarkerPairResetDigests.SameHandleOwnership(
                        root.CampaignId,
                        root.Revision,
                        proof.Value.MarkerDigest,
                        root.IndexedIdentityDigest,
                        retained.PhysicalIdentityDigest,
                        proof.Value.RootVolumeId,
                        proof.Value.RootFileId)
                    : Result<CovenantDigest>.Failure(proof.Error);

                if (proof.IsFailure || ownershipDigest.IsFailure)
                {

                    return Inert();

                }

                reproducedEntries.Add(new CampaignMarkerInventoryEntryV1(
                    root.CampaignId,
                    root.Revision,
                    proof.Value.MarkerDigest,
                    root.IndexedIdentityDigest,
                    displayPathDigest.Value,
                    ownershipDigest.Value));

            }

            ImmutableArray<CampaignMarkerInventoryEntryV1> entries =
                reproducedEntries.MoveToImmutable();

            Result<CovenantDigest> inventoryDigest =
                FullInstallationResetMarkerPairResetDigests.CampaignInventory(entries);

            Result<CampaignPathFullInstallationResetInventory> reproduced =
                inventoryDigest.IsSuccess
                    ? CampaignPathFullInstallationResetInventory.Create(
                        ownerOperationId,
                        entries,
                        inventoryDigest.Value)
                    : Inert<CampaignPathFullInstallationResetInventory>();

            if (reproduced.IsFailure
                || !CampaignPathFullInstallationResetContractComparer.InventoryEquals(
                    frozen.Value,
                    reproduced.Value))
            {

                return Inert();

            }

            retainRoots = true;

            return Result.Success();

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception exception) when (
            exception is SqliteException
                or IOException
                or InvalidOperationException
                or ArgumentException
                or NotSupportedException
                or OverflowException)
        {

            return Inert();

        }
        finally
        {

            if (!retainRoots && ownerOperationId != Guid.Empty)
            {

                await ReleaseRetainedRootsAsync(ownerOperationId).ConfigureAwait(false);

            }

        }

    }

    public Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
        PrepareFullInstallationResetCleanupAsync(
            CampaignPathFullInstallationResetCleanupPreparation preparation,
            CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt,
            FullInstallationResetMarkerCleanupAuthority authority,
            SqliteConnection liveCoreConnection,
            SqliteTransaction liveCoreTransaction,
            CancellationToken cancellationToken) =>
        Task.FromResult(Inert<CampaignPathFullInstallationResetCleanupReceipt>());

    public Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
        ReconcileFullInstallationResetCleanupAsync(
            CampaignPathFullInstallationResetCleanupReceipt prepared,
            FullInstallationResetMarkerCleanupAuthority authority,
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken) =>
        Task.FromResult(Inert<CampaignPathFullInstallationResetCleanupReceipt>());

    private static Result Inert() =>
        Result.Failure(new Error(
            ErrorCodes.Data.RecoveryRequired,
            "The full-installation reset Campaign cleanup is not available."));

    private static Result<T> Inert<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Data.RecoveryRequired,
            "The full-installation reset Campaign cleanup is not available."));

    private sealed record FullResetRegisteredRoot(
        Guid CampaignId,
        long Revision,
        string DisplayPath,
        CovenantDigest IndexedIdentityDigest);

}
