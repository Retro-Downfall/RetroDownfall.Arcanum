using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// Derives one selective-import request from the archive it will actually read.
/// </summary>
/// <remarks>
/// Every digest and count here comes from the pinned source snapshot rather than from the operator's
/// request. The store recomputes all of them before it writes anything, so this is a claim the store
/// checks — never an instruction it follows — and an omitted row or attachment shows up as a manifest
/// mismatch rather than as a partial graph that committed (§10.13).
/// </remarks>
internal static class BackupSessionImportPlanner
{

    /// <summary>
    /// Refuses the whole selection if any chosen Session is Campaign-bound with no mapping.
    /// </summary>
    /// <remarks>
    /// Run once over the entire selection before the first commit, because the per-Session plan below
    /// cannot refuse early enough to matter: each Session commits under its own compound lease, so a
    /// selection whose third Session is unmapped would otherwise land the first two and then report the
    /// whole import refused — and the operator's obvious retry, with the missing mapping added, would
    /// import those two a second time under fresh destination identities.
    ///
    /// <para>Read-only, and over the same pinned snapshot the per-Session plans read, so a selection
    /// that passes here fails no differently than one that was fully mapped to begin with.</para>
    /// </remarks>
    internal static async Task<Result> ValidateCampaignCoverageAsync(
        SqliteConnection source,
        IReadOnlyList<Guid> sessionIds,
        IReadOnlyList<BackupSessionCampaignMapping> campaignMappings,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "Sessions", cancellationToken)
                .ConfigureAwait(false))
        {

            // Left to the per-Session plan, which already names this and is the one place that decides
            // whether the snapshot is readable at all.
            return Result.Success();

        }

        foreach (Guid sessionId in sessionIds)
        {

            Guid? bound = await ReadSourceCampaignAsync(source, sessionId, cancellationToken)
                .ConfigureAwait(false);

            if (bound is { } campaignId
                && !campaignMappings.Any(candidate => candidate.SourceCampaignId == campaignId))
            {

                return UnmappedCampaignBinding(sessionId, campaignId);

            }

        }

        return Result.Success();

    }

    /// <summary>
    /// The one refusal both the coverage pass and the per-Session plan report.
    /// </summary>
    /// <remarks>
    /// Names the archived Campaign and the option that supplies it. A refusal naming only the Session
    /// would leave the operator to work out which of the archive's Campaigns it was bound to before they
    /// could write the mapping that fixes it.
    /// </remarks>
    private static Error UnmappedCampaignBinding(Guid sourceSessionId, Guid campaignId) =>
        new(
            ErrorCodes.Covenant.CampaignBindingConflict,
            $"Session {sourceSessionId:D} is bound to archived Campaign {campaignId:D} and needs an "
            + "explicit destination Campaign mapping before it can be imported. Re-run with "
            + $"--map-campaign {campaignId:D}=<destination-campaign-id>.");

    internal static async Task<Result<ImportedSessionTransferRequest>> PlanAsync(
        ImportedSessionSourceLease sourceLease,
        Guid sourceSessionId,
        IReadOnlyList<BackupSessionCampaignMapping> campaignMappings,
        CancellationToken cancellationToken)
    {

        SqliteConnection source = sourceLease.Snapshot;

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "Sessions", cancellationToken)
                .ConfigureAwait(false))
        {

            return new Error(
                ErrorCodes.Covenant.NotFound,
                "The archived Grimoire snapshot has no Sessions table.");

        }

        Guid? sourceCampaignId = await ReadSourceCampaignAsync(source, sourceSessionId, cancellationToken)
            .ConfigureAwait(false);

        if (sourceCampaignId is null
            && !await SessionExistsAsync(source, sourceSessionId, cancellationToken).ConfigureAwait(false))
        {

            return new Error(
                ErrorCodes.Covenant.NotFound,
                $"The archive does not contain Session {sourceSessionId:D}.");

        }

        BackupSessionCampaignMapping? mapping = null;

        if (sourceCampaignId is { } bound)
        {

            // Explicit or nothing. Dropping the binding would produce an unbound Session whose
            // standing Campaign instructions silently stop applying to it, and guessing a destination
            // would attach it to a Campaign the operator never chose.
            mapping = campaignMappings.FirstOrDefault(
                candidate => candidate.SourceCampaignId == bound);

            if (mapping is null)
            {

                return UnmappedCampaignBinding(sourceSessionId, bound);

            }

        }

        ImmutableArray<byte[]> manifestItems = await ReadManifestItemsAsync(
            source,
            sourceSessionId,
            cancellationToken).ConfigureAwait(false);

        ProtectedSessionTransferCounts counts = await ReadCountsAsync(
            source,
            sourceSessionId,
            cancellationToken).ConfigureAwait(false);

        CovenantDigest manifest = ProtectedSessionTransferDigests.Manifest(manifestItems);

        CovenantDigest sourceEvidence = ComputeSourceEvidence(sourceSessionId, manifest, counts);

        CovenantDigest binding = ProtectedSessionTransferDigests.DestinationBinding(
            mapping is null ? CovenantScope.Global : CovenantScope.Campaign,
            mapping?.DestinationCampaignId,
            mapping?.SourceCampaignId);

        Guid operationId = Guid.NewGuid();

        Guid destinationSessionId = Guid.NewGuid();

        Result<CovenantDigest> effect = ProtectedSessionTransferDigests.Effect(
            ProtectedSessionTransferKind.Import,
            operationId,
            sourceSessionId,
            cutoffEntryId: null,
            destinationSessionId,
            binding,
            sourceEvidence,
            manifest,
            counts);

        return effect.IsFailure
            ? effect.Error
            : new ImportedSessionTransferRequest(
                operationId,
                sourceSessionId,
                destinationSessionId,
                sourceEvidence,
                manifest,
                counts,
                mapping,
                binding,
                effect.Value);

    }

    /// <summary>
    /// A content-free commitment to which archived Session this import copied from.
    /// </summary>
    /// <remarks>
    /// Every imported finalization guard carries this digest, and that is what marks the row as
    /// non-replayable. Without it an imported turn would be indistinguishable from one this
    /// installation actually ran.
    /// </remarks>
    private static CovenantDigest ComputeSourceEvidence(
        Guid sourceSessionId,
        CovenantDigest manifestDigest,
        ProtectedSessionTransferCounts counts) =>
        new(SHA256.HashData(
        [
            .. Encoding.ASCII.GetBytes("Arcanum.Covenant.ProtectedSessionTransfer.SourceEvidence.v1"),
            0x00,
            .. sourceSessionId.ToByteArray(bigEndian: true),
            .. manifestDigest.Bytes,
            .. counts.ComputeVectorDigest().Value.Bytes,
        ]));

    /// <summary>
    /// The eight comparisons that decide what this plan claims about the archive, all keyed by the
    /// Session under import.
    /// </summary>
    /// <remarks>
    /// Every one of them bound <c>ToString("D")</c> — lowercase — and three of the four columns they
    /// bind it against hold an uppercase spelling. <c>"Sessions"."Id"</c> and
    /// <c>"Entries"."SessionId"</c> come from a <see cref="Guid"/> property the object-relational
    /// writer stores as uppercase dashed TEXT, and <c>assistant_entry_finalizations.SessionId</c> is
    /// bound as a <see cref="Guid"/> by the turn-commit writer, which the SQLite provider renders the
    /// same way. SQLite's BINARY collation makes those different strings from the lowercase form. So
    /// this planner refused a selective protected import of a genuine backup with "The archive does
    /// not contain Session {id}" before the transfer store beneath it was ever reached — and where a
    /// Session did resolve, the manifest and the counts it committed to were assembled from an empty
    /// graph.
    ///
    /// <para><c>"SessionAttachments"."SessionId"</c> is the fourth, and it is the one that was
    /// matching. Its three writers — the attachment store, the protected transfer store and the
    /// unprotected merge path — each render the identity with <c>ToString()</c>, so the column holds
    /// one spelling today and a lowercase comparison found its rows. It is normalised with the rest
    /// anyway, because that agreement is three independent renderings that happen to coincide rather
    /// than anything a reader of this file could verify, and because the count is generated by a
    /// helper shared with <c>"Entries"."SessionId"</c>, which is not matching. Uniform here is
    /// cheaper to read than a split that invites the next reader to unify it the wrong way.</para>
    ///
    /// <para>All eight now compare through the shared shape, which reduces every stored spelling to
    /// one and binds the parameter already reduced.</para>
    ///
    /// <para><b>The cost.</b> These scan the archive rather than seeking. They run once per Session
    /// planned, against a read-only snapshot opened for that import, and the import they are planning
    /// copies every row they count — so the scan is a fraction of the work the operation was always
    /// going to do.</para>
    /// </remarks>
    private static async Task<bool> SessionExistsAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = source.CreateCommand();

        command.CommandText =
            $"SELECT 1 FROM \"Sessions\" WHERE {CovenantIdentitySql.Keyed("\"Id\"", "$sessionKey")} LIMIT 1;";

        _ = command.Parameters.AddWithValue("$sessionKey", CovenantIdentitySql.Key(sessionId));

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;

    }

    private static async Task<Guid?> ReadSourceCampaignAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = source.CreateCommand();

        // Normalised for the reason SessionExistsAsync states. This one decides whether the whole
        // selection needs a Campaign mapping, so an unmatched row here reports a Campaign-bound
        // Session as unbound rather than merely missing.
        command.CommandText =
            $"SELECT \"CampaignId\" FROM \"Sessions\" WHERE {CovenantIdentitySql.Keyed("\"Id\"", "$sessionKey")};";

        _ = command.Parameters.AddWithValue("$sessionKey", CovenantIdentitySql.Key(sessionId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? null : Guid.Parse((string)value);

    }

    private static async Task<ProtectedSessionTransferCounts> ReadCountsAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        long entries = await CountAsync(source, "Entries", "SessionId", sessionId, cancellationToken)
            .ConfigureAwait(false);

        long attachments = await CountAsync(
            source,
            "SessionAttachments",
            "SessionId",
            sessionId,
            cancellationToken).ConfigureAwait(false);

        long blobs = await CountAttachmentBlobsAsync(source, sessionId, cancellationToken)
            .ConfigureAwait(false);

        long finalizations = await CountFinalizationsAsync(source, sessionId, cancellationToken)
            .ConfigureAwait(false);

        return new ProtectedSessionTransferCounts(
            1,
            checked((ulong)entries),
            checked((ulong)attachments),
            checked((ulong)blobs),
            checked((ulong)finalizations),
            0);

    }

    /// <summary>
    /// Rebuilds the exact ordered preimages the transfer store commits to.
    /// </summary>
    private static async Task<ImmutableArray<byte[]>> ReadManifestItemsAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        ImmutableArray<byte[]>.Builder items = ImmutableArray.CreateBuilder<byte[]>();

        if (await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "Entries", cancellationToken)
                .ConfigureAwait(false))
        {

            await using SqliteCommand command = source.CreateCommand();

            command.CommandText = $"""
                SELECT "Id", "Sequence" FROM "Entries"
                WHERE {CovenantIdentitySql.Keyed("\"SessionId\"", "$sessionKey")}
                ORDER BY "Sequence", "Id";
                """;

            _ = command.Parameters.AddWithValue("$sessionKey", CovenantIdentitySql.Key(sessionId));

            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                items.Add(Encoding.UTF8.GetBytes(
                    $"entry:{reader.GetString(0)}:{reader.GetValue(1)}"));

            }

        }

        if (await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "SessionAttachments", cancellationToken)
                .ConfigureAwait(false))
        {

            await using SqliteCommand command = source.CreateCommand();

            command.CommandText = $"""
                SELECT "RelativePath", "ContentSha256", "ByteLength" FROM "SessionAttachments"
                WHERE {CovenantIdentitySql.Keyed("\"SessionId\"", "$sessionKey")}
                ORDER BY "Id";
                """;

            _ = command.Parameters.AddWithValue("$sessionKey", CovenantIdentitySql.Key(sessionId));

            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                items.Add(Encoding.UTF8.GetBytes(
                    $"attachment:{Text(reader, 0)}:{Hex(reader, 1)}:{Number(reader, 2)}"));

            }

        }

        if (await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "assistant_entry_finalizations", cancellationToken)
                .ConfigureAwait(false))
        {

            await using SqliteCommand command = source.CreateCommand();

            command.CommandText = $"""
                SELECT AssistantEntryId FROM assistant_entry_finalizations
                WHERE {CovenantIdentitySql.Keyed("SessionId", "$sessionKey")} AND OutcomeCode = 1
                ORDER BY AssistantEntryId;
                """;

            _ = command.Parameters.AddWithValue("$sessionKey", CovenantIdentitySql.Key(sessionId));

            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                items.Add(Encoding.UTF8.GetBytes($"finalization:{reader.GetString(0)}"));

            }

        }

        return items.ToImmutable();

    }

    private static string Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

    private static string Hex(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? Convert.ToHexString(new byte[32])
            : reader.GetValue(ordinal) switch
            {
                byte[] bytes => Convert.ToHexString(bytes),
                string hex when hex.Length == 64 => Convert.ToHexString(Convert.FromHexString(hex)),
                _ => Convert.ToHexString(new byte[32]),
            };

    private static long Number(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);

    private static async Task<long> CountAsync(
        SqliteConnection source,
        string table,
        string column,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, table, cancellationToken)
                .ConfigureAwait(false))
        {

            return 0;

        }

        await using SqliteCommand command = source.CreateCommand();

        // The column arrives as a name rather than a literal, so the shape is composed around it.
        // Both columns this is called with hold a Session identity somebody else's writer spelled, and
        // one of the two — "Entries"."SessionId" — spells it in a form this lowercase binding missed.
        command.CommandText =
            $"SELECT COUNT(*) FROM \"{table}\" WHERE {CovenantIdentitySql.Keyed($"\"{column}\"", "$sessionKey")};";

        _ = command.Parameters.AddWithValue("$sessionKey", CovenantIdentitySql.Key(sessionId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async Task<long> CountAttachmentBlobsAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "SessionAttachments", cancellationToken)
                .ConfigureAwait(false))
        {

            return 0;

        }

        await using SqliteCommand command = source.CreateCommand();

        command.CommandText = $"""
            SELECT COUNT(*) FROM "SessionAttachments"
            WHERE {CovenantIdentitySql.Keyed("\"SessionId\"", "$sessionKey")}
                  AND "RelativePath" IS NOT NULL AND length("RelativePath") > 0;
            """;

        _ = command.Parameters.AddWithValue("$sessionKey", CovenantIdentitySql.Key(sessionId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async Task<long> CountFinalizationsAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(source, "assistant_entry_finalizations", cancellationToken)
                .ConfigureAwait(false))
        {

            return 0;

        }

        await using SqliteCommand command = source.CreateCommand();

        command.CommandText = $"""
            SELECT COUNT(*) FROM assistant_entry_finalizations
            WHERE {CovenantIdentitySql.Keyed("SessionId", "$sessionKey")} AND OutcomeCode = 1;
            """;

        _ = command.Parameters.AddWithValue("$sessionKey", CovenantIdentitySql.Key(sessionId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

}
