using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Data.Schema;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildQuestLifecycleTests
{
    [Fact]
    public async Task Protected_goal_erasure_removes_body_and_label_while_uncertain_dispatch_evidence_survives()
    {
        await using Fixture fixture = await Fixture.CreateAsync(protectedGoal: true);

        await fixture.ExecuteAsync("""
            UPDATE guild_quests SET AccountingStateCode=2,Revision=Revision+1;
            INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc)
            VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',zeroblob(32),0,'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,1,'2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:00.0000000Z');
            """);

        await using (SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false))
        {
            var erased = await BackupRestoreProtectedStatePurger.PurgeStagedAsync(fixture.Connection, transaction,
                CovenantSqliteConnectionInitializer.Instance, TimeProvider.System, CancellationToken.None);

            Assert.True(erased.IsSuccess, erased.Error.Message);

            await transaction.CommitAsync();
        }

        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM guild_content WHERE ContentKindCode=1"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM guild_quests WHERE Name IS NULL AND AccountingStateCode=2"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM guild_dispatches WHERE EvidenceCode=2"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM guild_content_erasure_receipts WHERE ContentKindCode=1"));
    }

    [Fact]
    public async Task Retired_owner_binding_denies_reused_owner_ids_after_event_cleanup()
    {
        Guid session = Guid.NewGuid();

        await using Fixture fixture = await Fixture.CreateAsync(session);

        await fixture.ExecuteAsync("UPDATE guild_quests SET Retired=1,CancellationRequested=1,Revision=Revision+1");

        await fixture.ExecuteAsync($"INSERT INTO guild_owner_cleanup(OwnerKindCode,OwnerId,PhaseCode,CreatedAtUtc,UpdatedAtUtc) VALUES(2,'{session.ToString("D").ToUpperInvariant()}',6,'2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:00.0000000Z')");

        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM owner_deletion_events"));

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        var owner = await GuildStore.RequireOwnerWithinAsync(fixture.Connection, transaction, session, null, CancellationToken.None);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, owner.Error.Code);
    }

    [Fact]
    public async Task Deleted_Session_denial_preserves_its_live_Campaign_and_unrelated_Session()
    {
        Guid deleted = Guid.NewGuid();

        Guid campaign = Guid.NewGuid();

        Guid live = Guid.NewGuid();

        await using Fixture fixture = await Fixture.CreateAsync(deleted, campaignId: campaign);

        await fixture.ExecuteAsync($"""
            UPDATE guild_quests SET Retired=1,CancellationRequested=1,Revision=Revision+1;
            INSERT INTO guild_owner_cleanup(OwnerKindCode,OwnerId,PhaseCode,CreatedAtUtc,UpdatedAtUtc)
            VALUES(2,'{deleted.ToString("D").ToUpperInvariant()}',6,'2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:00.0000000Z');
            INSERT INTO Sessions(Id,CampaignId,CreatedAt,UpdatedAt)
            VALUES('{live.ToString("D").ToUpperInvariant()}','{campaign.ToString("D").ToUpperInvariant()}','2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:00.0000000Z');
            """);

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        var owner = await GuildStore.RequireOwnerWithinAsync(fixture.Connection, transaction, live, campaign, CancellationToken.None);

        Assert.True(owner.IsSuccess, owner.Error.Message);
    }

    [Fact]
    public async Task Goal_erasure_can_remove_its_sensitive_label_but_cannot_restore_or_replace_it()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.ExecuteAsync("UPDATE guild_quests SET Name=NULL,Revision=Revision+1");

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM guild_quests WHERE Name IS NULL"));

        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync("UPDATE guild_quests SET Name='resurrected label',Revision=Revision+1"));
    }

    [Fact]
    public async Task A_current_Guild_catalog_requires_every_Quest_table()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.ExecuteAsync("DROP TABLE guild_quest_start_intents");

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        await Assert.ThrowsAsync<GuildStorageIntegrityException>(() => GuildStorageIntegrity.ValidateAsync(fixture.Connection, transaction, CancellationToken.None));
    }

    [Theory]
    [InlineData("UPDATE LongRunningOperations SET Kind='other-work'")]
    [InlineData("UPDATE LongRunningOperations SET CheckpointPayload=X'7B7D'")]
    [InlineData("DELETE FROM guild_signets")]
    [InlineData("DELETE FROM guild_content WHERE ContentKindCode=1")]
    [InlineData("UPDATE LongRunningOperations SET State=4")]
    public async Task Quest_admission_integrity_refuses_missing_or_changed_cross_family_evidence(string corruption)
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.ExecuteAsync(corruption);

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        await Assert.ThrowsAsync<GuildStorageIntegrityException>(() => GuildStorageIntegrity.ValidateAsync(fixture.Connection, transaction, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_receipt_requires_matching_delivery_intent_and_parent_relationship(bool wrongRecipient)
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.SeedTerminalAsync();

        if (wrongRecipient)
        {
            await fixture.ExecuteAsync("""
                INSERT INTO guild_result_outbox(QuestId,ResultId,RecipientQuestId,Digest,DeliveryStateCode,Revision,CreatedAtUtc)
                VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','11111111-1111-1111-1111-111111111111',zeroblob(32),1,1,'2026-11-06T00:00:00.0000000Z');
                """);
        }

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        await Assert.ThrowsAsync<GuildStorageIntegrityException>(() => GuildStorageIntegrity.ValidateAsync(fixture.Connection, transaction, CancellationToken.None));
    }

    [Fact]
    public async Task Terminal_receipt_cannot_contradict_the_ledgers_failure_code()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.SeedTerminalAsync();

        await fixture.ExecuteAsync("""
            INSERT INTO guild_result_outbox(QuestId,ResultId,Digest,DeliveryStateCode,Revision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',zeroblob(32),1,1,'2026-11-06T00:00:00.0000000Z');
            UPDATE LongRunningOperations SET TerminalErrorCode='operation.cancelled';
            """);

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        await Assert.ThrowsAsync<GuildStorageIntegrityException>(() => GuildStorageIntegrity.ValidateAsync(fixture.Connection, transaction, CancellationToken.None));
    }

    [Fact]
    public async Task Accepted_answer_intent_digest_is_independent_of_its_body_digest()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.ExecuteAsync("""
            UPDATE guild_quests SET Revision=2;
            INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
            VALUES('FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',3,'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',1,X'E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855','text/plain',X'',0,0,'2026-11-06T00:00:00.0000000Z');
            INSERT INTO guild_continuations(QuestId,ContinuationId,MutationId,Digest,ContentId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE','33333333-3333-3333-3333-333333333333',zeroblob(32),'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,'2026-11-06T00:00:00.0000000Z');
            """);

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        await GuildStorageIntegrity.ValidateAsync(fixture.Connection, transaction, CancellationToken.None);
    }

    [Fact]
    public async Task Exact_erasure_receipt_keeps_a_Quest_valid_without_its_goal_body()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.ExecuteAsync("""
            INSERT INTO guild_content_erasure_receipts(ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,ByteLength,ErasedAtUtc)
            SELECT ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,length(Body),CreatedAtUtc FROM guild_content WHERE ContentKindCode=1;
            DELETE FROM guild_content WHERE ContentKindCode=1;
            UPDATE guild_quests SET Name=NULL,Revision=Revision+1;
            """);

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM guild_quests"));

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        await GuildStorageIntegrity.ValidateAsync(fixture.Connection, transaction, CancellationToken.None);
    }

    internal sealed class Fixture(SqliteConnection connection) : IAsyncDisposable
    {
        internal SqliteConnection Connection => connection;

        internal static async Task<Fixture> CreateAsync(Guid? sessionId = null, bool protectedGoal = false, Guid? campaignId = null,
            GrimoireSchemaVersionChainSet? chains = null)
        {
            SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, chains ?? GrimoireSchemaVersionChains.Default, 768, CancellationToken.None);

            Fixture fixture = new(connection);

            string campaign = campaignId is { } bound ? "'" + bound.ToString("D").ToUpperInvariant() + "'" : "NULL";

            if (campaignId is not null)
            {
                await fixture.ExecuteAsync($"INSERT INTO Campaigns(Id,Name,NameLower,Path,Type,Settings,CreatedAt,UpdatedAt) VALUES({campaign},'lifecycle','lifecycle','/tmp/lifecycle',0,'{{}}','2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:00.0000000Z')");
            }

            if (sessionId is { } owner)
            {
                await fixture.ExecuteAsync($"INSERT INTO Sessions(Id,CampaignId,CreatedAt,UpdatedAt) VALUES('{owner.ToString("D").ToUpperInvariant()}',{campaign},'2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:00.0000000Z')");
            }

            await GuildQuestSchemaTests.SeedQuestAsync(connection, sessionId, SHA256.HashData([]), campaignId);

            await using SqliteCommand command = connection.CreateCommand();

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            command.Transaction = transaction;

            command.CommandText = """
                UPDATE LongRunningOperations SET RecoveryPolicy=2,CheckpointPayload=$checkpoint;
                INSERT INTO guild_content(ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,ExactGenerationIds,CreatedAtUtc)
                VALUES('CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',$session,$campaign,1,'11111111-1111-1111-1111-111111111111',1,$digest,'text/plain',X'',$sensitivity,$mode,$generations,'2000-01-01T00:00:00.0000000Z');
                INSERT INTO guild_signets(SignetId,AgentId,RunId,QuestId,RootQuestId,GrantJson,IssuedAtUtc,OriginPrincipalId,
                    InstallationGeneration,AuthorityEpoch,SessionId,CampaignId,Revision,CredentialGeneration,TokenDigest,RevocationGeneration,Revoked,RestoredInert,Retired)
                SELECT 'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB','88888888-8888-8888-8888-888888888888','AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                    '11111111-1111-1111-1111-111111111111','11111111-1111-1111-1111-111111111111',$grant,'2026-11-06T00:00:00.0000000Z',
                    '44444444-4444-4444-4444-444444444444','33333333-3333-3333-3333-333333333333',Epoch,$session,$campaign,1,1,zeroblob(32),0,0,0,0
                FROM guild_signet_authority_state WHERE StateKey=1;
                """;

            command.Parameters.AddWithValue("$checkpoint", GuildQuestCheckpoint.Encode(Guid.Parse("11111111-1111-1111-1111-111111111111")));

            command.Parameters.AddWithValue("$digest", SHA256.HashData([]));

            command.Parameters.AddWithValue("$session", sessionId is { } id ? id.ToString("D").ToUpperInvariant() : DBNull.Value);

            command.Parameters.AddWithValue("$campaign", campaignId is { } campaignOwner ? campaignOwner.ToString("D").ToUpperInvariant() : DBNull.Value);

            GenerationProvenance provenance = GenerationProvenance.Create([CovenantOperationGateFixture.DatasetGeneration]);

            command.Parameters.AddWithValue("$sensitivity", protectedGoal ? 1 : 0);

            command.Parameters.AddWithValue("$mode", protectedGoal ? 1 : 0);

            command.Parameters.AddWithValue("$generations", protectedGoal ? provenance.ToCanonicalExactBytes() : DBNull.Value);

            command.Parameters.AddWithValue("$grant", GuildSignetGrantJson.Encode(new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null)).Value);

            _ = await command.ExecuteNonQueryAsync();

            if (protectedGoal)
            {
                var labelled = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction,
                    new(SensitiveArtifactKind.GuildContent, Guid.Parse("CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC"), sessionId, campaignId, null,
                        1, new(SHA256.HashData([])), ContentSensitivity.CovenantDerived, provenance), CancellationToken.None);

                Assert.True(labelled.IsSuccess, labelled.Error.Message);
            }

            await transaction.CommitAsync();

            return fixture;
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            _ = await command.ExecuteNonQueryAsync();
        }

        internal Task SeedTerminalAsync() => ExecuteAsync("""
            UPDATE guild_quests SET Revision=2;
            UPDATE LongRunningOperations SET State=5,TerminalErrorCode='fixture.failure';
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,ReasonCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',zeroblob(32),4,'fixture.failure',1,1,'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,'2026-11-06T00:00:00.0000000Z');
            """);

        internal async Task<object?> ScalarAsync(string sql)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            return await command.ExecuteScalarAsync();
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
