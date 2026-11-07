using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildContinuationSchemaTests
{
    [Fact]
    public async Task Published_v4_upgrade_preserves_accepted_rows_as_unknown_and_keeps_them_immutable()
    {
        Assert.Equal(GuildCanonicalSchemaVersionFourFixture.PublishedFingerprint, GuildCanonicalSchemaVersionFourFixture.Fingerprint);

        await using SqliteConnection old = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(old, GuildCanonicalSchemaVersionFourFixture.ChainSet(), 768, default);

        await GuildQuestSchemaTests.SeedQuestAsync(old, goalDigest: SHA256.HashData([]));

        await ExecuteAsync(old, """
            INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
            SELECT GoalContentId,1,Id,GoalRevision,GoalDigest,GoalMimeType,x'',0,0,CreatedAtUtc FROM guild_quests;
            INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
            SELECT '99999999-9999-9999-9999-999999999999',3,'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',1,GoalDigest,'text/plain',x'',0,0,CreatedAtUtc FROM guild_quests;
            INSERT INTO guild_signets(SignetId,AgentId,RunId,QuestId,RootQuestId,IssuerSignetId,
                GrantJson,IssuedAtUtc,OriginPrincipalId,InstallationGeneration,AuthorityEpoch,Revision,
                CredentialGeneration,TokenDigest,RevocationGeneration,Revoked,RestoredInert,Retired,CancellationRequested)
            SELECT SignetId,AgentId,RunId,Id,RootQuestId,NULL,
                '{"workspaces":[],"tools":[],"memory":[],"budget":{"usd":{"mode":"finite","value":0},"tokens":{"mode":"finite","value":0}},"delegation":[],"expiresAtUtc":null}',
                CreatedAtUtc,IssuerIdentity,InstallationGeneration,Epoch,1,1,zeroblob(32),0,0,0,0,0
            FROM guild_quests CROSS JOIN guild_signet_authority_state;
            UPDATE guild_quests SET Revision=2;
            INSERT INTO guild_continuations(QuestId,ContinuationId,MutationId,Digest,ContentId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',
                'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',zeroblob(32),'99999999-9999-9999-9999-999999999999',2,'2026-11-06T00:00:00.0000000Z');
            """);

        const string evidence = "SELECT QuestId || ContinuationId || MutationId || hex(Digest) || ContentId || AcceptedRevision || CreatedAtUtc FROM guild_continuations";

        object? before = await ScalarAsync(old, evidence);

        object? epoch = await ScalarAsync(old, "SELECT Epoch FROM guild_signet_authority_state");

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(old, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        Assert.Equal(before, await ScalarAsync(old, evidence));

        Assert.Equal(epoch, await ScalarAsync(old, "SELECT Epoch FROM guild_signet_authority_state"));

        Assert.Equal(1L, await ScalarAsync(old, "SELECT COUNT(*) FROM guild_continuations WHERE AuthorityCode=0 AND ProducerAgentId IS NULL AND ProducerRunId IS NULL AND ProducerQuestId IS NULL AND OperatorPrincipalId IS NULL"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(old, "UPDATE guild_continuations SET AuthorityCode=1,OperatorPrincipalId='11111111-1111-1111-1111-111111111111'"));

        await using SqliteConnection fresh = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(fresh, 768, default);

        const string fingerprints = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

        Assert.Equal(await ScalarAsync(fresh, fingerprints), await ScalarAsync(old, fingerprints));
    }

    [Theory]
    [InlineData("1,NULL,NULL,NULL,NULL")]
    [InlineData("2,NULL,NULL,NULL,NULL")]
    [InlineData("0,NULL,NULL,NULL,'11111111-1111-1111-1111-111111111111'")]
    [InlineData("3,NULL,NULL,NULL,NULL")]
    [InlineData("1,NULL,NULL,NULL,'not-an-operator'")]
    public async Task Continuation_provenance_variants_cannot_be_partial_or_forged(string provenance)
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        await GuildQuestSchemaTests.SeedQuestAsync(connection);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, $"""
            INSERT INTO guild_continuations(QuestId,ContinuationId,MutationId,Digest,ContentId,AcceptedRevision,CreatedAtUtc,
                AuthorityCode,ProducerAgentId,ProducerRunId,ProducerQuestId,OperatorPrincipalId)
            VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',
                'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',zeroblob(32),'99999999-9999-9999-9999-999999999999',2,'2026-11-06T00:00:00.0000000Z',
                {provenance})
            """));
    }

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
