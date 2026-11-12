using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal async Task<Result<GuildContinueRequest>> ReadOperatorContinuationAsync(GuildCallerContext caller, Guid questId,
        GuildSendOperatorMessageRequest request, CancellationToken ct)
    {
        Result<GuildQuestStored> located = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<GuildContinueRequest>(located.Value.Quest.RootQuestId,
            async (connection, transaction, _, token) =>
            {
                Result<GuildQuestStored> current = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, current.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                Guid? continuation = current.Value.Quest.AwaitingInput?.ContinuationId;

                await using SqliteCommand prior = Command(connection, transaction, """
                    SELECT CASE WHEN typeof(ContinuationId)='text' AND length(CAST(ContinuationId AS BLOB))=36 THEN ContinuationId END
                    FROM guild_continuations WHERE QuestId=$quest AND MutationId=$mutation
                        AND AuthorityCode=1 AND OperatorPrincipalId=$principal
                    """);

                Add(prior, "$quest", Id(questId));

                Add(prior, "$mutation", Id(request.IdempotencyKey));

                Add(prior, "$principal", Id(caller.PrincipalId));

                object? saved = await prior.ExecuteScalarAsync(token).ConfigureAwait(false);

                if (saved is not null)
                {
                    if (saved is not string text || !Guid.TryParseExact(text, "D", out Guid accepted) || accepted == Guid.Empty) return Unavailable;

                    continuation = accepted;
                }

                return continuation is { } id
                    ? new GuildContinueRequest(request.IdempotencyKey, request.ExpectedRevision, id, request.MimeType, request.Body)
                    : new Error(ErrorCodes.Guild.RevisionConflict, "The Quest has no current continuation request.");
            }, ct).ConfigureAwait(false);
    }
}
