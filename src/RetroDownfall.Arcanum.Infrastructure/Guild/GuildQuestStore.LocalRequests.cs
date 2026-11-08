using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal async Task<Result> ValidateLocalContinuationRequestAsync(GuildQuestDriver driver,
        GuildDispatchRequest dispatch, GuildContinueRequest request, CancellationToken ct)
    {
        Result<string> valid = GuildQuestIntent.Compute(driver.Execution.QuestId, request);

        if (valid.IsFailure) return valid.Error;

        if (request.ContinuationId != dispatch.DispatchId || request.IdempotencyKey != dispatch.Mutation?.IdempotencyKey)
            return Conflict;

        var accepted = await ReadLocalContinuationAsync(driver, request.ContinuationId, ct, false).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        if (accepted.Value is not { } answer || answer.Mutation != dispatch.Mutation
            || answer.Continuation.ContentRef.Digest != dispatch.InvocationDigest
            || answer.Mutation.CanonicalRequestDigest != ContinuationIntent(request, Encoding.UTF8.GetBytes(request.Body)))
            return Conflict;

        return Result.Success();
    }

    internal async Task<Result<GuildMutationIdentity>> ReadLocalCancellationAsync(GuildCallerContext caller,
        Guid questId, CancellationToken ct)
    {
        var located = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<GuildMutationIdentity>(located.Value.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var current = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, current.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (!current.Value.Quest.CancellationRequested) return Conflict;

                await using SqliteCommand receipt = Command(connection, transaction, """
                    SELECT IdempotencyKey,CanonicalRequestDigest FROM guild_quest_cancellations
                    WHERE QuestId=$quest AND typeof(IdempotencyKey)='text' AND length(CAST(IdempotencyKey AS BLOB))=36
                        AND typeof(CanonicalRequestDigest)='blob' AND length(CanonicalRequestDigest)=32
                        AND ExpectedRevision>=1 AND AcceptedRevision>ExpectedRevision AND AcceptedRevision<=$revision
                    ORDER BY AcceptedRevision DESC LIMIT 1
                    """);

                Add(receipt, "$quest", Id(questId));

                Add(receipt, "$revision", current.Value.Quest.Revision);

                await using SqliteDataReader reader = await receipt.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (!await reader.ReadAsync(token).ConfigureAwait(false)
                    || !Guid.TryParseExact(reader.GetString(0), "D", out Guid key) || key == Guid.Empty) return Conflict;

                return new GuildMutationIdentity(key, Convert.ToHexString((byte[])reader.GetValue(1)));
            }, ct).ConfigureAwait(false);
    }

    internal async Task<Result> ValidateLocalCancellationRequestAsync(GuildQuestDriver driver,
        GuildMutationIdentity mutation, CancellationToken ct)
    {
        if (!DriverShape(driver) || !ReceiptMutationValid(mutation)) return Result.Failure(InvalidReceipt);

        var validated = await authority.InQuestTransactionAsync<bool>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                if (!admitted.Value.Row.Quest.CancellationRequested) return Conflict;

                await using SqliteCommand receipt = Command(connection, transaction, """
                    SELECT EXISTS(SELECT 1 FROM guild_quest_cancellations
                        WHERE QuestId=$quest AND IdempotencyKey=$key AND CanonicalRequestDigest=$digest
                            AND ExpectedRevision>=1 AND AcceptedRevision>ExpectedRevision AND AcceptedRevision<=$revision)
                    """);

                Add(receipt, "$quest", Id(driver.Execution.QuestId));

                Add(receipt, "$key", Id(mutation.IdempotencyKey));

                Add(receipt, "$digest", Convert.FromHexString(mutation.CanonicalRequestDigest));

                Add(receipt, "$revision", admitted.Value.Row.Quest.Revision);

                return Equals(1L, await receipt.ExecuteScalarAsync(token).ConfigureAwait(false))
                    ? Result<bool>.Success(true) : Result<bool>.Failure(Conflict);
            }, ct).ConfigureAwait(false);

        return validated.IsSuccess ? Result.Success() : Result.Failure(validated.Error);
    }
}
