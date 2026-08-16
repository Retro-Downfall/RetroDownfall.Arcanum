using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
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

                return new Error(
                    ErrorCodes.Covenant.CampaignBindingConflict,
                    $"Session {sourceSessionId:D} is bound to an archived Campaign and needs an explicit "
                    + "destination Campaign mapping before it can be imported.");

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

    private static async Task<bool> SessionExistsAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = source.CreateCommand();

        command.CommandText = "SELECT 1 FROM \"Sessions\" WHERE \"Id\" = $id LIMIT 1;";

        _ = command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;

    }

    private static async Task<Guid?> ReadSourceCampaignAsync(
        SqliteConnection source,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = source.CreateCommand();

        command.CommandText = "SELECT \"CampaignId\" FROM \"Sessions\" WHERE \"Id\" = $id;";

        _ = command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

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

            command.CommandText = """
                SELECT "Id", "Sequence" FROM "Entries"
                WHERE "SessionId" = $id ORDER BY "Sequence", "Id";
                """;

            _ = command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

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

            command.CommandText = """
                SELECT "RelativePath", "ContentSha256", "ByteLength" FROM "SessionAttachments"
                WHERE "SessionId" = $id ORDER BY "Id";
                """;

            _ = command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

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

            command.CommandText = """
                SELECT AssistantEntryId FROM assistant_entry_finalizations
                WHERE SessionId = $id AND OutcomeCode = 1 ORDER BY AssistantEntryId;
                """;

            _ = command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

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

        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" = $id;";

        _ = command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

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

        command.CommandText = """
            SELECT COUNT(*) FROM "SessionAttachments"
            WHERE "SessionId" = $id AND "RelativePath" IS NOT NULL AND length("RelativePath") > 0;
            """;

        _ = command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

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

        command.CommandText = """
            SELECT COUNT(*) FROM assistant_entry_finalizations
            WHERE SessionId = $id AND OutcomeCode = 1;
            """;

        _ = command.Parameters.AddWithValue("$id", sessionId.ToString("D"));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

}
