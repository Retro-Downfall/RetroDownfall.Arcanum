using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildDeclaredSourceStateTests
{
    [Fact]
    public async Task Oversize_encoded_claim_metadata_is_refused_before_any_claim_is_persisted()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        await using (var connection = await database.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        var store = new GuildStore(new FixtureOrdinaryConnectionFactory(database.ConnectionString),
            new GrimoireConnectionAdmissionGate(TimeProvider.System), TimeProvider.System);

        Guid generation = Guid.NewGuid();

        string directory = Path.GetTempPath();

        var begun = await store.BeginDeclaredRootScanAsync(generation, GuildAgentSourceKind.DeclaredGlobal, null,
            directory, CancellationToken.None);

        Assert.True(begun.IsSuccess);

        GuildParsedAgent agent = new(Guid.NewGuid(), "Agent", "Description", "1", [], ["text/plain"], ["text/plain"], [], "guidance");

        GuildDeclaredAgentObservation oversized = new(new string('界', 22000), GuildAgentSourceKind.DeclaredGlobal,
            null, agent, new string('A', 64), null);

        var result = await store.ObserveDeclaredSourceAsync(generation,
            new(GuildAgentSourceKind.DeclaredGlobal, null, directory, begun.Value), oversized, CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.Equal("Guild.PayloadTooLarge", result.Error.Code);

        await using var verified = await database.OpenAsync(CancellationToken.None);

        await using SqliteCommand command = verified.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM guild_declared_source_claims";

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
