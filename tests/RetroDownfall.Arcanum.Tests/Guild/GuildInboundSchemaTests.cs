using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildInboundSchemaTests
{
    [Fact]
    public async Task Fresh_install_provides_durable_peer_task_and_revocation_epoch_storage()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(default);

        var installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN('guild_inbound_tasks','guild_inbound_peer_state')";

        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }
}
