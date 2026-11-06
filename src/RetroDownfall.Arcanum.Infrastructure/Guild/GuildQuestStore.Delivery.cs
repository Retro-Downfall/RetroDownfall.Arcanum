using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildDeliveryState>> AcknowledgeRootResultAsync(GuildCallerContext caller,
        Guid questId, Guid resultId, string digest, long expectedDeliveryRevision, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || questId == Guid.Empty
            || resultId == Guid.Empty || !ReceiptDigestValid(digest))
            return Task.FromResult(Result<GuildDeliveryState>.Failure(InvalidReceipt));

        return authority.InQuestTransactionAsync<GuildDeliveryState>(questId,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                GuildQuestStored row = found.Value;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, row, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (row.Quest.ParentQuestId is not null || row.Quest.RootQuestId != questId
                    || row.Quest.ResultReceiptId != resultId || row.Quest.ReconciliationRequired) return Conflict;

                await using SqliteCommand receipt = Command(connection, transaction, """
                    SELECT b.DeliveryStateCode,b.Revision FROM guild_result_outbox b
                    JOIN guild_result_receipts r ON r.QuestId=b.QuestId AND r.ResultId=b.ResultId AND r.Digest=b.Digest
                    WHERE b.QuestId=$id AND b.ResultId=$result AND b.Digest=$digest AND b.RecipientQuestId IS NULL
                    """);

                Add(receipt, "$id", Id(questId));

                Add(receipt, "$result", Id(resultId));

                Add(receipt, "$digest", Convert.FromHexString(digest));

                long revision;

                GuildDeliveryState state;

                await using (SqliteDataReader reader = await receipt.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Conflict;

                    state = (GuildDeliveryState)reader.GetInt32(0);

                    revision = reader.GetInt64(1);
                }

                if (state == GuildDeliveryState.Applied) return state;

                if (state == GuildDeliveryState.Withheld || row.Quest.ResultRef?.ContentErased == true)
                    return new Error(ErrorCodes.Guild.ContentErased, "The result was withheld after content erasure.");

                if (state != GuildDeliveryState.Pending) return Unavailable;

                if (revision != expectedDeliveryRevision) return RevisionConflict;

                if (revision == long.MaxValue) return ExhaustedReceipt;

                await using SqliteCommand acknowledge = Command(connection, transaction, """
                    UPDATE guild_result_outbox SET DeliveryStateCode=2,Revision=Revision+1,AcknowledgedAtUtc=$now
                    WHERE QuestId=$id AND Revision=$revision AND DeliveryStateCode=1
                    """);

                Add(acknowledge, "$id", Id(questId));

                Add(acknowledge, "$revision", revision);

                Add(acknowledge, "$now", UtcInstantText.Format(time.GetUtcNow()));

                return await acknowledge.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1
                    ? GuildDeliveryState.Applied : RevisionConflict;
            }, ct);
    }
}
