using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildPortalSchemaTests
{
    private const string Quest = "11111111-1111-1111-1111-111111111111";

    private const string Stream = "22222222-2222-2222-2222-222222222222";

    private const string Envelope = "33333333-3333-3333-3333-333333333333";

    private const string Inbox = "44444444-4444-4444-4444-444444444444";

    private const string PublishedGrant = "{\"workspaces\":[],\"tools\":[],\"memory\":[],\"budget\":{\"usd\":{\"mode\":\"finite\",\"value\":0},\"tokens\":{\"mode\":\"finite\",\"value\":0}},\"delegation\":[],\"expiresAtUtc\":null}";

    private const string Metadata = "{}";

    private const string Timestamp = "2026-11-06T00:00:00.0000000Z";

    [Fact]
    public async Task Published_v3_upgrades_to_v4_without_altering_Quest_evidence_Core_or_authority_epoch()
    {
        Assert.Equal(GuildCanonicalSchemaVersionThreeFixture.PublishedFingerprint, GuildCanonicalSchemaVersionThreeFixture.Fingerprint);

        await using SqliteConnection upgraded = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, GuildCanonicalSchemaVersionThreeFixture.ChainSet(), 768, CancellationToken.None);

        await GuildQuestSchemaTests.SeedQuestAsync(upgraded, goalDigest: SHA256.HashData([]));

        await ExecuteAsync(upgraded, $"""
            INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
            SELECT GoalContentId,1,Id,GoalRevision,GoalDigest,GoalMimeType,x'',0,0,CreatedAtUtc FROM guild_quests;
            INSERT INTO guild_signets(SignetId,AgentId,RunId,QuestId,RootQuestId,IssuerSignetId,
                GrantJson,IssuedAtUtc,OriginPrincipalId,InstallationGeneration,AuthorityEpoch,Revision,
                CredentialGeneration,TokenDigest,RevocationGeneration,Revoked,RestoredInert,Retired,CancellationRequested)
            SELECT SignetId,AgentId,RunId,Id,RootQuestId,NULL,'{PublishedGrant}',CreatedAtUtc,IssuerIdentity,
                InstallationGeneration,Epoch,1,1,zeroblob(32),0,0,0,0,0
            FROM guild_quests CROSS JOIN guild_signet_authority_state;
            """);

        GrimoireSchemaInstallResult legacy = await GrimoireSchemaTestInstaller.InstallAsync(upgraded,
            GuildCanonicalSchemaVersionThreeFixture.ChainSet(), 768, CancellationToken.None);

        Assert.True(legacy.GuildCanonical.IsHealthy, legacy.GuildCanonical.ToString());

        object? core = await ScalarAsync(upgraded, "SELECT SourceDefinitionFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=0");

        object? epoch = await ScalarAsync(upgraded, "SELECT Epoch FROM guild_signet_authority_state");

        object? quest = await ScalarAsync(upgraded, "SELECT hex(IntentDigest) || GoalContentId || Revision FROM guild_quests");

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, 768, CancellationToken.None);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        Assert.Equal(7, installed.GuildCanonical.SchemaVersion);

        await using SqliteConnection fresh = await OpenAsync();

        Assert.Equal(await ScalarAsync(fresh, "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2"),
            await ScalarAsync(upgraded, "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2"));

        Assert.Equal(core, await ScalarAsync(upgraded, "SELECT SourceDefinitionFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=0"));

        Assert.Equal(epoch, await ScalarAsync(upgraded, "SELECT Epoch FROM guild_signet_authority_state"));

        Assert.Equal(quest, await ScalarAsync(upgraded, "SELECT hex(IntentDigest) || GoalContentId || Revision FROM guild_quests"));

        await SeedAsync(upgraded);
    }

    [Fact]
    public async Task Distinct_source_kinds_can_share_object_identity_and_revision_but_each_source_remains_unique()
    {
        await using SqliteConnection connection = await OpenAsync();

        await ExecuteAsync(connection, $"""
            INSERT INTO guild_event_streams VALUES ('{Quest}',3,0);
            INSERT INTO guild_events(RootQuestId,EventSequence,EventKindCode,ObjectId,Revision,QuestId,CreatedAtUtc)
            VALUES ('{Quest}',1,0,'{Quest}',1,'{Quest}','{Timestamp}');
            INSERT INTO guild_events(RootQuestId,EventSequence,EventKindCode,ObjectId,Revision,QuestId,CreatedAtUtc,SourceKindCode)
            VALUES ('{Quest}',2,0,'{Quest}',1,'{Quest}','{Timestamp}',1);
            INSERT INTO guild_events(RootQuestId,EventSequence,EventKindCode,ObjectId,Revision,QuestId,CreatedAtUtc,SourceKindCode)
            VALUES ('{Quest}',3,0,'{Quest}',1,'{Quest}','{Timestamp}',2);
            """);

        Assert.Equal(3L, await ScalarAsync(connection, "SELECT COUNT(*) FROM guild_events"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT SourceKindCode FROM guild_events WHERE EventSequence=1"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"""
            INSERT INTO guild_events(RootQuestId,EventSequence,EventKindCode,ObjectId,Revision,QuestId,CreatedAtUtc,SourceKindCode)
            VALUES ('{Quest}',4,0,'{Quest}',1,'{Quest}','{Timestamp}',1)
            """));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"""
            INSERT INTO guild_events(RootQuestId,EventSequence,EventKindCode,ObjectId,Revision,QuestId,CreatedAtUtc,SourceKindCode)
            VALUES ('{Quest}',4,0,'{Quest}',2,'{Quest}','{Timestamp}',7)
            """));
    }

    [Fact]
    public async Task Sender_sequences_and_publish_keys_are_unique_without_requiring_retained_content()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedAsync(connection);

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM guild_envelopes"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            $"INSERT INTO guild_portal_streams VALUES ('55555555-5555-5555-5555-555555555555','{Quest}',0,'producer',0)"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "INSERT INTO guild_envelopes SELECT '55555555-5555-5555-5555-555555555555',StreamId,ProducerRequestId,IntentDigest,QuestId,RootQuestId,SessionId,CampaignId,KindCode,ContentId,SenderSequence,MetadataJson,CreatedAtUtc FROM guild_envelopes"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "INSERT INTO guild_envelopes SELECT '55555555-5555-5555-5555-555555555555',StreamId,'second',IntentDigest,QuestId,RootQuestId,SessionId,CampaignId,KindCode,ContentId,SenderSequence,MetadataJson,CreatedAtUtc FROM guild_envelopes"));
    }

    [Theory]
    [InlineData("guild_portal_streams", "LastSequence=-1")]
    [InlineData("guild_portal_streams", "ProducerKindCode=3")]
    [InlineData("guild_portal_streams", "ProducerKey=printf('%4097s','x')")]
    [InlineData("guild_envelopes", "IntentDigest=zeroblob(31)")]
    [InlineData("guild_envelopes", "SenderSequence=0")]
    [InlineData("guild_envelopes", "KindCode=6")]
    [InlineData("guild_envelopes", "MetadataJson=printf('%16385s','x')")]
    [InlineData("guild_inbox", "RecipientKey=printf('%257s','x')")]
    [InlineData("guild_inbox", "DeliveryStateCode=4")]
    [InlineData("guild_inbox", "Revision=0")]
    public async Task Invalid_or_rewritten_accepted_identity_is_refused(string table, string mutation)
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedAsync(connection);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"UPDATE {table} SET {mutation}"));
    }

    [Theory]
    [InlineData("guild_portal_streams", "ProducerKindCode", "3")]
    [InlineData("guild_portal_streams", "LastSequence", "-1")]
    [InlineData("guild_envelopes", "KindCode", "6")]
    [InlineData("guild_envelopes", "SenderSequence", "0")]
    [InlineData("guild_envelopes", "IntentDigest", "zeroblob(31)")]
    [InlineData("guild_envelopes", "MetadataJson", "printf('%16385s','x')")]
    [InlineData("guild_inbox", "RecipientKey", "printf('%257s','x')")]
    [InlineData("guild_inbox", "DeliveryStateCode", "4")]
    [InlineData("guild_inbox", "Revision", "0")]
    public async Task New_rows_enforce_closed_enums_positive_sequences_and_representation_bounds(string table, string column, string value)
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedAsync(connection);

        await using SqliteCommand columns = connection.CreateCommand();

        columns.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid";

        await using SqliteDataReader reader = await columns.ExecuteReaderAsync();

        List<string> selections = [];

        while (await reader.ReadAsync())
        {
            string name = reader.GetString(0);

            selections.Add(name == column ? value : (name == "Id" || (name == "StreamId" && table == "guild_portal_streams"))
                ? "'77777777-7777-7777-7777-777777777777'"
                : name == "ProducerRequestId" ? "'different-request'"
                : name == "ProducerKey" ? "'different-producer'"
                : name == "RecipientKey" ? "'different-recipient'"
                : name == "SenderSequence" && table == "guild_envelopes" ? "2" : name);
        }

        await reader.DisposeAsync();

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            $"INSERT INTO {table} SELECT {string.Join(',', selections)} FROM {table}"));
    }

    [Fact]
    public async Task Inbox_is_fenced_by_revision_and_cannot_change_stream_or_regress_acknowledgement()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedAsync(connection);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_inbox SET Revision=3"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_inbox SET SenderSequence=2,Revision=2"));

        await ExecuteAsync(connection, $"UPDATE guild_inbox SET DeliveryStateCode=2,Revision=2,AcknowledgedAtUtc='{Timestamp}'");

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_inbox SET DeliveryStateCode=1,Revision=3,AcknowledgedAtUtc=NULL"));
    }

    [Fact]
    public async Task Application_inputs_require_the_exact_committed_inbox_and_application_receipt()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedAsync(connection);

        await Assert.ThrowsAsync<SqliteException>(() => InsertInputAsync(connection));

        await ExecuteAsync(connection, $"INSERT INTO guild_portal_receipts VALUES ('{Inbox}','55555555-5555-5555-5555-555555555555',zeroblob(32),0,'{Timestamp}')");

        await InsertInputAsync(connection);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_portal_receipts SET Digest=randomblob(32)"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_portal_inputs SET RecipientQuestId='66666666-6666-6666-6666-666666666666'"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM guild_portal_receipts"));
    }

    [Fact]
    public async Task Event_cursors_are_content_free_and_bound_to_durable_authority_and_snapshot()
    {
        await using SqliteConnection connection = await OpenAsync();

        await SeedAsync(connection);

        await ExecuteAsync(connection, $"""
            INSERT INTO guild_event_streams VALUES ('{Quest}',1,0);
            INSERT INTO guild_events(RootQuestId,EventSequence,EventKindCode,ObjectId,Revision,QuestId,EnvelopeId,CreatedAtUtc)
            VALUES ('{Quest}',1,1,'{Envelope}',1,'{Quest}','{Envelope}','{Timestamp}');
            INSERT INTO guild_portal_cursors VALUES ('{Inbox}','{Quest}','caller','{Quest}','root',0,1,'{Timestamp}','{Quest}','visibility');
            """);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_events SET Revision=2"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_event_streams SET LastSequence=0"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_event_streams SET RetainedAfterSequence=2"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE guild_portal_cursors SET CallerKey='another-caller'"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('guild_events') WHERE name IN('Body','MetadataJson','ContentId')"));
    }

    [Theory]
    [InlineData("SELECT Id FROM guild_inbox WHERE RecipientKey='recipient' AND StreamId='22222222-2222-2222-2222-222222222222' AND SenderSequence>0 ORDER BY SenderSequence LIMIT 1", "idx_guild_inbox_recipient_stream")]
    [InlineData("SELECT Id FROM guild_envelopes WHERE ContentId='99999999-9999-9999-9999-999999999999'", "idx_guild_envelopes_content")]
    public async Task Ordering_and_content_reference_lookups_seek_indexes(string query, string index)
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + query;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Assert.Contains("SEARCH", reader.GetString(3), StringComparison.Ordinal);

        Assert.Contains(index, reader.GetString(3), StringComparison.Ordinal);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        return connection;
    }

    private static Task SeedAsync(SqliteConnection connection) => ExecuteAsync(connection, $"""
        INSERT INTO guild_portal_streams VALUES ('{Stream}','{Quest}',0,'producer',1);
        INSERT INTO guild_envelopes VALUES ('{Envelope}','{Stream}','request',zeroblob(32),'{Quest}','{Quest}',NULL,NULL,1,'99999999-9999-9999-9999-999999999999',1,'{Metadata}','{Timestamp}');
        INSERT INTO guild_inbox VALUES ('{Inbox}','{Envelope}','recipient','{Quest}','{Stream}',1,1,1,NULL,NULL,NULL,NULL,'{Timestamp}',NULL);
        """);

    private static Task InsertInputAsync(SqliteConnection connection) => ExecuteAsync(connection,
        $"INSERT INTO guild_portal_inputs VALUES ('{Inbox}','{Quest}','{Envelope}','{Timestamp}')");

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
