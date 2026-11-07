using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal enum GuildPortalEventSource
{
    Quest = 0,

    Dispatch = 1,

    ResultDelivery = 2,

    Envelope = 3,

    Inbox = 4,

    Content = 5,

    Signet = 6,
}

internal static class GuildPortalEvents
{
    internal static async Task<Result<long>> RecordSourceWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid root, Guid quest, GuildReceiptSourceKind source, Guid receiptId, long revision, DateTimeOffset now, CancellationToken ct)
    {
        Result<long> projection = await AppendWithinAsync(connection, transaction, root, quest,
            source == GuildReceiptSourceKind.Settlement ? GuildEventKind.SpendChanged : GuildEventKind.QuestChanged,
            quest, revision, null, now, ct).ConfigureAwait(false);

        if (projection.IsFailure) return projection.Error;

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO guild_portal_outbox(SourceKindCode,ReceiptId,QuestId,CreatedAtUtc)
            VALUES($kind,$receipt,$quest,$now) ON CONFLICT(SourceKindCode,ReceiptId) DO NOTHING;
            SELECT QuestId FROM guild_portal_outbox WHERE SourceKindCode=$kind AND ReceiptId=$receipt;
            """;

        command.Parameters.AddWithValue("$kind", (int)source);

        command.Parameters.AddWithValue("$receipt", Id(receiptId));

        command.Parameters.AddWithValue("$quest", Id(quest));

        command.Parameters.AddWithValue("$now", UtcInstantText.Format(now));

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string stored && stored == Id(quest)
            ? projection : new Error(ErrorCodes.Guild.IdempotencyConflict, "The source receipt belongs to another Quest.");
    }

    internal static async Task<Result<long>> AppendWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid root, Guid quest, GuildEventKind kind, Guid objectId, long revision, Guid? envelopeId,
        DateTimeOffset now, CancellationToken ct, GuildPortalEventSource source = GuildPortalEventSource.Quest)
    {
        if (root == Guid.Empty || quest == Guid.Empty || objectId == Guid.Empty || revision < 1
            || !Enum.IsDefined(kind) || !Enum.IsDefined(source) || envelopeId == Guid.Empty)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The Portal event source binding is incomplete.");

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT EventSequence,QuestId,EnvelopeId FROM guild_events
            WHERE RootQuestId=$root AND EventKindCode=$kind AND SourceKindCode=$source AND ObjectId=$object AND Revision=$revision
            """;

        command.Parameters.AddWithValue("$root", Id(root));

        command.Parameters.AddWithValue("$quest", Id(quest));

        command.Parameters.AddWithValue("$kind", (int)kind);

        command.Parameters.AddWithValue("$source", (int)source);

        command.Parameters.AddWithValue("$object", Id(objectId));

        command.Parameters.AddWithValue("$revision", revision);

        command.Parameters.AddWithValue("$envelope", envelopeId is { } envelope ? Id(envelope) : DBNull.Value);

        await using (SqliteDataReader prior = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await prior.ReadAsync(ct).ConfigureAwait(false))
            {
                bool matches = prior.GetString(1) == Id(quest)
                    && (envelopeId is { } id ? !prior.IsDBNull(2) && prior.GetString(2) == Id(id) : prior.IsDBNull(2));

                return matches ? prior.GetInt64(0)
                    : new Error(ErrorCodes.Guild.IdempotencyConflict, "The event source identity has already committed different scope.");
            }
        }

        command.CommandText = "INSERT INTO guild_event_streams(RootQuestId,LastSequence,RetainedAfterSequence) VALUES($root,0,0) ON CONFLICT(RootQuestId) DO NOTHING";

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        command.CommandText = """
            UPDATE guild_event_streams SET LastSequence=LastSequence+1
            WHERE RootQuestId=$root AND typeof(LastSequence)='integer' AND LastSequence BETWEEN 0 AND 9223372036854775806
            RETURNING LastSequence
            """;

        object? allocated = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (allocated is not long sequence)
            return new Error(ErrorCodes.Guild.SequenceExhausted, "The Portal event sequence cannot advance.");

        command.Parameters.AddWithValue("$sequence", sequence);

        command.CommandText = """
            INSERT INTO guild_events(RootQuestId,EventSequence,EventKindCode,ObjectId,Revision,QuestId,EnvelopeId,CreatedAtUtc,SourceKindCode)
            VALUES($root,$sequence,$kind,$object,$revision,$quest,$envelope,$now,$source)
            """;

        command.Parameters.AddWithValue("$now", UtcInstantText.Format(now));

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return sequence;
    }

    private static string Id(Guid value) => value.ToString("D").ToUpperInvariant();
}
