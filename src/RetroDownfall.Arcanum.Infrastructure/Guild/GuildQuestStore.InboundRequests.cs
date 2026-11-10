using Microsoft.Data.Sqlite;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal static async Task<Result<GuildRemoteInvocationOptions?>> ReadRemoteContinuationOptionsWithinAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid questId, Guid continuationId, CancellationToken ct)
    {
        var quest = await ReadWithinAsync(connection, transaction, questId, ct).ConfigureAwait(false);

        if (quest.IsFailure) return quest.Error;

        var continuation = await ReadContinuationWithinAsync(connection, transaction, quest.Value, continuationId, ct).ConfigureAwait(false);

        if (continuation.IsFailure) return continuation.Error;

        await using SqliteCommand read = Command(connection, transaction, """
            SELECT RemoteOptionsJson IS NULL,
                CASE WHEN typeof(RemoteOptionsJson)='text' AND length(CAST(RemoteOptionsJson AS BLOB)) BETWEEN 1 AND 65536 THEN RemoteOptionsJson END,
                CASE WHEN typeof(Digest)='blob' AND length(Digest)=32 THEN Digest END
            FROM guild_continuations WHERE QuestId=$quest AND ContinuationId=$continuation
            """);

        Add(read, "$quest", Id(questId));

        Add(read, "$continuation", Id(continuationId));

        await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(2)) return Unavailable;

        GuildRemoteInvocationOptions? options = null;

        if (!reader.GetBoolean(0))
        {
            if (reader.IsDBNull(1)) return Unavailable;

            string json = reader.GetString(1);

            try
            {
                options = JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildRemoteInvocationOptions);

                if (options is null || JsonSerializer.Serialize(options, GuildJsonContext.Default.GuildRemoteInvocationOptions) != json
                    || GuildQuestIntent.Compute(questId, new GuildContinueRequest(Guid.Empty, 1, continuationId, "text/plain", string.Empty, options)).IsFailure)
                    return Unavailable;
            }
            catch (JsonException)
            {
                return Unavailable;
            }
        }

        GuildContinuationStored accepted = continuation.Value;

        string expected = ContinuationIntent(continuationId, accepted.AcceptedRevision - 1,
            accepted.ContentRef.MimeType, accepted.ContentRef.Digest, options);

        return Convert.ToHexString((byte[])reader.GetValue(2)) == expected
            ? Result<GuildRemoteInvocationOptions?>.Success(options) : Unavailable;
    }

    internal Task<Result<T>> AdmitInboundDisclosureAsync<T>(GuildCallerContext caller, Guid questId,
        Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
        authority.AdmitQuestEffectAsync<T>(questId,
            async (connection, transaction, signets, token) =>
            {
                var current = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, current.Value, token).ConfigureAwait(false);

                return visible.IsFailure ? visible.Error : true;
            }, async token => Result<T>.Success(await action(token).ConfigureAwait(false)), ct);

    internal async Task<Result<GuildCancelQuestRequest?>> ReadInboundCancellationRequestAsync(GuildCallerContext caller,
        Guid questId, Guid mutation, CancellationToken ct)
    {
        var current = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (current.IsFailure) return current.Error;

        return await authority.InQuestTransactionAsync<GuildCancelQuestRequest?>(current.Value.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result visible = await RequireVisibleAsync(connection, transaction, caller, current.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                await using SqliteCommand read = Command(connection, transaction, """
                    SELECT ExpectedRevision,ReasonCode FROM guild_quest_cancellations
                    WHERE QuestId=$quest AND IdempotencyKey=$mutation AND typeof(ExpectedRevision)='integer'
                      AND ExpectedRevision>=1 AND typeof(ReasonCode)='integer' AND ReasonCode IN(0,1)
                    """);

                Add(read, "$quest", Id(questId));

                Add(read, "$mutation", Id(mutation));

                await using SqliteDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);

                return await reader.ReadAsync(token).ConfigureAwait(false)
                    ? new GuildCancelQuestRequest(mutation, reader.GetInt64(0), (GuildCancellationReason)reader.GetInt32(1))
                    : Result<GuildCancelQuestRequest?>.Success(null);
            }, ct).ConfigureAwait(false);
    }

    internal async Task<Result<GuildContinueRequest?>> ReadInboundContinuationRequestAsync(GuildCallerContext caller,
        Guid questId, Guid mutation, string body, CancellationToken ct)
    {
        var current = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (current.IsFailure) return current.Error;

        return await authority.InQuestTransactionAsync<GuildContinueRequest?>(current.Value.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result visible = await RequireVisibleAsync(connection, transaction, caller, current.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                await using SqliteCommand read = Command(connection, transaction, """
                    SELECT ContinuationId,AcceptedRevision FROM guild_continuations
                    WHERE QuestId=$quest AND MutationId=$mutation AND PeerId=$peer AND PeerGrantRevision=$revision
                      AND AuthorityCode=2 AND typeof(ContinuationId)='text' AND length(ContinuationId)=36
                      AND typeof(AcceptedRevision)='integer' AND AcceptedRevision>=2
                    """);

                Add(read, "$quest", Id(questId));

                Add(read, "$mutation", Id(mutation));

                Add(read, "$peer", caller.PeerId);

                Add(read, "$revision", caller.PeerGrantRevision);

                await using SqliteDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Result<GuildContinueRequest?>.Success(null);

                if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid continuation)) return Unavailable;

                return new GuildContinueRequest(mutation, reader.GetInt64(1) - 1, continuation, "text/plain", body);
            }, ct).ConfigureAwait(false);
    }
}
