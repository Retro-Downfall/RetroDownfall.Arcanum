using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>Full-history compression inputs proved clean in the same Core snapshot that reads their text.</summary>
internal sealed class SessionSummaryMaintenanceStore(
    ICovenantConnectionSource connections,
    ICovenantSqliteConnectionInitializer initializer) : ISessionSummaryMaintenanceStore
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<Result<SessionSummaryMaintenanceInput?>> PrepareAsync(
        Guid sessionId, int entryLimit, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty || entryLimit is < 1 or > 5000)
        {
            return new Error(ErrorCodes.Validation.InvalidFields, "A clean summary snapshot requires a Session and bounded positive entry limit.");
        }

        SqliteConnection connection = await connections.GetOpenCoreConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

        Result<SessionSummaryMaintenanceInput?> prepared = await PrepareWithinAsync(
            connection, transaction, sessionId, entryLimit, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return prepared;
    }

    public async Task<Result> PublishAsync(
        SessionSummaryMaintenanceInput input, string summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(summary))
        {
            return new Error(ErrorCodes.Validation.InvalidFields, "The ordinary Session summary must contain text.");
        }

        SqliteConnection connection = await connections.GetOpenCoreConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Result<SessionSummaryMaintenanceInput?> current = await PrepareWithinAsync(
                connection, transaction, input.SessionId, input.EntryLimit, cancellationToken).ConfigureAwait(false);

            if (current.IsFailure || current.Value is not { } exact
                || exact.SourceDigest != input.SourceDigest || exact.HistoryRevision != input.HistoryRevision
                || exact.SensitivityRevision != input.SensitivityRevision)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return current.IsFailure ? Result.Failure(current.Error)
                    : Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "The clean Session summary sources changed before publication."));
            }

            DateTimeOffset through = exact.Entries[^1].CreatedAt;

            SessionDerivedArtifactStore artifacts = new(connections, initializer);

            Result<SessionDerivedArtifactWriteReceipt> saved = await artifacts.ReplaceSummaryWithinAsync(
                connection, transaction,
                new SessionSummaryArtifactWrite(input.SessionId, summary, through,
                    ContentSensitivity.None, GenerationProvenance.CreateExact([])),
                cancellationToken).ConfigureAwait(false);

            if (saved.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return Result.Failure(saved.Error);
            }

            await using SqliteCommand remaining = Command(connection, transaction, """
                UPDATE Sessions SET UnsummarizedEntryCount =
                    (SELECT COUNT(*) FROM Entries WHERE SessionId = $session AND CreatedAt > $through)
                WHERE Id = $session;
                """, input.SessionId);

            _ = remaining.Parameters.AddWithValue("$through", UtcInstantText.Format(through));

            _ = await remaining.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<SessionSummaryMaintenanceInput?>> PrepareWithinAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid sessionId, int entryLimit,
        CancellationToken cancellationToken)
    {
        if (entryLimit is < 1 or > 5000)
        {
            return new Error(ErrorCodes.Validation.InvalidFields, "The ordinary summary page limit is outside its bounded range.");
        }

        // This decision touches identities and sensitivity metadata only. SQLite holds this read
        // snapshot through the subsequent text SELECTs, so an Entry and its atomic label cannot
        // appear halfway through the read under a clean decision from an earlier snapshot.
        long sensitivityRevision;

        await using (SqliteCommand proof = Command(connection, transaction, """
            SELECT COALESCE((SELECT Revision FROM session_sensitivity_state WHERE SessionId = $session), 0),
                EXISTS(SELECT 1 FROM session_sensitivity_state WHERE SessionId = $session
                    AND (TaintedArtifactCount > 0 OR MaximumSensitivityCode <> 0))
                OR EXISTS(SELECT 1 FROM artifact_sensitivity WHERE SessionId = $session)
                OR EXISTS(SELECT 1 FROM artifact_sensitivity a JOIN Entries e ON e.Id = a.ArtifactId
                    WHERE e.SessionId = $session)
                OR EXISTS(SELECT 1 FROM session_summary_artifacts WHERE SessionId = $session AND SensitivityCode <> 0);
            """, sessionId))
        {
            await using SqliteDataReader reader = await proof.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            sensitivityRevision = reader.GetInt64(0);

            if (reader.GetInt64(1) != 0)
            {
                return new Error(ErrorCodes.Covenant.ForbiddenAuthority, "Protected Session compression requires an authenticated request claim.");
            }
        }

        byte[]? priorDigest = null;

        long summaryRevision = 0;

        await using (SqliteCommand metadata = Command(connection, transaction, """
            SELECT a.ContentDigest, a.Revision FROM session_summary_state s
            JOIN session_summary_artifacts a ON a.ArtifactId = s.CurrentArtifactId
                AND a.SessionId = s.SessionId AND a.Revision = s.Revision
            WHERE s.SessionId = $session;
            """, sessionId))
        {
            await using SqliteDataReader reader = await metadata.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                priorDigest = reader.GetFieldValue<byte[]>(0);

                summaryRevision = reader.GetInt64(1);
            }
        }

        string? previous;

        DateTimeOffset? watermark;

        await using (SqliteCommand header = Command(connection, transaction,
            "SELECT Summary, LastSummarizedMessageAt FROM Sessions WHERE Id = $session;", sessionId))
        {
            await using SqliteDataReader reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return Result<SessionSummaryMaintenanceInput?>.Success(null);
            }

            previous = reader.IsDBNull(0) ? null : reader.GetString(0);

            watermark = reader.IsDBNull(1) ? null : UtcInstantText.Parse(reader.GetString(1));
        }

        if (priorDigest is not null
            && DerivedArtifactContentDigest.ForText(previous ?? string.Empty) != new CovenantDigest(priorDigest))
        {
            return new Error(ErrorCodes.Covenant.IntegrityFailure, "The retained Session summary does not match its immutable artifact.");
        }

        long history;

        await using (SqliteCommand maximum = Command(connection, transaction,
            "SELECT COALESCE(MAX(Sequence), 0) FROM Entries WHERE SessionId = $session;", sessionId))
        {
            history = Convert.ToInt64(await maximum.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        List<SessionSummaryMaintenanceEntry> entries = [];

        int bytes = previous is null ? 0 : Utf8.GetByteCount(previous);

        await using (SqliteCommand page = Command(connection, transaction, """
            SELECT Id, Sequence, Role, Content, CreatedAt FROM Entries
            WHERE SessionId = $session AND ($watermark IS NULL OR CreatedAt > $watermark)
            ORDER BY CreatedAt, Sequence, Id LIMIT $limit;
            """, sessionId))
        {
            _ = page.Parameters.AddWithValue("$watermark", watermark is { } prior ? UtcInstantText.Format(prior) : DBNull.Value);

            _ = page.Parameters.AddWithValue("$limit", entryLimit + 1);

            await using SqliteDataReader reader = await page.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string text = reader.GetString(3);

                entries.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetInt32(2),
                    text, UtcInstantText.Parse(reader.GetString(4))));
            }
        }

        // The legacy watermark is a timestamp. Keep a whole boundary timestamp group or defer it;
        // cutting through equal timestamps would make its remaining Entries disappear on retry.
        if (entries.Count > entryLimit)
        {
            DateTimeOffset excludedAt = entries[^1].CreatedAt;

            entries.RemoveAt(entries.Count - 1);

            if (entries.Count > 0 && entries[^1].CreatedAt == excludedAt)
            {
                entries.RemoveAll(entry => entry.CreatedAt == excludedAt);
            }

            if (entries.Count == 0)
            {
                return new Error(ErrorCodes.Covenant.Unavailable, "One Session timestamp group exceeds the bounded ordinary summary page.");
            }
        }

        if (entries.Count == 0)
        {
            return Result<SessionSummaryMaintenanceInput?>.Success(null);
        }

        foreach (SessionSummaryMaintenanceEntry entry in entries)
        {
            bytes = checked(bytes + Utf8.GetByteCount(entry.Content));

            if (bytes > CampaignRollupLimits.InputPageUtf8Bytes)
            {
                return new Error(ErrorCodes.Covenant.Unavailable, "The ordinary summary input exceeds its bounded page.");
            }
        }

        ImmutableArray<SessionSummaryMaintenanceEntry> frozen = [.. entries];

        CovenantDigest digest = SourceDigest(sessionId, previous, watermark, frozen, history, sensitivityRevision, summaryRevision);

        return Result<SessionSummaryMaintenanceInput?>.Success(new(
            sessionId, previous, watermark, frozen, history, sensitivityRevision, entryLimit, digest));
    }

    private static CovenantDigest SourceDigest(Guid sessionId, string? summary, DateTimeOffset? watermark,
        ImmutableArray<SessionSummaryMaintenanceEntry> entries, long history, long sensitivity, long summaryRevision)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData("Arcanum.SessionSummary.CleanSources.v1\0"u8);

        hash.AppendData(sessionId.ToByteArray(bigEndian: true));

        void Number(long value)
        {
            Span<byte> bytes = stackalloc byte[8];

            BinaryPrimitives.WriteInt64BigEndian(bytes, value);

            hash.AppendData(bytes);
        }

        void Text(string? value)
        {
            Number(value is null ? -1 : Utf8.GetByteCount(value));

            if (value is not null)
            {
                hash.AppendData(Utf8.GetBytes(value));
            }
        }

        Number(history);

        Number(sensitivity);

        Number(summaryRevision);

        Text(summary);

        Text(UtcInstantText.Format(watermark));

        foreach (SessionSummaryMaintenanceEntry entry in entries)
        {
            hash.AppendData(entry.EntryId.ToByteArray(bigEndian: true));

            Number(entry.Sequence);

            Number(entry.Role);

            Text(UtcInstantText.Format(entry.CreatedAt));

            Text(entry.Content);
        }

        return new(hash.GetHashAndReset());
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, Guid sessionId)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        _ = command.Parameters.AddWithValue("$session", sessionId.ToString("D").ToUpperInvariant());

        return command;
    }
}
