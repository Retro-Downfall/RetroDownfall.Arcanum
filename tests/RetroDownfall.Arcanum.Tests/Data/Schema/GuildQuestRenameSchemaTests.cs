using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildQuestRenameSchemaTests
{
    private const string RootSignet = "11111111-1111-1111-1111-111111111111";

    private const string RootQuest = "22222222-2222-2222-2222-222222222222";

    private const string ChildQuest = "33333333-3333-3333-3333-333333333333";

    // Frozen v2 canonical grant: its data graph does not contain a Commission selector.
    private const string GrantJson = "{\"workspaces\":[],\"tools\":[],\"memory\":[],\"budget\":{\"usd\":{\"mode\":\"finite\",\"value\":0},\"tokens\":{\"mode\":\"finite\",\"value\":0}},\"delegation\":[],\"expiresAtUtc\":null}";

    [Fact]
    public async Task Published_v2_Signet_lineage_upgrades_to_Quest_selectors_without_rewriting_authority_evidence()
    {
        await using SqliteConnection connection = await OpenLegacyLineageAsync();

        string[] before = await ReadSignetsAsync(connection);

        object? epoch = await ScalarAsync(connection, "SELECT Epoch FROM guild_signet_authority_state");

        var upgraded = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.True(upgraded.GuildCanonical.IsHealthy, upgraded.GuildCanonical.ToString());

        Assert.Equal(2L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_info('guild_signets') WHERE name IN('QuestId','RootQuestId')"));

        Assert.Equal(0L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_info('guild_signets') WHERE name IN('CommissionId','RootCommissionId')"));

        Assert.Equal(before, await ReadSignetsAsync(connection));

        Assert.Equal(epoch, await ScalarAsync(connection, "SELECT Epoch FROM guild_signet_authority_state"));

        Assert.Equal(RootQuest, await ScalarAsync(connection, "SELECT RootQuestId FROM guild_signets WHERE QuestId='" + ChildQuest + "'"));

        Assert.Equal(GrantJson, await ScalarAsync(connection, "SELECT GrantJson FROM guild_signets WHERE QuestId='" + RootQuest + "'"));

        await using SqliteConnection fresh = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(fresh, 768, CancellationToken.None);

        const string fingerprints = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

        Assert.Equal(await ScalarAsync(fresh, fingerprints), await ScalarAsync(connection, fingerprints));

        var reopened = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.True(reopened.GuildCanonical.IsHealthy, reopened.GuildCanonical.ToString());

        Assert.Equal(before, await ReadSignetsAsync(connection));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE guild_signets SET QuestId='AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',Revision=Revision+1 WHERE SignetId='" + RootSignet + "'"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "UPDATE guild_signets SET Revoked=0,Revision=Revision+1 WHERE QuestId='" + ChildQuest + "'"));
    }

    private static async Task<SqliteConnection> OpenLegacyLineageAsync()
    {
        SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(connection,
            GuildCanonicalSchemaVersionTwoFixture.ChainSet(), 768, CancellationToken.None);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        await using SqliteCommand seed = connection.CreateCommand();

        seed.CommandText = """
            INSERT INTO guild_signets(SignetId,AgentId,RunId,CommissionId,RootCommissionId,IssuerSignetId,
                GrantJson,IssuedAtUtc,OriginPrincipalId,InstallationGeneration,AuthorityEpoch,Revision,
                CredentialGeneration,TokenDigest,RevocationGeneration,Revoked,RestoredInert,Retired,CancellationRequested)
            SELECT $signet,'44444444-4444-4444-4444-444444444444',$run,$quest,$root,$issuer,
                $grant,'2026-11-06T00:00:00.0000000Z','55555555-5555-5555-5555-555555555555',
                '66666666-6666-6666-6666-666666666666',Epoch,$revision,$credential,$digest,$revocation,$denied,$denied,$denied,$denied
            FROM guild_signet_authority_state WHERE StateKey=1
            """;

        seed.Parameters.AddWithValue("$signet", RootSignet);

        seed.Parameters.AddWithValue("$run", "77777777-7777-7777-7777-777777777777");

        seed.Parameters.AddWithValue("$quest", RootQuest);

        seed.Parameters.AddWithValue("$root", RootQuest);

        seed.Parameters.AddWithValue("$issuer", DBNull.Value);

        seed.Parameters.AddWithValue("$grant", GrantJson);

        seed.Parameters.AddWithValue("$revision", 3);

        seed.Parameters.AddWithValue("$credential", 2);

        seed.Parameters.AddWithValue("$digest", Enumerable.Repeat((byte)0xA5, 32).ToArray());

        seed.Parameters.AddWithValue("$revocation", 0);

        seed.Parameters.AddWithValue("$denied", 0);

        Assert.Equal(1, await seed.ExecuteNonQueryAsync());

        seed.Parameters["$signet"].Value = "88888888-8888-8888-8888-888888888888";

        seed.Parameters["$run"].Value = "99999999-9999-9999-9999-999999999999";

        seed.Parameters["$quest"].Value = ChildQuest;

        seed.Parameters["$issuer"].Value = RootSignet;

        seed.Parameters["$revision"].Value = 7;

        seed.Parameters["$credential"].Value = 4;

        seed.Parameters["$digest"].Value = Enumerable.Repeat((byte)0x5A, 32).ToArray();

        seed.Parameters["$revocation"].Value = 2;

        seed.Parameters["$denied"].Value = 1;

        Assert.Equal(1, await seed.ExecuteNonQueryAsync());

        // Reopening the frozen head proves both rows are accepted legacy data before migration.
        var reopened = await GrimoireSchemaTestInstaller.InstallAsync(connection,
            GuildCanonicalSchemaVersionTwoFixture.ChainSet(), 768, CancellationToken.None);

        Assert.True(reopened.GuildCanonical.IsHealthy, reopened.GuildCanonical.ToString());

        return connection;
    }

    private static async Task<string[]> ReadSignetsAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT * FROM guild_signets ORDER BY SignetId";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> rows = [];

        while (await reader.ReadAsync())
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.GetValue(index) is byte[] bytes ? Convert.ToHexString(bytes) : reader.GetValue(index).ToString())));
        }

        return rows.ToArray();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }
}
