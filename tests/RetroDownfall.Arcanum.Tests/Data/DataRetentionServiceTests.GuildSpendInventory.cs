using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Tests.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task Guild_spend_authority_and_captured_evidence_are_in_status_and_factory_plan()
    {
        RequireSqlCipher();

        var service = CreateService();

        var initial = await service.GetStatusAsync();

        Assert.Equal(2, Assert.Single(initial.Items, item => item.DataClass == RetentionDataClass.GuildEvidence).Rows);

        await _db!.Database.OpenConnectionAsync();

        _ = await GuildQuestStoreTests.CaptureForEncryptedRestoreAsync(_db.Database.GetDbConnection().ConnectionString);

        var inventory = await service.GetStatusAsync();

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.FactoryReset));

        long actual = await CampaignScalarCountAsync("SELECT (SELECT count(*) FROM guild_spend_authority_state)+(SELECT count(*) FROM guild_spend_sources)+(SELECT count(*) FROM guild_spend_receipts)+(SELECT count(*) FROM guild_spend_refreshes)");

        Assert.Equal(3, actual);

        long rows = Assert.Single(inventory.Items, item => item.DataClass == RetentionDataClass.GuildEvidence).Rows;

        Assert.Equal(20, rows);

        Assert.Equal(23, Assert.Single(plan.Items, item => item.DataClass == RetentionDataClass.GuildEvidence).DerivedRecords);
    }

    [SkippableFact]
    public async Task Factory_reset_removes_source_free_spend_authority_and_bootstrap_replaces_it()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = "SELECT KeyId || AuthorityEpoch || hex(CredentialKey) FROM guild_spend_authority_state";

        string before = Assert.IsType<string>(await read.ExecuteScalarAsync());

        var reset = await ReconcileFactoryResetV0Async(CreateService(), "guild-spend-authority-reset");

        Assert.Equal(1, reset.Summary.Completed);

        Assert.Equal(0, reset.Summary.RequiresAttention);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM guild_spend_authority_state"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM pragma_foreign_key_check"));

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy);

        Assert.NotEqual(before, Assert.IsType<string>(await read.ExecuteScalarAsync()));
    }

    [SkippableFact]
    public async Task Factory_reset_later_failure_rolls_back_spend_authority_deletion()
    {
        RequireSqlCipher();

        await SeedBudgetResetAccountsAsync();

        await ExecuteAsync("CREATE TEMP TRIGGER fail_after_spend BEFORE DELETE ON guild_budget_accounts BEGIN SELECT RAISE(ABORT,'injected'); END");

        var reset = await ReconcileFactoryResetV0Async(CreateService(), "guild-spend-rollback");

        Assert.Equal(0, reset.Summary.Completed);

        Assert.Equal(1, reset.Summary.RequiresAttention);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_spend_authority_state"));

        Assert.Equal(2, await CampaignScalarCountAsync("SELECT count(*) FROM guild_budget_accounts"));
    }

    [SkippableFact]
    public async Task Factory_reset_accepts_physically_complete_unlimited_provider_source()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        var captured = await GuildQuestStoreTests.CaptureAndCompleteForResetAsync(_db.Database.GetDbConnection().ConnectionString, sessionId);

        await SeedTerminalSpendRefreshAsync(captured.EffectId);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_spend_refreshes"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=2 AND UnknownUsd=1 AND UnknownTokens=0"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM guild_result_outbox WHERE DeliveryStateCode=1"));

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await using (SqliteTransaction snapshot = connection.BeginTransaction())
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = snapshot;

            command.CommandText = $"SELECT Id, {GuildQuestRetentionPins.NonAccountingUnresolved} FROM guild_quests c";

            Guid quest;

            await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());

                quest = Guid.Parse(reader.GetString(0));

                Assert.Equal(0L, reader.GetInt64(1));
            }

            Assert.True(await GuildQuestStore.AccountingClearWithinAsync(connection, snapshot, quest, quest, default));
        }

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Empty(plan.Conflicts);

        await using SqliteCommand authority = connection.CreateCommand();

        authority.CommandText = "SELECT KeyId || AuthorityEpoch || hex(CredentialKey) FROM guild_spend_authority_state";

        string before = Assert.IsType<string>(await authority.ExecuteScalarAsync());

        var reset = await ReconcileFactoryResetV0Async(CreateService(), "guild-complete-spend-reset");

        Assert.Equal(1, reset.Summary.Completed);

        Assert.Equal(0, reset.Summary.RequiresAttention);

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT (SELECT count(*) FROM guild_spend_authority_state)+(SELECT count(*) FROM guild_spend_sources)+(SELECT count(*) FROM guild_spend_receipts)+(SELECT count(*) FROM guild_spend_refreshes)"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM pragma_foreign_key_check"));

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy);

        Assert.NotEqual(before, Assert.IsType<string>(await authority.ExecuteScalarAsync()));

        await using SqliteTransaction transaction = connection.BeginTransaction();

        var stale = await captured.Credential.VerifyWithinAsync(connection, transaction, captured.EffectId, captured.InstallationGeneration, default);

        Assert.True(stale.IsFailure);
    }

    [SkippableFact]
    public async Task Factory_reset_late_failure_preserves_terminal_provider_evidence()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        var captured = await GuildQuestStoreTests.CaptureAndCompleteForResetAsync(_db.Database.GetDbConnection().ConnectionString, sessionId);

        await SeedTerminalSpendRefreshAsync(captured.EffectId);

        await ExecuteAsync("CREATE TEMP TRIGGER fail_after_provider_evidence BEFORE DELETE ON guild_budget_accounts BEGIN SELECT RAISE(ABORT,'injected'); END");

        var reset = await ReconcileFactoryResetV0Async(CreateService(), "guild-spend-evidence-rollback");

        Assert.Equal(0, reset.Summary.Completed);

        Assert.Equal(1, reset.Summary.RequiresAttention);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_spend_authority_state"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_spend_sources"));

        Assert.Equal(2, await CampaignScalarCountAsync("SELECT count(*) FROM guild_spend_receipts"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_spend_refreshes WHERE AcceptedReceiptId IS NOT NULL"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT count(*) FROM pragma_foreign_key_check"));
    }

    [SkippableFact]
    public async Task Factory_reset_refuses_reinserted_provider_source_without_rotating_authority()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        var captured = await GuildQuestStoreTests.CaptureAndCompleteForResetAsync(_db.Database.GetDbConnection().ConnectionString, sessionId);

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT KeyId || AuthorityEpoch || hex(CredentialKey) FROM guild_spend_authority_state";

        string authority = Assert.IsType<string>(await command.ExecuteScalarAsync());

        command.CommandText = "SELECT name FROM pragma_table_info('guild_spend_sources') ORDER BY cid";

        List<string> columns = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        }

        string[] deferred = ["GenerationId", "InitialSnapshot", "InitialDigest", "InitialOutcome", "InitialEventSequence"];

        string names = string.Join(",", columns.Select(column => "\"" + column + "\""));

        string values = string.Join(",", columns.Select(column => deferred.Contains(column, StringComparer.Ordinal)
            ? "NULL" : "OLD.\"" + column + "\""));

        string restored = string.Join(",", deferred.Select(column => "\"" + column + "\"=OLD.\"" + column + "\""));

        command.CommandText = "PRAGMA foreign_keys=OFF";

        await command.ExecuteNonQueryAsync();

        int reinsertionCount = 0;

        connection.CreateFunction("task22_observe_reinsert", () => ++reinsertionCount);

        command.CommandText = $"CREATE TEMP TRIGGER retain_provider_source AFTER DELETE ON guild_spend_sources BEGIN INSERT INTO guild_spend_sources({names}) VALUES({values}); UPDATE guild_spend_sources SET {restored} WHERE EffectId=OLD.EffectId; SELECT task22_observe_reinsert(); END";

        await command.ExecuteNonQueryAsync();

        var reset = await ReconcileFactoryResetV0Async(CreateService(), "guild-retained-source-reset");

        Assert.Equal(0, reset.Summary.Completed);

        Assert.Equal(1, reset.Summary.RequiresAttention);

        Assert.Equal(1, reinsertionCount);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_spend_authority_state"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_spend_sources"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_spend_receipts"));

        command.CommandText = "SELECT KeyId || AuthorityEpoch || hex(CredentialKey) FROM guild_spend_authority_state";

        Assert.Equal(authority, await command.ExecuteScalarAsync());

        await using SqliteTransaction transaction = connection.BeginTransaction();

        var verified = await captured.Credential.VerifyWithinAsync(connection, transaction, captured.EffectId, captured.InstallationGeneration, default);

        Assert.True(verified.IsSuccess, verified.IsFailure ? verified.Error.Message : null);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_reset_rejects_detached_effect_and_unproved_linked_funding(bool linkedFunding)
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        _ = await GuildQuestStoreTests.CaptureAndCompleteForResetAsync(_db.Database.GetDbConnection().ConnectionString, sessionId);

        Assert.Empty((await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset))).Conflicts);

        if (linkedFunding)
        {
            await ExecuteAsync("""
                INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,ParentAccountId,Revision)
                SELECT '12345678-1234-4234-8234-123456789ABC',1,'12345678-1234-4234-8234-123456789ABC',
                    RootAccountId,AccountId,1 FROM guild_budget_accounts LIMIT 1
                """);
        }
        else
        {
            await ExecuteAsync("""
                INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,Revision)
                VALUES('12345678-1234-4234-8234-123456789ABC',1,'12345678-1234-4234-8234-123456789ABC',
                    '12345678-1234-4234-8234-123456789ABC',1);
                INSERT INTO guild_budget_effects(EffectId,AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,
                    SourceId,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,StateCode,Revision)
                SELECT 'ABCDEF12-3456-4789-ABCD-EF1234567890','12345678-1234-4234-8234-123456789ABC',
                    QuestId,DispatchId,AccountingAssociationId,0,'ABCDEF12-3456-4789-ABCD-EF1234567891','0',0,0,0,2,1
                FROM guild_budget_effects LIMIT 1;
                INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision)
                VALUES('ABCDEF12-3456-4789-ABCD-EF1234567892','ABCDEF12-3456-4789-ABCD-EF1234567890',zeroblob(32),'0',0,0,0,1,0,1);
                """);
        }

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(plan.Conflicts, conflict => conflict.Code == "Guild.ReconciliationRequired");
    }

    [SkippableFact]
    public async Task Factory_reset_rejects_disconnected_quest_cycle()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        _ = await GuildQuestStoreTests.CaptureAndCompleteForResetAsync(_db.Database.GetDbConnection().ConnectionString, sessionId);

        Assert.Empty((await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset))).Conflicts);

        await ExecuteAsync("""
            INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,CompletedAt,AttemptCount,CheckpointVersion,PublicSummary,Revision)
            VALUES('abcdef1234564789abcdef1234567890','guild-quest',4,2,'2000-01-01T00:00:00Z','2000-01-01T00:00:00Z',1,1,'test',1);
            """);

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT name FROM pragma_table_info('guild_quests') ORDER BY cid";

        List<string> columns = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        }

        string id = "ABCDEF12-3456-4789-ABCD-EF1234567890";

        string SelectColumn(string column) => column switch
        {
            "Id" or "ParentQuestId" => "'" + id + "'",
            "OperationId" => "'abcdef1234564789abcdef1234567890'",
            "LogicalCallId" or "IssuingStepId" or "MutationId" or "SignetId" or "RunId" => "'" + id + "'",
            "IssuedSequence" => "(SELECT max(Sequence)+1 FROM guild_operator_sequences)",
            _ => "\"" + column + "\"",
        };

        command.CommandText = "INSERT INTO guild_quests SELECT " + string.Join(",", columns.Select(SelectColumn)) + " FROM guild_quests LIMIT 1";

        await command.ExecuteNonQueryAsync();

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(plan.Conflicts, conflict => conflict.Code == "Guild.ReconciliationRequired"
            && conflict.Message == "Guild Quest lineage could not be proved.");
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_reset_preserves_unknown_evidence_under_finite_funding(bool finiteTokens)
    {
        RequireSqlCipher();

        // This negative case uses the existing schema-seeded physical-effect fixture.
        await SeedGuildPhysicalRetentionAsync(2, copied: true, unknown: false, authentic: true,
            unknownTokens: false, finiteUsd: !finiteTokens, finiteTokens: finiteTokens);

        await ExecuteAsync("""
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','99999999-9999-9999-9999-999999999999',zeroblob(32),5,0,1,
                '88888888-8888-8888-8888-888888888888',1,'2000-01-01T00:00:00.0000000Z');
            UPDATE guild_budget_accounts SET Closed=1,SettlementReceiptId='99999999-9999-9999-9999-999999999999',Revision=2;
            """);

        DataRetentionPlan baseline = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Empty(baseline.Conflicts);

        await ExecuteAsync("""
            UPDATE guild_budget_effects SET UnknownUsd=@usd,UnknownTokens=@tokens,Revision=Revision+1;
            INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision)
            SELECT 'ABCDEF12-3456-4789-ABCD-EF1234567892',EffectId,zeroblob(32),KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,1,0,Revision
            FROM guild_budget_effects;
            """, ("@usd", finiteTokens ? 0 : 1), ("@tokens", finiteTokens ? 1 : 0));

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(plan.Conflicts, conflict => conflict.Code == "Guild.ReconciliationRequired"
            && conflict.ResourceId == "11111111-1111-1111-1111-111111111111");
    }

    [SkippableFact]
    public async Task Reset_lineage_child_lookup_uses_parent_index()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN SELECT child.Id FROM guild_quests child WHERE child.ParentQuestId=$parent";

        command.Parameters.AddWithValue("$parent", "11111111-1111-1111-1111-111111111111");

        List<string> details = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync()) details.Add(reader.GetString(3));

        Assert.Contains(details, detail => detail.Contains("SEARCH", StringComparison.Ordinal)
            && detail.Contains("idx_guild_quests_parent", StringComparison.Ordinal));
    }

    // This is a schema-level terminal refresh and accepted-receipt fixture; no refresh service is invoked.
    private async Task SeedTerminalSpendRefreshAsync(Guid effectId)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT e.QuestId,q.RootQuestId,e.SourceId,q.InstallationGeneration,g.OriginPrincipalId,
                g.AuthorityEpoch,s.AuthorityEpoch,s.AuthorityKeyId
            FROM guild_budget_effects e JOIN guild_quests q ON q.Id=e.QuestId
            JOIN guild_signets g ON g.SignetId=q.SignetId JOIN guild_spend_sources s ON s.EffectId=e.EffectId
            WHERE e.EffectId=$effect
            """;

        string effect = effectId.ToString("D").ToUpperInvariant();

        command.Parameters.AddWithValue("$effect", effect);

        string[] values = new string[8];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());

            for (int i = 0; i < values.Length; i++) values[i] = reader.GetString(i);
        }

        Guid refreshId = Guid.NewGuid();

        Guid operationId = Guid.NewGuid();

        string refresh = refreshId.ToString("D").ToUpperInvariant();

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", "Arcanum.Guild.RefreshRequest.v1",
            values[3], values[4], values[0], effect, refresh)));

        string checkpoint = $$"""
            {"version":1,"refreshId":"{{refreshId:D}}","questId":"{{values[0]}}","rootQuestId":"{{values[1]}}","effectId":"{{effectId:D}}","sourceId":"{{values[2]}}","operationId":"{{operationId:D}}","installationGeneration":"{{values[3]}}","principalId":"{{values[4]}}","signetEpoch":"{{values[5]}}","spendEpoch":"{{values[6]}}","spendKeyId":"{{values[7]}}","phase":"admitted"}
            """;

        command.CommandText = "SELECT OriginalResult FROM guild_spend_receipts WHERE EffectId=$effect AND OriginCode=0";

        byte[] initial = Assert.IsType<byte[]>(await command.ExecuteScalarAsync());

        GuildSpendReceiptDto initialReceipt = Assert.IsType<GuildSpendReceiptDto>(
            JsonSerializer.Deserialize(initial, GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto));

        Guid acceptedId = Guid.NewGuid();

        GuildSpendReceiptDto accepted = initialReceipt with
        {
            ReceiptId = acceptedId,
            Origin = GuildSpendOrigin.ProviderReported,
            EvidenceRevision = initialReceipt.EvidenceRevision + 1,
        };

        command.CommandText = """
            INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision)
            SELECT $evidence,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision+1
            FROM guild_budget_evidence WHERE EffectId=$effect AND AppliedRevision=$priorRevision;
            INSERT INTO guild_spend_receipts(ReceiptId,EffectId,EvidenceReceiptId,EvidenceRevision,OriginCode,RequestIdentity,
                RequestDigest,OriginalResult,OriginalEventSequence,AcceptedAtUtc)
            VALUES($accepted,$effect,$evidence,$revision,3,$requestIdentity,$digest,$result,$sequence,$acceptedAt);
            INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,CompletedAt,AttemptCount,
                CheckpointVersion,CheckpointPayload,PublicSummary,Revision)
            VALUES($operation,'guild-spend-refresh',4,2,$at,$at,1,1,$checkpoint,'Provider refresh',1);
            INSERT INTO guild_spend_refreshes(RefreshId,EffectId,OperationId,RequestDigest,AcceptedReceiptId)
            VALUES($refresh,$effect,$operation,$digest,$accepted);
            """;

        command.Parameters.AddWithValue("$evidence", Guid.NewGuid().ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$accepted", acceptedId.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$priorRevision", initialReceipt.EvidenceRevision);

        command.Parameters.AddWithValue("$revision", accepted.EvidenceRevision);

        command.Parameters.AddWithValue("$requestIdentity", Convert.ToHexString(digest));

        command.Parameters.AddWithValue("$result", JsonSerializer.SerializeToUtf8Bytes(accepted, GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto));

        command.Parameters.AddWithValue("$sequence", accepted.EventSequence);

        command.Parameters.AddWithValue("$acceptedAt", accepted.AcceptedAt.ToString("O"));

        command.Parameters.AddWithValue("$operation", operationId.ToString("N"));

        command.Parameters.AddWithValue("$at", OldTimestamp);

        command.Parameters.AddWithValue("$checkpoint", Encoding.UTF8.GetBytes(checkpoint));

        command.Parameters.AddWithValue("$refresh", refresh);

        command.Parameters.AddWithValue("$digest", digest);

        await command.ExecuteNonQueryAsync();
    }
}
