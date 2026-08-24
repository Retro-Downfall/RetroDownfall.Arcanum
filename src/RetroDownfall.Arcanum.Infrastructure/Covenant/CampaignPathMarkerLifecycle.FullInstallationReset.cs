using System.Collections.Immutable;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Tower;

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

                Result<CampaignPathMarkerRootAuthority> opened = await OpenRegisteredRootExactAsync(
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

    /// <summary>
    /// The one place in the full-reset arm that turns a recorded display path into an open root.
    /// </summary>
    /// <remarks>
    /// Inventory, first preparation, and replay rehydration all reach a root the same way, and they
    /// reach it here. Keeping that to one call site is the invariant rather than a tidiness
    /// preference: a display path is not authority, and the open below believes the directory it
    /// finds only because it re-derives the recorded identity from the handle it actually obtained.
    /// A second call site would be a second chance to get that wrong, and a source-inventory test
    /// asserts this file holds exactly one (§10.12).
    /// </remarks>
    private ValueTask<Result<CampaignPathMarkerRootAuthority>> OpenRegisteredRootExactAsync(
        Guid campaignId,
        long revision,
        CovenantDigest indexedIdentityDigest,
        string displayPath,
        CancellationToken cancellationToken) =>
        CampaignPathMarkerRootAuthority.Instance.OpenExistingAsync(
            _rootOpener,
            campaignId,
            revision,
            indexedIdentityDigest,
            displayPath,
            cancellationToken);

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
            CancellationToken cancellationToken,
            SqliteTransaction? transaction = null)
    {

        List<FullResetRegisteredRoot> roots = [];

        HashSet<Guid> campaigns = [];

        await using SqliteCommand command = connection.CreateCommand();

        // Null before the caller owns a snapshot, and the caller's own transaction once it does.
        // Microsoft.Data.Sqlite refuses a command that ignores a pending transaction, so this is the
        // difference between reading the registry inside the preparation and not reading it at all.
        command.Transaction = transaction;

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

    /// <summary>
    /// Journals one immutable kind-four child per authenticated Campaign, or proves the children an
    /// earlier attempt already journaled are exactly the ones this attempt would have written.
    /// </summary>
    /// <remarks>
    /// The whole point of this seam is that it runs before either host-tools marker is touched, so
    /// its output is the only record of what the installation looked like while it was still intact.
    /// That makes idempotency a correctness property rather than a convenience: a second attempt
    /// that wrote a second vector, or adopted a vector it cannot reproduce, would be describing an
    /// installation nobody observed.
    ///
    /// <para>Replay is therefore established by comparing the complete existing vector — parent and
    /// companion, every field — against what this preparation would produce. Matching on the
    /// owner/Campaign/kind uniqueness alone would accept a child carrying a different observation,
    /// path hint, or effect, which is precisely the substitution the evidence exists to prevent.</para>
    ///
    /// <para>The caller's transaction is borrowed and never begun, committed, rolled back, or
    /// disposed. Parent and companion land inside it together, so the vector this returns either
    /// becomes durable whole when the caller commits, or was never written at all.</para>
    /// </remarks>
    public async Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
        PrepareFullInstallationResetCleanupAsync(
            CampaignPathFullInstallationResetCleanupPreparation preparation,
            CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt,
            FullInstallationResetMarkerCleanupAuthority authority,
            SqliteConnection liveCoreConnection,
            SqliteTransaction liveCoreTransaction,
            CancellationToken cancellationToken)
    {

        if (_recoveryKeys is null
            || preparation is null
            || authority is null
            || liveCoreConnection is null
            || liveCoreTransaction is null
            || !ReferenceEquals(liveCoreTransaction.Connection, liveCoreConnection))
        {

            return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

        }

        try
        {

            // Before any read and any write. The authority re-proves the authenticated checkpoint is
            // still the one this preparation belongs to, and that the caller's expected receipt is
            // the checkpoint's own — so a stale caller cannot drive the journal from a proof that
            // has since been superseded.
            Result revalidated = await authority.RevalidatePreparationAsync(
                preparation,
                expectedReceipt,
                cancellationToken).ConfigureAwait(false);

            if (revalidated.IsFailure)
            {

                return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

            }

            Result<CampaignPathFullInstallationResetCleanupPreparation> frozen =
                CampaignPathFullInstallationResetCleanupPreparation.Create(
                    preparation.OwnerOperationId,
                    preparation.OwnerEffectDigest,
                    preparation.Inventory);

            if (frozen.IsFailure)
            {

                return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

            }

            return await PrepareCleanupChildrenAsync(
                frozen.Value,
                expectedReceipt,
                liveCoreConnection,
                liveCoreTransaction,
                cancellationToken).ConfigureAwait(false);

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

            return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

        }

    }

    private async Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
        PrepareCleanupChildrenAsync(
            CampaignPathFullInstallationResetCleanupPreparation preparation,
            CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt,
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {

        Guid owner = preparation.OwnerOperationId;

        ImmutableArray<CampaignMarkerInventoryEntryV1> entries = preparation.Inventory.Entries;

        CampaignPathMarkerIntentStore intents = new(
            _initializer,
            connection,
            transaction,
            _timeProvider);

        CampaignPathFullResetCleanupEvidenceStore evidence = new(
            _initializer,
            connection,
            transaction);

        Result<IReadOnlyList<CampaignPathFullResetCleanupChildRow>> existing =
            await evidence.ReadOwnerChildrenAsync(owner, cancellationToken).ConfigureAwait(false);

        if (existing.IsFailure)
        {

            return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

        }

        // The joined read cannot see a kind-four parent whose companion row is absent as anything
        // but malformed, but it also cannot see one the join dropped. Counting the parents
        // independently is what makes "the vector is complete" an assertion rather than an
        // assumption about the join.
        long journaled = await intents
            .CountFullInstallationResetCleanupForOwnerAsync(owner, cancellationToken)
            .ConfigureAwait(false);

        if (journaled != existing.Value.Count)
        {

            return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

        }

        // A proven-empty inventory is idempotent by construction: there is nothing to journal and
        // nothing to compare, so first attempt and replay reach the same frozen receipt. The caller
        // still owns and still commits its transaction — zero effect is not zero protocol.
        if (entries.IsEmpty)
        {

            return existing.Value.Count == 0
                ? CompleteReceipt(preparation, [], expectedReceipt)
                : Inert<CampaignPathFullInstallationResetCleanupReceipt>();

        }

        if (existing.Value.Count > 0)
        {

            return await ReplayCleanupChildrenAsync(
                preparation,
                expectedReceipt,
                existing.Value,
                cancellationToken).ConfigureAwait(false);

        }

        // No children and a receipt the checkpoint already published cannot both be true: the
        // receipt names a vector this journal does not hold.
        if (expectedReceipt is not null)
        {

            return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

        }

        return await JournalCleanupChildrenAsync(
            preparation,
            intents,
            evidence,
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Writes the first vector: every seed observed before the first insertion, then every parent
    /// and companion committed in the caller's transaction.
    /// </summary>
    /// <remarks>
    /// Observation is completed before any row is written on purpose. Interleaving them would let a
    /// failure part-way through leave a journal describing some Campaigns as they were before the
    /// attempt and others as they were during it, and the caller's rollback is the only thing that
    /// makes the difference invisible.
    /// </remarks>
    private async Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
        JournalCleanupChildrenAsync(
            CampaignPathFullInstallationResetCleanupPreparation preparation,
            CampaignPathMarkerIntentStore intents,
            CampaignPathFullResetCleanupEvidenceStore evidence,
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {

        Result<List<FullResetCleanupSeed>> seeds = await ObserveCleanupSeedsAsync(
            preparation,
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (seeds.IsFailure)
        {

            return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

        }

        ImmutableArray<Guid>.Builder committed =
            ImmutableArray.CreateBuilder<Guid>(seeds.Value.Count);

        foreach (FullResetCleanupSeed seed in seeds.Value)
        {

            Result<Guid> intentId = await intents.InsertFullInstallationResetCleanupAsync(
                preparation.OwnerOperationId,
                preparation.OwnerEffectDigest,
                seed.Entry.CampaignId,
                seed.Entry.MarkerDigest,
                seed.TargetDisplayPath,
                seed.Entry.PriorPathRevision,
                cancellationToken).ConfigureAwait(false);

            if (intentId.IsFailure)
            {

                return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

            }

            Result written = await evidence.InsertAsync(
                new CampaignPathFullResetCleanupEvidenceRow(
                    intentId.Value,
                    seed.InventoryEntryDigest,
                    seed.Entry.IndexedPhysicalIdentityDigest,
                    seed.Entry.CanonicalDisplayPathDigest,
                    seed.Entry.SameHandleOwnershipEvidenceDigest,
                    seed.ObservationCode,
                    seed.OpenedSameHandleOwnershipEvidenceDigest,
                    seed.ObservationDigest),
                cancellationToken).ConfigureAwait(false);

            if (written.IsFailure)
            {

                return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

            }

            committed.Add(intentId.Value);

        }

        return CompleteReceipt(
            preparation,
            committed.MoveToImmutable(),
            expectedReceipt: null);

    }

    /// <summary>
    /// Decides the closed observation arm for every authenticated Campaign, without writing a row.
    /// </summary>
    /// <remarks>
    /// The current registry is reread first and compared with the authenticated pre-effect
    /// inventory. Only a Campaign that still agrees on location, revision, and identity may have its
    /// retained root supply deletion authority; a Campaign whose registration vanished or moved
    /// becomes a blocked child with no path and no authority at all, because the one thing this
    /// journal must never manufacture is a location nobody observed.
    /// </remarks>
    private async Task<Result<List<FullResetCleanupSeed>>> ObserveCleanupSeedsAsync(
        CampaignPathFullInstallationResetCleanupPreparation preparation,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        Result<List<FullResetRegisteredRoot>> registered =
            await ReadFullResetRegisteredRootsAsync(
                connection,
                cancellationToken,
                transaction).ConfigureAwait(false);

        if (registered.IsFailure)
        {

            return Inert<List<FullResetCleanupSeed>>();

        }

        Dictionary<Guid, FullResetRegisteredRoot> current =
            registered.Value.ToDictionary(static root => root.CampaignId);

        List<FullResetCleanupSeed> seeds = new(preparation.Inventory.Entries.Length);

        bool keyProven = false;

        foreach (CampaignMarkerInventoryEntryV1 entry in preparation.Inventory.Entries)
        {

            Result<CovenantDigest> entryDigest =
                FullInstallationResetMarkerPairResetDigests.CampaignInventoryEntry(entry);

            if (entryDigest.IsFailure)
            {

                return Inert<List<FullResetCleanupSeed>>();

            }

            Result<FullResetCleanupSeed> seed = await ObserveOneCleanupSeedAsync(
                preparation.OwnerOperationId,
                entry,
                entryDigest.Value,
                current,
                keyProven,
                cancellationToken).ConfigureAwait(false);

            if (seed.IsFailure)
            {

                return Inert<List<FullResetCleanupSeed>>();

            }

            keyProven |= seed.Value.RequiredRootIdentityKey;

            seeds.Add(seed.Value);

        }

        return seeds;

    }

    private async Task<Result<FullResetCleanupSeed>> ObserveOneCleanupSeedAsync(
        Guid owner,
        CampaignMarkerInventoryEntryV1 entry,
        CovenantDigest entryDigest,
        Dictionary<Guid, FullResetRegisteredRoot> current,
        bool keyAlreadyProven,
        CancellationToken cancellationToken)
    {

        // A registration that is simply gone is unavailable, not a mismatch. The distinction is the
        // difference between "there is nothing left to clean up here" and "something is there and it
        // is not what was authenticated", and reconciliation treats them differently.
        if (!current.TryGetValue(entry.CampaignId, out FullResetRegisteredRoot? root))
        {

            return Blocked(
                entry,
                entryDigest,
                CampaignPathFullResetCleanupObservationCode.Unavailable);

        }

        Result<CovenantDigest> displayPathDigest =
            FullInstallationResetMarkerPairResetDigests.CampaignDisplayPath(root.DisplayPath);

        if (displayPathDigest.IsFailure
            || root.Revision != entry.PriorPathRevision
            || root.IndexedIdentityDigest != entry.IndexedPhysicalIdentityDigest
            || displayPathDigest.Value != entry.CanonicalDisplayPathDigest)
        {

            return Blocked(
                entry,
                entryDigest,
                CampaignPathFullResetCleanupObservationCode.Mismatch);

        }

        FullResetRetainedRootKey retainedKey = new(owner, entry.CampaignId);

        bool requiredKey = false;

        CampaignPathMarkerRootAuthority? opened = null;

        bool ownsOpened = false;

        try
        {

            if (!_fullResetRetainedRoots.TryGetValue(retainedKey, out opened))
            {

                // A restarted process retained nothing, so this Campaign needs a fresh exact reopen.
                // The identity key is the first thing proven, before the opener, the codec, the
                // marker, or any other filesystem access: without it the reopen could not be
                // completed anyway, and attempting it would touch the workspace for nothing.
                if (!keyAlreadyProven && !TryPreflightExistingRootIdentityKey())
                {

                    return Inert<FullResetCleanupSeed>();

                }

                requiredKey = true;

                Result<CampaignPathMarkerRootAuthority> reopened = await OpenRegisteredRootExactAsync(
                    root.CampaignId,
                    root.Revision,
                    root.IndexedIdentityDigest,
                    root.DisplayPath,
                    cancellationToken).ConfigureAwait(false);

                if (reopened.IsFailure)
                {

                    return Blocked(
                        entry,
                        entryDigest,
                        CampaignPathFullResetCleanupObservationCode.Mismatch,
                        requiredKey);

                }

                opened = reopened.Value;

                ownsOpened = true;

                if (!TryRetainFullResetRoot(retainedKey, opened))
                {

                    return Inert<FullResetCleanupSeed>();

                }

                ownsOpened = false;

            }

            Result<MarkerOwnershipEvidence> proof = await ProveMarkerOwnershipAsync(
                opened,
                root.CampaignId,
                root.Revision,
                cancellationToken).ConfigureAwait(false);

            Result<CovenantDigest> ownershipDigest = proof.IsSuccess
                ? FullInstallationResetMarkerPairResetDigests.SameHandleOwnership(
                    root.CampaignId,
                    root.Revision,
                    proof.Value.MarkerDigest,
                    root.IndexedIdentityDigest,
                    opened.PhysicalIdentityDigest,
                    proof.Value.RootVolumeId,
                    proof.Value.RootFileId)
                : Result<CovenantDigest>.Failure(proof.Error);

            // Opened means what was observed equals what was authenticated. Anything else — a marker
            // that no longer proves its root, a root whose identity moved, a digest that simply does
            // not agree — is a mismatch, and a mismatch never carries a path hint.
            if (proof.IsFailure
                || ownershipDigest.IsFailure
                || ownershipDigest.Value != entry.SameHandleOwnershipEvidenceDigest)
            {

                return Blocked(
                    entry,
                    entryDigest,
                    CampaignPathFullResetCleanupObservationCode.Mismatch,
                    requiredKey);

            }

            Result<CovenantDigest> observationDigest =
                FullInstallationResetMarkerPairResetDigests.CampaignObservation(
                    CampaignPathFullResetCleanupObservationCode.Opened,
                    entryDigest,
                    entry.SameHandleOwnershipEvidenceDigest);

            return observationDigest.IsSuccess
                ? new FullResetCleanupSeed(
                    entry,
                    entryDigest,
                    CampaignPathFullResetCleanupObservationCode.Opened,
                    root.DisplayPath,
                    entry.SameHandleOwnershipEvidenceDigest,
                    observationDigest.Value,
                    requiredKey)
                : Inert<FullResetCleanupSeed>();

        }
        finally
        {

            if (ownsOpened && opened is not null)
            {

                await opened.DisposeAsync().ConfigureAwait(false);

            }

        }

    }

    /// <summary>
    /// Proves the journal already holds exactly the vector this preparation would have written.
    /// </summary>
    /// <remarks>
    /// Every field of every child is reproduced from the authenticated inventory and compared, and
    /// the observation digest is recomputed rather than read back — a stored digest that agreed with
    /// a substituted code would otherwise authenticate the substitution.
    /// </remarks>
    private async Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
        ReplayCleanupChildrenAsync(
            CampaignPathFullInstallationResetCleanupPreparation preparation,
            CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt,
            IReadOnlyList<CampaignPathFullResetCleanupChildRow> existing,
            CancellationToken cancellationToken)
    {

        ImmutableArray<CampaignMarkerInventoryEntryV1> entries = preparation.Inventory.Entries;

        if (existing.Count != entries.Length)
        {

            return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

        }

        Dictionary<Guid, CampaignPathFullResetCleanupChildRow> byCampaign =
            existing.ToDictionary(static child => child.Intent.CampaignId);

        ImmutableArray<Guid>.Builder ordered = ImmutableArray.CreateBuilder<Guid>(entries.Length);

        List<CampaignPathFullResetCleanupChildRow> rehydratable = [];

        foreach (CampaignMarkerInventoryEntryV1 entry in entries)
        {

            if (!byCampaign.TryGetValue(
                    entry.CampaignId,
                    out CampaignPathFullResetCleanupChildRow? child)
                || !ChildMatchesEntry(preparation, entry, child))
            {

                return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

            }

            ordered.Add(child.Intent.IntentId);

            if (child.Intent.Phase is CampaignPathMarkerPhase.Prepared
                && child.Evidence.ObservationCode
                    is CampaignPathFullResetCleanupObservationCode.Opened)
            {

                rehydratable.Add(child);

            }

        }

        Result<CampaignPathFullInstallationResetCleanupReceipt> receipt = CompleteReceipt(
            preparation,
            ordered.MoveToImmutable(),
            expectedReceipt);

        if (receipt.IsFailure)
        {

            return receipt;

        }

        Result rehydrated = await RehydrateOpenedChildrenAsync(
            preparation.OwnerOperationId,
            rehydratable,
            cancellationToken).ConfigureAwait(false);

        return rehydrated.IsSuccess
            ? receipt
            : Inert<CampaignPathFullInstallationResetCleanupReceipt>();

    }

    /// <summary>
    /// Reopens the roots a still-prepared opened child will need, for a process that retained none.
    /// </summary>
    /// <remarks>
    /// Blocked and terminal children are skipped entirely, so a replay that has nothing left to open
    /// makes no credential, opener, codec, marker, or filesystem call at all. When there is
    /// something to open, the identity key is proven once up front and the whole replay fails if it
    /// is unavailable — reopening some roots and refusing the rest would leave the process holding
    /// partial authority over a vector it just declared exact.
    ///
    /// <para>An individual root that can no longer be reopened is not a failure of the replay. The
    /// child's evidence is immutable and already records what was observed while the installation
    /// was intact; reconciliation is what advances that child to a manual blocker, and it needs the
    /// receipt this call is returning in order to get there.</para>
    /// </remarks>
    private async Task<Result> RehydrateOpenedChildrenAsync(
        Guid owner,
        IReadOnlyList<CampaignPathFullResetCleanupChildRow> children,
        CancellationToken cancellationToken)
    {

        List<CampaignPathFullResetCleanupChildRow> pending =
        [
            .. children.Where(child =>
                !_fullResetRetainedRoots.ContainsKey(
                    new FullResetRetainedRootKey(owner, child.Intent.CampaignId))),
        ];

        if (pending.Count == 0)
        {

            return Result.Success();

        }

        if (!TryPreflightExistingRootIdentityKey())
        {

            return Inert();

        }

        foreach (CampaignPathFullResetCleanupChildRow child in pending)
        {

            // The committed hint is believed only because the companion beside it commits to the
            // same path. A hint that no longer hashes to the authenticated display-path digest is a
            // rewritten row, not a location to go looking at.
            if (child.Intent.TargetDisplayPath is not { } hint)
            {

                continue;

            }

            Result<CovenantDigest> hintDigest =
                FullInstallationResetMarkerPairResetDigests.CampaignDisplayPath(hint);

            if (hintDigest.IsFailure
                || hintDigest.Value != child.Evidence.CanonicalDisplayPathDigest)
            {

                return Inert();

            }

            Result<CampaignPathMarkerRootAuthority> reopened = await OpenRegisteredRootExactAsync(
                child.Intent.CampaignId,
                child.Intent.PriorRevision,
                child.Evidence.IndexedPhysicalIdentityDigest,
                hint,
                cancellationToken).ConfigureAwait(false);

            if (reopened.IsFailure)
            {

                continue;

            }

            if (!TryRetainFullResetRoot(
                    new FullResetRetainedRootKey(owner, child.Intent.CampaignId),
                    reopened.Value))
            {

                await reopened.Value.DisposeAsync().ConfigureAwait(false);

                return Inert();

            }

        }

        return Result.Success();

    }

    private static bool ChildMatchesEntry(
        CampaignPathFullInstallationResetCleanupPreparation preparation,
        CampaignMarkerInventoryEntryV1 entry,
        CampaignPathFullResetCleanupChildRow child)
    {

        Result<CovenantDigest> entryDigest =
            FullInstallationResetMarkerPairResetDigests.CampaignInventoryEntry(entry);

        if (entryDigest.IsFailure
            || child.Intent.IntentId == Guid.Empty
            || child.Intent.OwnerOperationId != preparation.OwnerOperationId
            || child.Intent.Kind is not CampaignPathMarkerIntentKind.FullInstallationResetCleanup
            || child.Intent.ExclusiveOwnerOperation is not null
            || child.Intent.PendingDisposition is not null
            || child.Intent.OwnerEffectDigest != preparation.OwnerEffectDigest
            || child.Intent.MarkerDigest != entry.MarkerDigest
            || child.Intent.PriorRevision != entry.PriorPathRevision
            || child.Intent.PhaseRevision <= 0
            || child.Intent.Phase is not (CampaignPathMarkerPhase.Prepared
                or CampaignPathMarkerPhase.Completed
                or CampaignPathMarkerPhase.ManualBlocker)
            || child.Evidence.IntentId != child.Intent.IntentId
            || child.Evidence.CampaignInventoryEntryDigest != entryDigest.Value
            || child.Evidence.IndexedPhysicalIdentityDigest != entry.IndexedPhysicalIdentityDigest
            || child.Evidence.CanonicalDisplayPathDigest != entry.CanonicalDisplayPathDigest
            || child.Evidence.SameHandleOwnershipEvidenceDigest
                != entry.SameHandleOwnershipEvidenceDigest)
        {

            return false;

        }

        Result<CovenantDigest> observationDigest =
            FullInstallationResetMarkerPairResetDigests.CampaignObservation(
                child.Evidence.ObservationCode,
                entryDigest.Value,
                child.Evidence.OpenedSameHandleOwnershipEvidenceDigest);

        if (observationDigest.IsFailure
            || observationDigest.Value != child.Evidence.ObservationDigest)
        {

            return false;

        }

        // The path hint and the observation arm are one decision. An opened child that lost its hint
        // could not be rehydrated, and a blocked child that gained one would be handing the next
        // phase a location its own evidence says was never opened.
        return child.Evidence.ObservationCode switch
        {

            CampaignPathFullResetCleanupObservationCode.Opened =>
                child.Intent.TargetDisplayPath is { } hint
                    && FullInstallationResetMarkerPairResetDigests.CampaignDisplayPath(hint)
                        is { IsSuccess: true } hinted
                    && hinted.Value == entry.CanonicalDisplayPathDigest,

            CampaignPathFullResetCleanupObservationCode.Unavailable
                or CampaignPathFullResetCleanupObservationCode.Mismatch =>
                child.Intent.TargetDisplayPath is null,

            _ => false,

        };

    }

    /// <summary>
    /// Builds the prepared receipt and, on replay, requires it to be the one already published.
    /// </summary>
    private static Result<CampaignPathFullInstallationResetCleanupReceipt> CompleteReceipt(
        CampaignPathFullInstallationResetCleanupPreparation preparation,
        ImmutableArray<Guid> orderedIntentIds,
        CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt)
    {

        Result<CovenantDigest> vector =
            FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(orderedIntentIds);

        if (vector.IsFailure)
        {

            return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

        }

        Result<CampaignPathFullInstallationResetCleanupReceipt> receipt =
            CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
                preparation.OwnerOperationId,
                preparation.OwnerEffectDigest,
                orderedIntentIds,
                vector.Value);

        if (receipt.IsFailure)
        {

            return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

        }

        // Order matters here and is not incidental: the identifiers follow authenticated Campaign
        // order, so a vector holding the same identifiers in a different order produces a different
        // digest and fails this comparison rather than passing it by set equality.
        return expectedReceipt is null
            || CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
                expectedReceipt,
                receipt.Value)
                ? receipt
                : Inert<CampaignPathFullInstallationResetCleanupReceipt>();

    }

    private static Result<FullResetCleanupSeed> Blocked(
        CampaignMarkerInventoryEntryV1 entry,
        CovenantDigest entryDigest,
        CampaignPathFullResetCleanupObservationCode code,
        bool requiredRootIdentityKey = false)
    {

        Result<CovenantDigest> observationDigest =
            FullInstallationResetMarkerPairResetDigests.CampaignObservation(
                code,
                entryDigest,
                openedSameHandleOwnershipEvidenceDigest: null);

        return observationDigest.IsSuccess
            ? new FullResetCleanupSeed(
                entry,
                entryDigest,
                code,
                TargetDisplayPath: null,
                OpenedSameHandleOwnershipEvidenceDigest: null,
                observationDigest.Value,
                requiredRootIdentityKey)
            : Inert<FullResetCleanupSeed>();

    }

    /// <summary>
    /// Drives every prepared child to a terminal phase, deleting only what it can prove.
    /// </summary>
    /// <remarks>
    /// The caller's live core connection is borrowed and never disposed — the coordinator still has
    /// its own effects to run on it — and every mutation lands in its own short transaction, because
    /// one transaction spanning every Campaign would make a failure at the last marker discard the
    /// terminal evidence of all the ones already deleted.
    ///
    /// <para>Authority is revalidated before the vector is read, before each child, and again before
    /// each of those short transactions. Every active-record publication invalidates the proof bound
    /// to the previous envelope revision, so a reconciliation that authenticated once at the top
    /// would be running the rest of its effects on a journal that may since have moved.</para>
    ///
    /// <para>Nothing here releases a retained root. Release belongs to the coordinator, after the
    /// terminal receipt is durably published: a seam that released as it went would drop the
    /// authority a retry needs while that retry is still the only thing that can finish the
    /// operation.</para>
    /// </remarks>
    public async Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
        ReconcileFullInstallationResetCleanupAsync(
            CampaignPathFullInstallationResetCleanupReceipt prepared,
            FullInstallationResetMarkerCleanupAuthority authority,
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken)
    {

        if (_recoveryKeys is null
            || prepared is null
            || authority is null
            || liveCoreConnection is null)
        {

            return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

        }

        try
        {

            // Before the first read. The receipt has to be the one the authenticated checkpoint
            // published, or the vector about to be walked belongs to a different operation.
            Result revalidated = await authority.RevalidateReceiptAsync(
                prepared,
                cancellationToken).ConfigureAwait(false);

            if (revalidated.IsFailure)
            {

                return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

            }

            Result<List<CampaignPathFullResetCleanupChildRow>> ordered =
                await ReadOrderedCleanupChildrenAsync(
                    prepared,
                    liveCoreConnection,
                    cancellationToken).ConfigureAwait(false);

            if (ordered.IsFailure)
            {

                return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

            }

            return await TerminalizeCleanupChildrenAsync(
                prepared,
                ordered.Value,
                authority,
                liveCoreConnection,
                cancellationToken).ConfigureAwait(false);

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
                or ObjectDisposedException
                or OverflowException)
        {

            return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

        }

    }

    /// <summary>
    /// Reads the durable vector and proves it is exactly the one the authenticated receipt names.
    /// </summary>
    /// <remarks>
    /// The independent parent count sits beside the joined read for the same reason it does during
    /// preparation: the join cannot see a kind-four parent whose companion is missing as anything but
    /// absent, and a shorter vector that still matched the receipt position by position would let a
    /// deleted companion pass as a complete journal.
    /// </remarks>
    private static async Task<Result<List<CampaignPathFullResetCleanupChildRow>>>
        ReadOrderedCleanupChildrenAsync(
            CampaignPathFullInstallationResetCleanupReceipt prepared,
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

        CampaignPathMarkerIntentStore intents = new(
            CovenantSqliteConnectionInitializer.Instance,
            connection,
            transaction,
            TimeProvider.System);

        CampaignPathFullResetCleanupEvidenceStore evidence = new(
            CovenantSqliteConnectionInitializer.Instance,
            connection,
            transaction);

        Result<IReadOnlyList<CampaignPathFullResetCleanupChildRow>> existing =
            await evidence.ReadOwnerChildrenAsync(
                prepared.OwnerOperationId,
                cancellationToken).ConfigureAwait(false);

        long journaled = await intents
            .CountFullInstallationResetCleanupForOwnerAsync(
                prepared.OwnerOperationId,
                cancellationToken).ConfigureAwait(false);

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        if (existing.IsFailure
            || journaled != existing.Value.Count
            || existing.Value.Count != prepared.OrderedMarkerIntentIds.Length)
        {

            return Inert<List<CampaignPathFullResetCleanupChildRow>>();

        }

        Dictionary<Guid, CampaignPathFullResetCleanupChildRow> byIntent =
            existing.Value.ToDictionary(static child => child.Intent.IntentId);

        List<CampaignPathFullResetCleanupChildRow> ordered =
            new(prepared.OrderedMarkerIntentIds.Length);

        foreach (Guid intentId in prepared.OrderedMarkerIntentIds)
        {

            if (!byIntent.TryGetValue(
                    intentId,
                    out CampaignPathFullResetCleanupChildRow? child)
                || !CleanupChildMatchesReceipt(prepared, child))
            {

                return Inert<List<CampaignPathFullResetCleanupChildRow>>();

            }

            ordered.Add(child);

        }

        return ordered;

    }

    /// <summary>
    /// Reproves one child's shape from its own immutable evidence, without the inventory.
    /// </summary>
    /// <remarks>
    /// The observation digest is recomputed rather than read back, so a stored digest that agreed
    /// with a substituted code cannot authenticate the substitution. The path hint is checked against
    /// the authenticated display-path digest for the same reason: a blocked child that gained a hint
    /// would be offering the deletion arm a location its own evidence says nobody opened.
    /// </remarks>
    private static bool CleanupChildMatchesReceipt(
        CampaignPathFullInstallationResetCleanupReceipt prepared,
        CampaignPathFullResetCleanupChildRow child)
    {

        if (child.Intent.IntentId == Guid.Empty
            || child.Intent.OwnerOperationId != prepared.OwnerOperationId
            || child.Intent.Kind is not CampaignPathMarkerIntentKind.FullInstallationResetCleanup
            || child.Intent.OwnerEffectDigest != prepared.OwnerEffectDigest
            || child.Intent.ExclusiveOwnerOperation is not null
            || child.Intent.PendingDisposition is not null
            || child.Intent.PriorRevision <= 0
            || child.Intent.PhaseRevision <= 0
            || child.Intent.Phase is not (CampaignPathMarkerPhase.Prepared
                or CampaignPathMarkerPhase.Completed
                or CampaignPathMarkerPhase.ManualBlocker)
            || child.Evidence.IntentId != child.Intent.IntentId)
        {

            return false;

        }

        Result<CovenantDigest> observationDigest =
            FullInstallationResetMarkerPairResetDigests.CampaignObservation(
                child.Evidence.ObservationCode,
                child.Evidence.CampaignInventoryEntryDigest,
                child.Evidence.OpenedSameHandleOwnershipEvidenceDigest);

        if (observationDigest.IsFailure
            || observationDigest.Value != child.Evidence.ObservationDigest)
        {

            return false;

        }

        return child.Evidence.ObservationCode switch
        {

            CampaignPathFullResetCleanupObservationCode.Opened =>
                child.Evidence.OpenedSameHandleOwnershipEvidenceDigest
                    == child.Evidence.SameHandleOwnershipEvidenceDigest
                && child.Intent.TargetDisplayPath is { } hint
                && FullInstallationResetMarkerPairResetDigests.CampaignDisplayPath(hint)
                    is { IsSuccess: true } hinted
                && hinted.Value == child.Evidence.CanonicalDisplayPathDigest,

            CampaignPathFullResetCleanupObservationCode.Unavailable
                or CampaignPathFullResetCleanupObservationCode.Mismatch =>
                child.Evidence.OpenedSameHandleOwnershipEvidenceDigest is null
                && child.Intent.TargetDisplayPath is null,

            _ => false,

        };

    }

    /// <summary>
    /// Walks the ordered vector once, counting what was deleted against what was left as an orphan.
    /// </summary>
    private async Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
        TerminalizeCleanupChildrenAsync(
            CampaignPathFullInstallationResetCleanupReceipt prepared,
            List<CampaignPathFullResetCleanupChildRow> ordered,
            FullInstallationResetMarkerCleanupAuthority authority,
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {

        ulong deleted = 0;

        ulong orphaned = 0;

        foreach (CampaignPathFullResetCleanupChildRow child in ordered)
        {

            Result revalidated = await authority.RevalidateReceiptAsync(
                prepared,
                cancellationToken).ConfigureAwait(false);

            if (revalidated.IsFailure)
            {

                return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

            }

            // A child that is already terminal is counted from its durable phase and skipped
            // entirely. That is what makes an authenticated retry after the terminal publication
            // reach the identical receipt without opening a single handle.
            if (child.Intent.Phase is CampaignPathMarkerPhase.Completed)
            {

                deleted = checked(deleted + 1);

                continue;

            }

            if (child.Intent.Phase is CampaignPathMarkerPhase.ManualBlocker)
            {

                orphaned = checked(orphaned + 1);

                continue;

            }

            Result<bool> terminalized = await TerminalizeOneCleanupChildAsync(
                prepared,
                child,
                authority,
                connection,
                cancellationToken).ConfigureAwait(false);

            if (terminalized.IsFailure)
            {

                return Inert<CampaignPathFullInstallationResetCleanupReceipt>();

            }

            if (terminalized.Value)
            {

                deleted = checked(deleted + 1);

            }
            else
            {

                orphaned = checked(orphaned + 1);

            }

        }

        Result<CampaignPathFullInstallationResetCleanupReceipt> terminal =
            CampaignPathFullInstallationResetCleanupReceipt.CreateTerminal(
                prepared.OwnerOperationId,
                prepared.OwnerEffectDigest,
                prepared.OrderedMarkerIntentIds,
                prepared.MarkerIntentVectorDigest,
                deleted,
                orphaned);

        return terminal.IsSuccess
            ? terminal
            : Inert<CampaignPathFullInstallationResetCleanupReceipt>();

    }

    /// <summary>
    /// Terminalizes one prepared child, returning whether its marker was deleted.
    /// </summary>
    /// <remarks>
    /// The two blocked observation arms return before anything that could touch the workspace: no
    /// marker store, no opener, no codec, and no filesystem call at all. Their evidence was written
    /// while the installation was still intact and is the only thing this phase is entitled to act
    /// on — going back to look would be asking a workspace the reset has already changed to decide
    /// something the reset already decided.
    ///
    /// <para>Everything else is a compare-delete through the retained root and nothing else. A root
    /// this process no longer holds is a manual blocker rather than a reopen, because reopening one
    /// here would resolve a display path a second time, after the pair deletion removed the evidence
    /// that would have let anybody check the answer.</para>
    /// </remarks>
    private async Task<Result<bool>> TerminalizeOneCleanupChildAsync(
        CampaignPathFullInstallationResetCleanupReceipt prepared,
        CampaignPathFullResetCleanupChildRow child,
        FullInstallationResetMarkerCleanupAuthority authority,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        if (child.Evidence.ObservationCode
            is not CampaignPathFullResetCleanupObservationCode.Opened)
        {

            return await BlockCleanupChildAsync(
                prepared,
                child,
                authority,
                connection,
                cancellationToken).ConfigureAwait(false);

        }

        if (!_fullResetRetainedRoots.TryGetValue(
                new FullResetRetainedRootKey(prepared.OwnerOperationId, child.Intent.CampaignId),
                out CampaignPathMarkerRootAuthority? root)
            || root.CampaignId != child.Intent.CampaignId
            || root.PathRevision != child.Intent.PriorRevision
            || root.PhysicalIdentityDigest != child.Evidence.IndexedPhysicalIdentityDigest)
        {

            return await BlockCleanupChildAsync(
                prepared,
                child,
                authority,
                connection,
                cancellationToken).ConfigureAwait(false);

        }

        Result<PhysicalCampaignMarkerOpenResult> opened =
            await root.OpenMarkerOrProveAbsentNoFollowAsync(cancellationToken)
                .ConfigureAwait(false);

        if (opened.IsFailure)
        {

            return await BlockCleanupChildAsync(
                prepared,
                child,
                authority,
                connection,
                cancellationToken).ConfigureAwait(false);

        }

        // Absence is this operation's own completed delete, not a missing target: preparation proved
        // the marker present through this same retained root, and nothing else holds authority over
        // that directory. Treating it as a blocker would strand a Campaign that is already clean.
        if (opened.Value is PhysicalCampaignMarkerOpenResult.Absent)
        {

            return await CompleteCleanupChildAsync(
                prepared,
                child,
                authority,
                connection,
                cancellationToken).ConfigureAwait(false);

        }

        PhysicalCampaignRootOpener.MarkerHandleCapability marker =
            ((PhysicalCampaignMarkerOpenResult.Opened)opened.Value).Marker;

        await using (marker.ConfigureAwait(false))
        {

            Result<PhysicalCampaignRootOpener.MarkerCodecBytesLease> read =
                await marker.ReadAllBoundedAsync(
                    _codec.MaximumMarkerByteCount,
                    cancellationToken).ConfigureAwait(false);

            if (read.IsFailure)
            {

                return await BlockCleanupChildAsync(
                    prepared,
                    child,
                    authority,
                    connection,
                    cancellationToken).ConfigureAwait(false);

            }

            using PhysicalCampaignRootOpener.MarkerCodecBytesLease lease = read.Value;

            Result<MarkerOwnershipEvidence> proof = ProveOpenMarkerOwnership(
                root,
                marker,
                lease.Bytes.Span,
                child.Intent.CampaignId,
                child.Intent.PriorRevision);

            // The parent's own committed digest, compared before the companion's. Either comparison
            // alone catches a marker whose bytes changed — the byte digest is an input to the
            // ownership digest below — so this one earns its place only against a journal whose two
            // halves disagree, which is exactly the case the companion cannot report on itself.
            if (proof.IsFailure || proof.Value.MarkerDigest != child.Intent.MarkerDigest)
            {

                return await BlockCleanupChildAsync(
                    prepared,
                    child,
                    authority,
                    connection,
                    cancellationToken).ConfigureAwait(false);

            }

            Result<CovenantDigest> ownership =
                FullInstallationResetMarkerPairResetDigests.SameHandleOwnership(
                    child.Intent.CampaignId,
                    child.Intent.PriorRevision,
                    proof.Value.MarkerDigest,
                    child.Evidence.IndexedPhysicalIdentityDigest,
                    root.PhysicalIdentityDigest,
                    proof.Value.RootVolumeId,
                    proof.Value.RootFileId);

            if (ownership.IsFailure
                || ownership.Value != child.Evidence.SameHandleOwnershipEvidenceDigest)
            {

                return await BlockCleanupChildAsync(
                    prepared,
                    child,
                    authority,
                    connection,
                    cancellationToken).ConfigureAwait(false);

            }

            Result removed = await root.CompareDeleteMarkerAsync(
                marker,
                marker.PhysicalIdentityDigest,
                lease.Bytes,
                cancellationToken).ConfigureAwait(false);

            if (removed.IsFailure)
            {

                return await BlockCleanupChildAsync(
                    prepared,
                    child,
                    authority,
                    connection,
                    cancellationToken).ConfigureAwait(false);

            }

            // The parent directory barrier is part of the delete, not a follow-up to it. A child
            // marked completed over an unflushed rename would be evidence that the marker is gone
            // when a crash could still bring it back.
            Result flushed = await root.FlushMarkerDirectoryAsync(cancellationToken)
                .ConfigureAwait(false);

            if (flushed.IsFailure)
            {

                return await BlockCleanupChildAsync(
                    prepared,
                    child,
                    authority,
                    connection,
                    cancellationToken).ConfigureAwait(false);

            }

        }

        return await CompleteCleanupChildAsync(
            prepared,
            child,
            authority,
            connection,
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<Result<bool>> CompleteCleanupChildAsync(
        CampaignPathFullInstallationResetCleanupReceipt prepared,
        CampaignPathFullResetCleanupChildRow child,
        FullInstallationResetMarkerCleanupAuthority authority,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        Result advanced = await AdvanceCleanupChildAsync(
            prepared,
            child,
            CampaignPathMarkerPhase.Completed,
            authority,
            connection,
            cancellationToken).ConfigureAwait(false);

        return advanced.IsSuccess ? true : Inert<bool>();

    }

    private async Task<Result<bool>> BlockCleanupChildAsync(
        CampaignPathFullInstallationResetCleanupReceipt prepared,
        CampaignPathFullResetCleanupChildRow child,
        FullInstallationResetMarkerCleanupAuthority authority,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        Result advanced = await AdvanceCleanupChildAsync(
            prepared,
            child,
            CampaignPathMarkerPhase.ManualBlocker,
            authority,
            connection,
            cancellationToken).ConfigureAwait(false);

        return advanced.IsSuccess ? false : Inert<bool>();

    }

    /// <summary>
    /// Commits one child's single legal phase advance in its own short transaction.
    /// </summary>
    /// <remarks>
    /// Kind four has exactly one transition — prepared to completed or to a manual blocker — so there
    /// is no ladder to walk and nothing to observe on the way. The advance carries a null observation
    /// and a null pending disposition because both are authority this kind never received, and the
    /// compare-and-swap is on the phase revision this attempt actually read.
    /// </remarks>
    private async Task<Result> AdvanceCleanupChildAsync(
        CampaignPathFullInstallationResetCleanupReceipt prepared,
        CampaignPathFullResetCleanupChildRow child,
        CampaignPathMarkerPhase phase,
        FullInstallationResetMarkerCleanupAuthority authority,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        Result revalidated = await authority.RevalidateReceiptAsync(
            prepared,
            cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return Inert();

        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

        CampaignPathMarkerIntentStore intents = new(
            _initializer,
            connection,
            transaction,
            _timeProvider);

        Result advanced = await intents.AdvancePhaseAsync(
            child.Intent.IntentId,
            child.Intent.PhaseRevision,
            phase,
            observation: null,
            pendingDisposition: null,
            cancellationToken).ConfigureAwait(false);

        if (advanced.IsFailure)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return Inert();

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();

    }

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

    /// <summary>
    /// One decided observation, complete enough to write a child from and holding no capability.
    /// </summary>
    /// <remarks>
    /// Every seed is built before the first row is written, so the vector describes one moment
    /// rather than a walk through several. <see cref="RequiredRootIdentityKey"/> records whether
    /// reaching this arm needed the root identity key, which is what lets a later Campaign reuse the
    /// proof instead of asking the credential store again per Campaign.
    /// </remarks>
    private sealed record FullResetCleanupSeed(
        CampaignMarkerInventoryEntryV1 Entry,
        CovenantDigest InventoryEntryDigest,
        CampaignPathFullResetCleanupObservationCode ObservationCode,
        string? TargetDisplayPath,
        CovenantDigest? OpenedSameHandleOwnershipEvidenceDigest,
        CovenantDigest ObservationDigest,
        bool RequiredRootIdentityKey);

}
