using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildCohortStoreTests
{
    [Fact]
    public async Task Cohort_schema_installs_membership_contributions_and_mutation_receipts()
    {
        await using var connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('guild_cohorts','guild_cohort_members','guild_cohort_member_quests','guild_cohort_contributions','guild_cohort_mutations')";

        Assert.Equal(5L, await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task V11_upgrade_and_fresh_install_have_same_catalog()
    {
        Assert.Equal(GuildCanonicalSchemaVersionElevenFixture.PublishedFingerprint, GuildCanonicalSchemaVersionElevenFixture.Fingerprint);

        await using var upgraded = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, GuildCanonicalSchemaVersionElevenFixture.ChainSet(), 768, default);

        var result = await GrimoireSchemaTestInstaller.InstallAsync(upgraded, 768, default);

        Assert.True(result.GuildCanonical.IsHealthy, result.GuildCanonical.ToString());

        await using var fresh = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        var freshResult = await GrimoireSchemaTestInstaller.InstallAsync(fresh, 768, default);

        Assert.True(freshResult.GuildCanonical.IsHealthy, freshResult.GuildCanonical.ToString());

        await using var first = upgraded.CreateCommand();

        await using var second = fresh.CreateCommand();

        first.CommandText = second.CommandText = "SELECT SourceDefinitionFingerprint || InstalledCatalogFingerprint FROM grimoire_feature_schemas WHERE FamilyCode=2";

        Assert.Equal(await first.ExecuteScalarAsync(), await second.ExecuteScalarAsync());
    }
    [Fact]
    public async Task Terminal_Cohort_decision_cannot_be_rewritten_to_a_different_terminal_state()
    {
        await using var connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default)).GuildCanonical.IsHealthy);

        await using var command = connection.CreateCommand();

        command.CommandText = """
            PRAGMA foreign_keys=OFF;
            INSERT INTO guild_cohorts(Id,RootQuestId,IssuerQuestId,Name,PolicyCode,MimeType,GrantJson,AllocationAccountId,
                StateCode,Revision,MembershipVersion,DecisionCode)
            VALUES('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA','BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',
                'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB','panel',1,'text/plain','{}',
                'CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',4,1,0,1);
            """;

        _ = await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE guild_cohorts SET StateCode=5,Revision=Revision+1";

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }
    [Fact]
    public async Task Historical_association_lookup_seeks_by_exact_Quest()
    {
        await using var connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default)).GuildCanonical.IsHealthy);

        await using var command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN SELECT MemberId FROM guild_cohort_member_quests WHERE QuestId='AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA'";

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Assert.Contains("idx_guild_cohort_associations_quest", reader.GetString(3), StringComparison.Ordinal);
    }
}
