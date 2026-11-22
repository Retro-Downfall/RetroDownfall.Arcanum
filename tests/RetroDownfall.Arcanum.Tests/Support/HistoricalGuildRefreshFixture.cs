using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static class HistoricalGuildRefreshFixture
{
    internal static async Task<Guid> SeedAsync(
        SqliteConnection connection, GuildCallerContext caller, Guid questId, Guid effectId, Guid refreshId)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Guid operationId = Guid.NewGuid();

        GuildSpendRefreshCheckpoint checkpoint;

        await using (SqliteCommand query = connection.CreateCommand())
        {
            query.Transaction = transaction;

            query.CommandText = """
                SELECT q.RootQuestId, e.SourceId, g.AuthorityEpoch, s.AuthorityEpoch, s.AuthorityKeyId
                FROM guild_budget_effects e JOIN guild_quests q ON q.Id=e.QuestId
                JOIN guild_signets g ON g.SignetId=q.SignetId JOIN guild_spend_sources s ON s.EffectId=e.EffectId
                WHERE e.QuestId=$quest AND e.EffectId=$effect
                """;

            query.Parameters.AddWithValue("$quest", Id(questId));

            query.Parameters.AddWithValue("$effect", Id(effectId));

            await using SqliteDataReader row = await query.ExecuteReaderAsync();

            Assert.True(await row.ReadAsync());

            checkpoint = new(1, refreshId, questId, Guid.Parse(row.GetString(0)), effectId, Guid.Parse(row.GetString(1)),
                operationId, caller.InstallationGeneration, caller.PrincipalId!.Value, Guid.Parse(row.GetString(2)),
                Guid.Parse(row.GetString(3)), Guid.Parse(row.GetString(4)), "admitted");

            Assert.False(await row.ReadAsync());
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(checkpoint, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint);

        await LongRunningOperationStore.CreateWithinAsync(connection, transaction, operationId,
            new(LongRunningOperationKinds.GuildSpendRefresh, LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                "Historical provider refresh", DateTimeOffset.UtcNow), 1, payload, null, default);

        await using SqliteCommand insert = connection.CreateCommand();

        insert.Transaction = transaction;

        insert.CommandText = """
            INSERT INTO guild_spend_refreshes (RefreshId, EffectId, OperationId, RequestDigest)
            VALUES ($refresh, $effect, $operation, $digest);
            """;

        insert.Parameters.AddWithValue("$refresh", Id(refreshId));

        insert.Parameters.AddWithValue("$effect", Id(effectId));

        insert.Parameters.AddWithValue("$operation", operationId.ToString("N"));

        insert.Parameters.AddWithValue("$digest", GuildQuestStore.RefreshRequestDigest(
            caller.InstallationGeneration, caller.PrincipalId!.Value, questId, effectId, refreshId));

        Assert.Equal(1, await insert.ExecuteNonQueryAsync());

        await transaction.CommitAsync();

        return operationId;
    }

    internal static async Task<Guid> SeedAcceptedReceiptAsync(SqliteConnection connection, Guid questId, Guid effectId, Guid refreshId)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Guid receiptId = Guid.NewGuid();

        Guid digestSeed = Guid.NewGuid();

        byte[] digest = System.Security.Cryptography.SHA256.HashData(digestSeed.ToByteArray());

        DateTimeOffset acceptedAt = DateTimeOffset.UtcNow;

        Guid previousReceipt;

        long revision;

        long eventSequence;

        await using (SqliteCommand query = connection.CreateCommand())
        {
            query.Transaction = transaction;

            query.CommandText = """
                SELECT ReceiptId, EvidenceRevision, OriginalEventSequence
                FROM guild_spend_receipts WHERE EffectId=$effect ORDER BY EvidenceRevision DESC LIMIT 1
                """;

            query.Parameters.AddWithValue("$effect", Id(effectId));

            await using SqliteDataReader row = await query.ExecuteReaderAsync();

            Assert.True(await row.ReadAsync());

            previousReceipt = Guid.Parse(row.GetString(0));

            revision = row.GetInt64(1) + 1;

            eventSequence = row.GetInt64(2);
        }

        GuildSpendReceiptDto receipt = new(receiptId, questId, effectId, GuildSpendOrigin.ProviderReported,
            Convert.ToHexString(digest), new(0.30m, 130, false, false, true, new(110, 20, 40, 5)), revision, eventSequence, acceptedAt);

        await using SqliteCommand insert = connection.CreateCommand();

        insert.Transaction = transaction;

        insert.CommandText = """
            INSERT INTO guild_budget_evidence (ReceiptId, EffectId, Digest, KnownUsd, KnownTokens, UnknownUsd,
                UnknownTokens, Complete, NoDispatchProven, AppliedRevision)
            VALUES ($receipt,$effect,$digest,'0.3',130,0,0,1,0,$revision);

            INSERT INTO guild_spend_receipts (ReceiptId, EffectId, EvidenceReceiptId, EvidenceRevision, OriginCode,
                RequestIdentity, RequestDigest, OriginalResult, OriginalEventSequence, AcceptedAtUtc)
            VALUES ($receipt,$effect,$receipt,$revision,3,$identity,$digest,$result,$sequence,$now);

            UPDATE guild_spend_refreshes SET AcceptedReceiptId=$receipt WHERE RefreshId=$refresh;
            """;

        insert.Parameters.AddWithValue("$receipt", Id(receiptId));

        insert.Parameters.AddWithValue("$effect", Id(effectId));

        insert.Parameters.AddWithValue("$digest", digest);

        insert.Parameters.AddWithValue("$revision", revision);

        insert.Parameters.AddWithValue("$identity", previousReceipt.ToString("N"));

        insert.Parameters.AddWithValue("$result", JsonSerializer.SerializeToUtf8Bytes(receipt, GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto));

        insert.Parameters.AddWithValue("$sequence", eventSequence);

        insert.Parameters.AddWithValue("$now", UtcInstantText.Format(acceptedAt));

        insert.Parameters.AddWithValue("$refresh", Id(refreshId));

        Assert.Equal(3, await insert.ExecuteNonQueryAsync());

        await transaction.CommitAsync();

        return receiptId;
    }

    private static string Id(Guid value) => value.ToString("D").ToUpperInvariant();
}
