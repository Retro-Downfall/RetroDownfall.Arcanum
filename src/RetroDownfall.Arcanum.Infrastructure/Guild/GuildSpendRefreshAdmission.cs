using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<bool>> ReadHistoricalSpendRefreshAsync(
        GuildCallerContext caller, GuildSpendRefreshCheckpoint checkpoint, CancellationToken ct) =>
        WithSpendOwnerAsync<bool>(caller, checkpoint.QuestId, async (connection, transaction, quest, _, token) =>
        {
            if (caller.PrincipalId is not { } principal || checkpoint.RootQuestId != quest.Quest.RootQuestId
                || checkpoint.RefreshId == Guid.Empty || checkpoint.EffectId == Guid.Empty)
            {
                return OwnerUnavailable;
            }

            Result owner = await GuildSpendRefreshOwnerProof.RequireWithinAsync(connection, transaction,
                checkpoint.QuestId, checkpoint.RootQuestId, token).ConfigureAwait(false);

            if (owner.IsFailure) return owner.Error;

            await using SqliteCommand command = Command(connection, transaction, """
                SELECT EffectId, OperationId, RequestDigest, AcceptedReceiptId FROM guild_spend_refreshes WHERE RefreshId=$refresh
                """);

            Add(command, "$refresh", Id(checkpoint.RefreshId));

            await using SqliteDataReader row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await row.ReadAsync(token).ConfigureAwait(false)
                || row.GetValue(0) is not string storedEffect || storedEffect != Id(checkpoint.EffectId)
                || row.GetValue(1) is not string operation || operation != checkpoint.OperationId.ToString("N")
                || row.GetValue(2) is not byte[] digest || digest.Length != 32
                || !CryptographicOperations.FixedTimeEquals(digest, RefreshRequestDigest(caller.InstallationGeneration, principal,
                    checkpoint.QuestId, checkpoint.EffectId, checkpoint.RefreshId)))
            {
                return Conflict;
            }

            if (row.IsDBNull(3)) return false;

            if (row.GetValue(3) is not string rawReceipt || !Guid.TryParseExact(rawReceipt, "D", out Guid receiptId)
                || receiptId == Guid.Empty) return Conflict;

            await row.DisposeAsync().ConfigureAwait(false);

            Result<GuildSpendReceiptDto> receipt = await ReadSpendReceiptWithinAsync(connection, transaction, checkpoint.QuestId,
                checkpoint.EffectId, receiptId, token).ConfigureAwait(false);

            if (receipt.IsFailure) return receipt.Error;

            return receipt.Value.Origin == GuildSpendOrigin.ProviderReported ? Result<bool>.Success(true) : Conflict;
        }, ct);
}
