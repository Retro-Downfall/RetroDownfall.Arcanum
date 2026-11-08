using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GuildSignetSchemaTests
{
    [Fact]
    public async Task Fresh_install_creates_digest_authority_and_epoch_without_changing_Core()
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.Equal(38, installed.Core.SchemaVersion);

        Assert.Equal(9, installed.GuildCanonical.SchemaVersion);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN('guild_signets','guild_signet_revocations','guild_signet_authority_state')";

        Assert.Equal(3L, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT Epoch FROM guild_signet_authority_state WHERE StateKey=1";

        Assert.True(Guid.TryParseExact((string)(await command.ExecuteScalarAsync())!, "D", out Guid epoch));

        Assert.NotEqual(Guid.Empty, epoch);
    }

    [Fact]
    public async Task Malformed_authority_epoch_is_not_reseeded_and_disables_only_Guild()
    {
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "UPDATE guild_signet_authority_state SET Epoch='00000000-0000-0000-0000-000000000000'";

        _ = await command.ExecuteNonQueryAsync();

        var refused = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.True(refused.Core.IsHealthy);

        Assert.False(refused.GuildCanonical.IsHealthy);

        command.CommandText = "SELECT Epoch FROM guild_signet_authority_state";

        Assert.Equal("00000000-0000-0000-0000-000000000000", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Published_Guild_one_upgrades_without_rewriting_existing_discovery_state()
    {
        Assert.Equal(GuildCanonicalSchemaVersionOneFixture.PublishedFingerprint, GuildCanonicalSchemaVersionOneFixture.Fingerprint);

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        var first = await GrimoireSchemaTestInstaller.InstallAsync(connection, GuildCanonicalSchemaVersionOneFixture.ChainSet(), 768, CancellationToken.None);

        Assert.True(first.GuildCanonical.IsHealthy);

        var upgraded = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.True(upgraded.GuildCanonical.IsHealthy);

        Assert.Equal(9, upgraded.GuildCanonical.SchemaVersion);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT Epoch FROM guild_signet_authority_state WHERE StateKey=1";

        string epoch = (string)(await command.ExecuteScalarAsync())!;

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Assert.Equal(epoch, await command.ExecuteScalarAsync());
    }
}
