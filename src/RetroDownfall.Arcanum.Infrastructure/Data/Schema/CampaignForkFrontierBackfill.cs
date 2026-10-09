using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>Proves an inherited copied prefix by stable Entry identity, or excludes all legacy history.</summary>
internal sealed class CampaignForkFrontierBackfill : IGrimoireSchemaBackfill
{
    public string Name => "campaign-fork-native-frontiers";

    public int MaxRowsPerBatch => 200;

    public async Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? cursor,
        CancellationToken cancellationToken)
    {
        Position? position = Parse(cursor);

        PendingFork? fork = await ReadForkAsync(connection, transaction, position?.SessionId, cancellationToken)
            .ConfigureAwait(false);

        if (fork is null)
        {
            // Owner deletion can legitimately drain the Session a prior bounded page named.
            return new GrimoireSchemaBackfillBatch(null, 0, position is null);
        }

        if (position is not null && !string.Equals(position.SourceSessionId, fork.SourceSessionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A legacy fork's source identity changed during frontier classification.");
        }

        long maximum = position?.MaximumSequence ?? fork.MaximumSequence;

        long lastExamined = position?.LastExaminedSequence ?? 0;

        long lastCopied = position?.LastCopiedSequence ?? 0;

        bool reachedNative = position?.ReachedNative ?? false;

        if (!fork.SourceExists || !Guid.TryParseExact(fork.SessionId, "D", out Guid child))
        {
            await PublishAsync(connection, transaction, fork, fork.MaximumSequence, 2, cancellationToken).ConfigureAwait(false);

            return new GrimoireSchemaBackfillBatch(null, 1, false);
        }

        bool unprovable = false;

        int examined = 0;

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;

            command.CommandText =
                """
                SELECT e.Sequence, e.Id, source.Id
                FROM "Entries" e
                LEFT JOIN "Entries" source ON source.SessionId = $source AND source.Sequence = e.Sequence
                WHERE e.SessionId = $session AND e.Sequence > $after AND e.Sequence <= $maximum
                ORDER BY e.Sequence
                LIMIT $limit;
                """;

            _ = command.Parameters.AddWithValue("$source", fork.SourceSessionId);

            _ = command.Parameters.AddWithValue("$session", fork.SessionId);

            _ = command.Parameters.AddWithValue("$after", lastExamined);

            _ = command.Parameters.AddWithValue("$maximum", maximum);

            _ = command.Parameters.AddWithValue("$limit", MaxRowsPerBatch - 1);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                examined++;

                lastExamined = reader.GetInt64(0);

                // Missing source rows may be erased inherited history, so absence cannot prove native authorship.
                if (reader.IsDBNull(2)
                    || !Guid.TryParseExact(reader.GetString(2), "D", out Guid sourceEntry)
                    || !Guid.TryParseExact(reader.GetString(1), "D", out Guid childEntry))
                {
                    unprovable = true;

                    break;
                }

                if (childEntry == CopiedEntryId(child, sourceEntry))
                {
                    if (reachedNative)
                    {
                        unprovable = true;

                        break;
                    }

                    lastCopied = lastExamined;
                }
                else
                {
                    reachedNative = true;
                }
            }
        }

        if (unprovable)
        {
            await PublishAsync(connection, transaction, fork, fork.MaximumSequence, 2, cancellationToken).ConfigureAwait(false);

            return new GrimoireSchemaBackfillBatch(null, examined + 1, false);
        }

        if (examined == MaxRowsPerBatch - 1 && lastExamined < maximum)
        {
            return new GrimoireSchemaBackfillBatch(
                Serialize(new Position(fork.SessionId, fork.SourceSessionId, maximum, lastExamined, lastCopied, reachedNative)),
                examined,
                false);
        }

        await PublishAsync(connection, transaction, fork, lastCopied, 1, cancellationToken).ConfigureAwait(false);

        return new GrimoireSchemaBackfillBatch(null, examined + 1, false);
    }

    private static async Task<PendingFork?> ReadForkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT s.Id, s.ForkedFromSessionId, s.CreatedAt,
                COALESCE((SELECT MAX(e.Sequence) FROM "Entries" e WHERE e.SessionId = s.Id), 0),
                EXISTS(SELECT 1 FROM "Sessions" parent WHERE parent.Id = s.ForkedFromSessionId)
            FROM "Sessions" s
            WHERE s.ForkedFromSessionId IS NOT NULL
                AND ($session IS NULL OR s.Id = $session)
                AND NOT EXISTS (SELECT 1 FROM campaign_fork_frontiers f WHERE f.SessionId = s.Id)
            ORDER BY s.Id
            LIMIT 1;
            """;

        _ = command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PendingFork(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetBoolean(4))
            : null;
    }

    private static async Task PublishAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PendingFork fork,
        long inheritedThrough,
        int proof,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            INSERT INTO campaign_fork_frontiers
                (SessionId, SourceSessionId, InheritedThroughSequence, ProofKindCode, CreatedAtUtc)
            VALUES ($session, $source, $sequence, $proof, $created);
            """;

        _ = command.Parameters.AddWithValue("$session", fork.SessionId);

        _ = command.Parameters.AddWithValue("$source", fork.SourceSessionId);

        _ = command.Parameters.AddWithValue("$sequence", inheritedThrough);

        _ = command.Parameters.AddWithValue("$proof", proof);

        _ = UtcInstantSql.AddStoredParameter(command, "$created", fork.CreatedAtUtc);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // This is the same stable domain-separated recipe used by SessionRepository.ForkAsync.
    private static Guid CopiedEntryId(Guid sessionId, Guid entryId)
    {
        Span<byte> material = stackalloc byte[33];

        _ = sessionId.TryWriteBytes(material[..16]);

        _ = entryId.TryWriteBytes(material[16..32]);

        material[32] = 1;

        Span<byte> digest = stackalloc byte[32];

        _ = SHA256.HashData(material, digest);

        return new Guid(digest[..16]);
    }

    private static string Serialize(Position position) => string.Join(':',
        "v1",
        position.MaximumSequence.ToString(CultureInfo.InvariantCulture),
        position.LastExaminedSequence.ToString(CultureInfo.InvariantCulture),
        position.LastCopiedSequence.ToString(CultureInfo.InvariantCulture),
        position.ReachedNative ? "1" : "0",
        Convert.ToBase64String(Encoding.UTF8.GetBytes(position.SessionId)),
        Convert.ToBase64String(Encoding.UTF8.GetBytes(position.SourceSessionId)));

    private static Position? Parse(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        string[] parts = cursor.Split(':');

        if (parts.Length != 7 || parts[0] != "v1"
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long maximum)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long examined)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long copied)
            || copied < 0 || examined < copied || maximum < examined || parts[4] is not ("0" or "1"))
        {
            throw new InvalidDataException("The Campaign fork frontier backfill cursor is malformed.");
        }

        try
        {
            string session = Encoding.UTF8.GetString(Convert.FromBase64String(parts[5]));

            string source = Encoding.UTF8.GetString(Convert.FromBase64String(parts[6]));

            if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(source))
            {
                throw new InvalidDataException("The Campaign fork frontier backfill cursor has no source identity.");
            }

            return new Position(session, source, maximum, examined, copied, parts[4] == "1");
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The Campaign fork frontier backfill cursor is malformed.", exception);
        }
    }

    private sealed record Position(
        string SessionId,
        string SourceSessionId,
        long MaximumSequence,
        long LastExaminedSequence,
        long LastCopiedSequence,
        bool ReachedNative);

    private sealed record PendingFork(string SessionId, string SourceSessionId, string CreatedAtUtc, long MaximumSequence, bool SourceExists);
}
