using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The one arm that discovers which destination Campaign roots a restore has to clean.
/// </summary>
/// <remarks>
/// It lives beside the restart proof rather than in the retained-root half for the same reason that
/// one does: it resolves a display path, and the in-process file has to stay incapable of that. What
/// it resolves are the destination's <em>own</em> registrations — rows this installation wrote when it
/// opened those directories — so the resolution is a re-open of something this machine already proved,
/// not the adoption of a name from an archive.
///
/// <para>The open is the authority, never the row. Every candidate is opened through the ordinary
/// producer, which re-derives the identity from the handle it actually obtained; a directory that was
/// swapped underneath the recorded path answers with an identity the registry never indexed and
/// becomes a blocked seed rather than an opened one. Preparation then refuses the whole restore,
/// because displacing an installation while leaving a marker that claims a Campaign the restored
/// installation does not own is exactly the residue this phase exists to prevent (§10.19.9).</para>
/// </remarks>
internal sealed partial class CampaignPathMarkerLifecycle
{

    /// <summary>
    /// The domain the content-free observation digest of an unopenable root is computed under.
    /// </summary>
    /// <remarks>
    /// A blocked arm still has to carry <em>some</em> observation, and it must not be a path, a handle,
    /// or a reconstructible identity. Hashing the closed blocker code together with the entry the
    /// registry already holds gives an operator something to correlate two runs by and gives a caller
    /// nothing it could reopen.
    /// </remarks>
    private const string BlockedObservationDomain =
        "Arcanum.Covenant.BackupRestore.BlockedRootObservation.v1";

    /// <inheritdoc />
    public async Task<Result<CampaignPathRestoreCleanupInventory>> InventoryRestoreCleanupAsync(
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken)
    {

        Result ownerValid = ValidateRestoreOwner(owner);

        if (ownerValid.IsFailure)
        {

            return ownerValid.Error;

        }

        SqliteConnection connection =
            await _liveConnections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        Result<List<RegisteredRoot>> registered =
            await ReadRegisteredRootsAsync(connection, cancellationToken).ConfigureAwait(false);

        if (registered.IsFailure)
        {

            return registered.Error;

        }

        if (registered.Value.Count > CampaignPathRestoreCleanupIntentVector.MaximumIntents)
        {

            return new Error(
                ErrorCodes.Covenant.CapacityExceeded,
                "This destination holds more registered Campaign roots than one authenticated restore "
                + "journal may carry.");

        }

        ImmutableArray<CampaignPathRestoreCleanupSeed>.Builder seeds =
            ImmutableArray.CreateBuilder<CampaignPathRestoreCleanupSeed>(registered.Value.Count);

        foreach (RegisteredRoot root in registered.Value)
        {

            seeds.Add(await ObserveRegisteredRootAsync(root, cancellationToken).ConfigureAwait(false));

        }

        return new CampaignPathRestoreCleanupInventory(seeds.ToImmutable());

    }

    /// <summary>
    /// Opens one registered root, or records exactly why it could not be opened.
    /// </summary>
    private async Task<CampaignPathRestoreCleanupSeed> ObserveRegisteredRootAsync(
        RegisteredRoot root,
        CancellationToken cancellationToken)
    {

        // One no-follow resolution, answering "what is there" and never "may this be worked on".
        if (_rootOpener.IdentifyExact(root.DisplayPath) is not { } identity)
        {

            return Blocked(root, CampaignPathCleanupRootBlocker.RootUnavailable);

        }

        Result<CampaignPathMarkerRootAuthority> opened =
            await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
                _rootOpener,
                root.CampaignId,
                root.Revision,
                identity,
                root.DisplayPath,
                cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure)
        {

            return Blocked(root, CampaignPathCleanupRootBlocker.RootUnavailable);

        }

        // The registry indexed one physical identity for this Campaign. A directory now answering to a
        // different one is somebody else's, whatever the name still says.
        if (opened.Value.PhysicalIdentityDigest != root.IndexedIdentityDigest)
        {

            await opened.Value.DisposeAsync().ConfigureAwait(false);

            return Blocked(root, CampaignPathCleanupRootBlocker.PhysicalIdentityMismatch);

        }

        return new CampaignPathRestoreCleanupSeed(
            root.CampaignId,
            root.Revision,
            root.IndexedIdentityDigest,
            root.DisplayPath,
            new CampaignPathCleanupRootObservation.Opened(opened.Value));

    }

    private static CampaignPathRestoreCleanupSeed Blocked(
        RegisteredRoot root,
        CampaignPathCleanupRootBlocker blocker)
    {

        CampaignPathCleanupRootBlockerEvidence evidence = new(
            blocker,
            root.IndexedIdentityDigest,
            ObservationDigest(root, blocker));

        return new CampaignPathRestoreCleanupSeed(
            root.CampaignId,
            root.Revision,
            root.IndexedIdentityDigest,
            root.DisplayPath,
            blocker is CampaignPathCleanupRootBlocker.RootUnavailable
                ? new CampaignPathCleanupRootObservation.Unavailable(evidence)
                : new CampaignPathCleanupRootObservation.Mismatch(evidence));

    }

    private static CovenantDigest ObservationDigest(
        RegisteredRoot root,
        CampaignPathCleanupRootBlocker blocker)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(BlockedObservationDomain));

        hash.AppendData([0x00, (byte)blocker]);

        Span<byte> campaign = stackalloc byte[16];

        _ = root.CampaignId.TryWriteBytes(campaign, bigEndian: true, out _);

        hash.AppendData(campaign);

        hash.AppendData(root.IndexedIdentityDigest.Bytes.AsSpan());

        return new CovenantDigest(hash.GetHashAndReset());

    }

    /// <summary>
    /// Reads every Campaign this installation has a resolved root for, in canonical Campaign order.
    /// </summary>
    /// <remarks>
    /// Ordered by the Campaign identity rather than by whatever order SQLite returned, because the
    /// vector this inventory eventually produces is compared byte-for-byte after a restart and two
    /// runs over the same registry have to agree.
    /// </remarks>
    private static async Task<Result<List<RegisteredRoot>>> ReadRegisteredRootsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        List<RegisteredRoot> roots = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT CampaignId, Revision, DisplayPath, PhysicalIdentityDigest
            FROM campaign_path_identities
            ORDER BY CampaignId;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (!Guid.TryParse(reader.GetString(0), out Guid campaignId)
                || campaignId == Guid.Empty)
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A registered Campaign root names no usable Campaign identity.");

            }

            long revision = reader.GetInt64(1);

            byte[] digest = ReadDigest(reader, 3);

            if (revision <= 0 || digest.Length != CovenantLimits.DigestBytes)
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A registered Campaign root carries no usable revision or identity evidence.");

            }

            roots.Add(
                new RegisteredRoot(
                    campaignId,
                    revision,
                    reader.GetString(2),
                    new CovenantDigest(digest)));

        }

        return roots;

    }

    private static byte[] ReadDigest(SqliteDataReader reader, int ordinal)
    {

        if (reader.IsDBNull(ordinal))
        {

            return [];

        }

        using Stream stream = reader.GetStream(ordinal);

        using MemoryStream buffer = new();

        stream.CopyTo(buffer);

        return buffer.ToArray();

    }

    /// <summary>One row of this installation's own Campaign root registry.</summary>
    private sealed record RegisteredRoot(
        Guid CampaignId,
        long Revision,
        string DisplayPath,
        CovenantDigest IndexedIdentityDigest);

}
