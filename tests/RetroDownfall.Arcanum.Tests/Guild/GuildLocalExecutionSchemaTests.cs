using Microsoft.Data.Sqlite;
using Xunit.Abstractions;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildLocalExecutionSchemaTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Installed_schema_accepts_execution_input_and_has_immutable_local_binding()
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        output.WriteLine(GrimoireSchemaCatalog.GuildCanonicalSchemaFingerprint);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='guild_local_executions' AND type='table'";

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        command.CommandText = """
            INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,
                SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
            VALUES($id,9,$source,1,zeroblob(32),'application/json',X'7B7D',0,0,'2026-11-08T00:00:00.0000000Z');
            """;

        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$source", Guid.NewGuid().ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task V8_upgrade_preserves_content_and_erasure_evidence_and_converges_with_fresh_schema()
    {
        Assert.Equal(GuildCanonicalSchemaVersionEightFixture.PublishedFingerprint, GuildCanonicalSchemaVersionEightFixture.Fingerprint);

        await using SqliteConnection upgraded = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        var original = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, GuildCanonicalSchemaVersionEightFixture.ChainSet(), 768, default);

        Assert.True(original.GuildCanonical.IsHealthy, original.GuildCanonical.ToString());

        await using SqliteCommand command = upgraded.CreateCommand();

        command.CommandText = """
            INSERT INTO guild_content(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Body,
                SensitivityCode,ProvenanceModeCode,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111',8,'22222222-2222-2222-2222-222222222222',1,
                zeroblob(32),'text/plain',X'68656C6C6F',0,0,'2026-11-08T00:00:00.0000000Z');
            INSERT INTO guild_content_erasure_receipts(ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,ByteLength,ErasedAtUtc)
            VALUES('33333333-3333-3333-3333-333333333333',8,'44444444-4444-4444-4444-444444444444',1,
                zeroblob(32),'text/plain',5,'2026-11-08T00:00:00.0000000Z');
            """;

        await command.ExecuteNonQueryAsync();

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        command.CommandText = "SELECT hex(Body) FROM guild_content";

        Assert.Equal("68656C6C6F", await command.ExecuteScalarAsync());

        command.CommandText = "SELECT count(*) FROM guild_content_erasure_receipts";

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

        object? migrated = await command.ExecuteScalarAsync();

        await using SqliteConnection fresh = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        await GrimoireSchemaTestInstaller.InstallAsync(fresh, 768, default);

        await using SqliteCommand freshCatalog = fresh.CreateCommand();

        freshCatalog.CommandText = command.CommandText;

        Assert.Equal(migrated, await freshCatalog.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Cancellation_intent_has_an_immutable_durable_receipt()
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='guild_quest_cancellations' AND type='table'";

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        command.CommandText = """
            INSERT INTO guild_quest_cancellations(QuestId,IdempotencyKey,CanonicalRequestDigest,ExpectedRevision,
                ReasonCode,AcceptedRevision,AcceptedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222',zeroblob(32),1,0,2,
                '2026-11-08T00:00:00.0000000Z');
            """;

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        command.CommandText = "UPDATE guild_quest_cancellations SET ReasonCode=1";

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }
}
