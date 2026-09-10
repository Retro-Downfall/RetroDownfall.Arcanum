using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Core version 10 adds the private claim that owns crash-safe batch accounting recovery without
/// widening the OpenAI-compatible public status vocabulary.
/// </summary>
public sealed class BatchAccountingRecoveryClaimEvolutionTests
{
    static BatchAccountingRecoveryClaimEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task Version_nine_evolves_to_the_same_private_claim_table_as_a_fresh_install()
    {
        using EvolutionScratchDatabase evolvedFile = EvolutionScratchDatabase.Create();
        await using SqliteConnection evolvedConnection = await evolvedFile.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult seeded = await GrimoireSchemaTestInstaller.InstallAsync(
            evolvedConnection,
            CoreSchemaVersionNineFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, seeded.Core.Health);
        Assert.Equal(9, seeded.Core.SchemaVersion);
        Assert.Null(await ReadTableSqlAsync(evolvedConnection));

        GrimoireSchemaInstallResult evolved = await GrimoireSchemaTestInstaller.InstallAsync(
            evolvedConnection,
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, evolved.Core.Health);
        Assert.Equal(10, evolved.Core.SchemaVersion);

        string evolvedSql = Assert.IsType<string>(await ReadTableSqlAsync(evolvedConnection));

        Assert.Equal(
            "Batches|BatchId|Id|CASCADE",
            await ScalarStringAsync(
                evolvedConnection,
                """
                SELECT "table" || '|' || "from" || '|' || "to" || '|' || on_delete
                FROM pragma_foreign_key_list('BatchAccountingRecoveryClaims');
                """));

        using EvolutionScratchDatabase freshFile = EvolutionScratchDatabase.Create();
        await using SqliteConnection freshConnection = await freshFile.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult fresh = await GrimoireSchemaTestInstaller.InstallAsync(
            freshConnection,
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, fresh.Core.Health);
        Assert.Equal(10, fresh.Core.SchemaVersion);
        Assert.Equal(evolvedSql, await ReadTableSqlAsync(freshConnection));
    }

    private static async Task<string?> ReadTableSqlAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'BatchAccountingRecoveryClaims';";

        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
}
