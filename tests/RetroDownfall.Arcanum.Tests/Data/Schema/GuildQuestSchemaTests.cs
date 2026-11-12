using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildQuestSchemaTests
{
    private const string Quest = "11111111-1111-1111-1111-111111111111";

    private const string Operation = "22222222222222222222222222222222";

    private const string Timestamp = "2026-11-06T00:00:00.0000000Z";

    [Fact]
    public async Task Fresh_install_admits_Quest_evidence_without_a_second_execution_state()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedQuestAsync(connection);

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM guild_quests WHERE OperationId='" + Operation + "'"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('guild_quests') WHERE name='State' OR name='StateCode'"));

        Assert.Equal(14L, await ScalarAsync(connection, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=2"));
    }

    [Fact]
    public async Task Published_v2_upgrades_to_fresh_catalog_without_altering_Core_or_authority_epoch()
    {
        Assert.Equal(GuildCanonicalSchemaVersionTwoFixture.PublishedFingerprint, GuildCanonicalSchemaVersionTwoFixture.Fingerprint);

        await using SqliteConnection upgraded = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, GuildCanonicalSchemaVersionTwoFixture.ChainSet(), 768, CancellationToken.None);

        object? core = await ScalarAsync(upgraded, "SELECT SourceDefinitionFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=0");

        object? epoch = await ScalarAsync(upgraded, "SELECT Epoch FROM guild_signet_authority_state");

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, 768, CancellationToken.None);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        await using SqliteConnection fresh = await OpenAsync();

        Assert.Equal(await ScalarAsync(fresh, "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2"),
            await ScalarAsync(upgraded, "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2"));

        Assert.Equal(core, await ScalarAsync(upgraded, "SELECT SourceDefinitionFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=0"));

        Assert.Equal(epoch, await ScalarAsync(upgraded, "SELECT Epoch FROM guild_signet_authority_state"));

        await SeedQuestAsync(upgraded);
    }

    [Theory]
    [InlineData("IntentDigest=randomblob(32)")]
    [InlineData("OperationId='33333333333333333333333333333333'")]
    [InlineData("IssuedSequence=2")]
    [InlineData("SessionId='33333333-3333-3333-3333-333333333333'")]
    [InlineData("GoalDigest=randomblob(32)")]
    [InlineData("RootQuestId='33333333-3333-3333-3333-333333333333'")]
    [InlineData("Name='replacement label'")]
    [InlineData("IssuerKindCode=1")]
    public async Task Accepted_Quest_identity_cannot_be_rewritten(string mutation)
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedQuestAsync(connection);

        await ExecuteAsync(connection, $"INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,PublicSummary,CheckpointVersion) VALUES ('33333333333333333333333333333333','guild-quest',0,0,'{Timestamp}','test',1)");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_quests SET " + mutation + ",Revision=Revision+1"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT Revision FROM guild_quests"));
    }

    [Fact]
    public async Task Quest_revision_and_denial_evidence_cannot_move_backwards()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedQuestAsync(connection);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_quests SET CancellationRequested=1"));

        await ExecuteAsync(connection, "UPDATE guild_quests SET CancellationRequested=1,Retired=1,Revision=Revision+1");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_quests SET CancellationRequested=0,Revision=Revision+1"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_quests SET Retired=0,Revision=Revision+1"));
    }

    [Fact]
    public async Task Issuance_tuple_and_operation_link_are_unique_and_preserve_LRO_evidence()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedQuestAsync(connection);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM LongRunningOperations WHERE Id='" + Operation + "'"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO guild_quests SELECT '33333333-3333-3333-3333-333333333333'," + QuestColumnsWithoutId + " FROM guild_quests"));
    }

    [Fact]
    public async Task A_new_operation_and_generated_identity_cannot_reopen_an_accepted_issuance_tuple()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedQuestAsync(connection);

        await ExecuteAsync(connection, $"INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,PublicSummary,CheckpointVersion) VALUES ('eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee','guild-quest',0,0,'{Timestamp}','test',1)");

        string freshCoordinates = string.Join(',', QuestColumnsWithoutId.Split(',').Select(column => column switch
        {
            "OperationId" => "'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'",
            "SignetId" => "'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF'",
            "IssuedSequence" => "2",
            _ => column,
        }));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "INSERT INTO guild_quests SELECT '33333333-3333-3333-3333-333333333333'," + freshCoordinates + " FROM guild_quests"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM guild_quests"));
    }

    [Fact]
    public async Task Invocation_and_Quest_issuers_have_separate_identity_namespaces()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedQuestAsync(connection);

        await ExecuteAsync(connection, $"INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,PublicSummary,CheckpointVersion) VALUES ('eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee','guild-quest',0,0,'{Timestamp}','test',1)");

        string freshCoordinates = string.Join(',', QuestColumnsWithoutId.Split(',').Select(column => column switch
        {
            "OperationId" => "'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'",
            "SignetId" => "'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF'",
            "IssuedSequence" => "2",
            "IssuerKindCode" => "1",
            _ => column,
        }));

        await ExecuteAsync(connection, "INSERT INTO guild_quests SELECT '33333333-3333-3333-3333-333333333333'," + freshCoordinates + " FROM guild_quests");

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM guild_quests"));
    }

    [Theory]
    [InlineData("SELECT Id FROM guild_quests WHERE GoalContentId='CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC'", "idx_guild_quests_goal")]
    [InlineData("SELECT QuestId FROM guild_result_receipts WHERE ContentId='CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC'", "idx_guild_result_receipts_content")]
    public async Task Content_reference_lookups_seek_their_covering_index(string query, string index)
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + query;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        string plan = reader.GetString(3);

        Assert.Contains("SEARCH", plan, StringComparison.Ordinal);

        Assert.Contains(index, plan, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(1, 4, true)]
    [InlineData(1, 3, false)]
    [InlineData(2, 1, false)]
    [InlineData(2, 3, true)]
    [InlineData(2, 4, true)]
    [InlineData(3, 2, false)]
    [InlineData(4, 2, false)]
    public async Task Dispatch_evidence_only_advances_along_proven_transitions(int before, int after, bool allowed)
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedQuestAsync(connection);

        await ExecuteAsync(connection, $"INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc) VALUES ('33333333-3333-3333-3333-333333333333','{Quest}',zeroblob(32),0,'44444444-4444-4444-4444-444444444444',{before},1,'{Timestamp}','{Timestamp}')");

        string update = $"UPDATE guild_dispatches SET EvidenceCode={after},Revision=Revision+1";

        if (allowed)
        {
            await ExecuteAsync(connection, update);

            Assert.Equal((long)after, await ScalarAsync(connection, "SELECT EvidenceCode FROM guild_dispatches"));
        }
        else
        {
            await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, update));
        }
    }

    [Theory]
    [InlineData("guild_result_receipts", "Digest=randomblob(32)")]
    [InlineData("guild_result_applications", "Digest=randomblob(32)")]
    [InlineData("guild_result_inputs", "Digest=randomblob(32)")]
    [InlineData("guild_quest_start_intents", "CreatedAtUtc='2026-11-07T00:00:00.0000000Z'")]
    public async Task Committed_receipts_and_parent_inputs_are_immutable(string table, string mutation)
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedQuestAsync(connection);

        await InsertReceiptsAsync(connection);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"UPDATE {table} SET {mutation}"));
    }

    [Fact]
    public async Task Result_parent_input_requires_its_exact_committed_application()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedQuestAsync(connection);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"INSERT INTO guild_result_inputs(RecipientQuestId,QuestId,ResultId,Digest,CreatedAtUtc) VALUES ('{Quest}','{Quest}','33333333-3333-3333-3333-333333333333',zeroblob(32),'{Timestamp}')"));

        await InsertReceiptsAsync(connection);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM guild_result_applications"));
    }

    [Fact]
    public async Task Accepted_continuation_identity_is_immutable_and_request_key_cannot_accept_another_answer()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedQuestAsync(connection);

        await ExecuteAsync(connection, $"INSERT INTO guild_continuations(QuestId,ContinuationId,MutationId,Digest,ContentId,AcceptedRevision,CreatedAtUtc) VALUES('{Quest}','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',zeroblob(32),'99999999-9999-9999-9999-999999999999',2,'{Timestamp}')");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_continuations SET Digest=randomblob(32)"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"INSERT INTO guild_continuations(QuestId,ContinuationId,MutationId,Digest,ContentId,AcceptedRevision,CreatedAtUtc) VALUES('{Quest}','33333333-3333-3333-3333-333333333333','FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',randomblob(32),'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',3,'{Timestamp}')"));
    }

    [Fact]
    public async Task A_parent_input_cannot_claim_another_digest_for_the_same_application()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedQuestAsync(connection);

        await InsertReceiptsAsync(connection);

        await ExecuteAsync(connection, "DELETE FROM guild_result_inputs");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"INSERT INTO guild_result_inputs(RecipientQuestId,QuestId,ResultId,Digest,CreatedAtUtc) VALUES ('{Quest}','{Quest}','33333333-3333-3333-3333-333333333333',randomblob(32),'{Timestamp}')"));
    }

    private const string QuestColumnsWithoutId = "OperationId,InstallationGeneration,IssuerIdentity,IssuerKindCode,IssuingStepId,LogicalCallId,MutationId,IntentDigest,ResolutionDigest,RootQuestId,ParentQuestId,AgentId,CardId,CardRevision,RunId,SignetId,Name,SessionId,CampaignId,WorkspaceId,GoalContentId,GoalRevision,GoalDigest,GoalMimeType,GoalByteLength,AllocationAccountId,CreatedAtUtc,UpdatedAtUtc,IssuedSequence,Revision,LastNonterminalStateCode,WaitReasonCode,QuestionId,ContinuationId,CancellationRequested,CancellationConverged,AccountingStateCode,Retired";

    private static async Task<SqliteConnection> OpenAsync()
    {
        SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        return connection;
    }

    internal static async Task SeedQuestAsync(SqliteConnection connection, Guid? sessionId = null, byte[]? goalDigest = null, Guid? campaignId = null)
    {
        _ = await LongRunningOperationStore.CreateWithinAsync(connection, null, Guid.Parse(Operation),
            new(LongRunningOperationKinds.GuildQuest, LongRunningOperationRecoveryPolicy.ReconcileAndComplete, "test",
                new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), SessionId: sessionId,
                RunId: Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")), 1,
            GuildQuestCheckpoint.Encode(Guid.Parse(Quest)), null, CancellationToken.None);

        string owner = sessionId is { } id ? "'" + id.ToString("D").ToUpperInvariant() + "'" : "NULL";

        string campaign = campaignId is { } bound ? "'" + bound.ToString("D").ToUpperInvariant() + "'" : "NULL";

        string digest = Convert.ToHexString(goalDigest ?? new byte[32]);

        await ExecuteAsync(connection, $"""
            INSERT INTO guild_quests(Id,{QuestColumnsWithoutId}) VALUES (
                '{Quest}','{Operation}','33333333-3333-3333-3333-333333333333','44444444-4444-4444-4444-444444444444',0,
                '55555555-5555-5555-5555-555555555555','66666666-6666-6666-6666-666666666666','77777777-7777-7777-7777-777777777777',
                zeroblob(32),zeroblob(32),'{Quest}',NULL,'88888888-8888-8888-8888-888888888888',
                '99999999-9999-9999-9999-999999999999',1,'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB','private label',{owner},{campaign},NULL,'CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',1,X'{digest}','text/plain',0,
                'DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD','{Timestamp}','{Timestamp}',1,1,0,NULL,NULL,NULL,0,0,0,0)
            """);
    }

    private static Task InsertReceiptsAsync(SqliteConnection connection) => ExecuteAsync(connection, $"""
        INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
        VALUES ('{Quest}','33333333-3333-3333-3333-333333333333',zeroblob(32),4,1,1,'44444444-4444-4444-4444-444444444444',2,'{Timestamp}');
        INSERT INTO guild_result_applications(RecipientQuestId,QuestId,ResultId,Digest,MutationId,AcceptedRecipientRevision,CreatedAtUtc)
        VALUES ('{Quest}','{Quest}','33333333-3333-3333-3333-333333333333',zeroblob(32),'55555555-5555-5555-5555-555555555555',2,'{Timestamp}');
        INSERT INTO guild_result_inputs(RecipientQuestId,QuestId,ResultId,Digest,CreatedAtUtc)
        VALUES ('{Quest}','{Quest}','33333333-3333-3333-3333-333333333333',zeroblob(32),'{Timestamp}');
        INSERT INTO guild_quest_start_intents(QuestId,CreatedAtUtc) VALUES ('{Quest}','{Timestamp}');
        """);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }
}
