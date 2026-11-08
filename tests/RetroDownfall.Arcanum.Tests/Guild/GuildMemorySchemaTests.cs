using Xunit.Abstractions;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildMemorySchemaTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Published_five_upgrades_without_changing_authority_epoch_or_inventing_attribution()
    {
        Assert.Equal(GuildCanonicalSchemaVersionFiveFixture.PublishedFingerprint, GuildCanonicalSchemaVersionFiveFixture.Fingerprint);

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        var original = await GrimoireSchemaTestInstaller.InstallAsync(connection, GuildCanonicalSchemaVersionFiveFixture.ChainSet(), 768, default);

        Assert.True(original.GuildCanonical.IsHealthy);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT Epoch FROM guild_signet_authority_state";

        object? epoch = await command.ExecuteScalarAsync();

        var upgraded = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        Assert.True(upgraded.GuildCanonical.IsHealthy, upgraded.GuildCanonical.ToString());

        Assert.Equal(8, upgraded.GuildCanonical.SchemaVersion);

        Assert.Equal(epoch, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT count(*) FROM guild_memory_attribution";

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Attribution_is_installed_empty_without_inventing_authorship_for_old_content()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        output.WriteLine(RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.GuildCanonicalSchemaFingerprint);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE name='guild_memory_attribution' AND type='table'"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));
    }
}
