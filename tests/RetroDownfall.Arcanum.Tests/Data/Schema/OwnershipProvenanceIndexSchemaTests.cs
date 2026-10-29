using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class OwnershipProvenanceIndexSchemaTests
{
    private const string IndexName = "idx_artifact_sensitivity_session_provenance";

    private const string IndexNames = "'idx_artifact_sensitivity_session_provenance', 'idx_long_rest_receipt_inputs_memory_receipt', 'idx_long_rest_protected_receipt_inputs_memory_receipt'";

    [Fact]
    public void Core29_is_the_exact_captured_predecessor_without_the_new_access_paths()
    {
        Assert.Equal(388, CoreSchemaVersionTwentyNineFixture.Objects.Count);

        Assert.Equal(CoreSchemaVersionTwentyNineFixture.PublishedFingerprint, CoreSchemaVersionTwentyNineFixture.Fingerprint);

        Assert.DoesNotContain(CoreSchemaVersionTwentyNineFixture.Objects,
            static item => item.Sql.Contains(IndexName, StringComparison.Ordinal));

        Assert.Equal(33, GrimoireSchemaVersionChains.CoreSchemaVersion);

        Assert.Equal(7, GrimoireSchemaVersionChains.CovenantCanonicalSchemaVersion);

        Assert.Equal(1, GrimoireSchemaVersionChains.CovenantAcceleratorSchemaVersion);
    }

    [Fact]
    public async Task Populated_Core29_upgrades_to_the_exact_fresh_index_catalog_without_losing_rows_or_FTS()
    {
        using EvolutionScratchDatabase oldFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection old = await oldFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(old, CoreSchemaVersionTwentyNineFixture.ChainSet(), 64, CancellationToken.None);

        await SeedAsync(old);

        Assert.Equal(0L, await ScalarAsync(old, $"SELECT COUNT(*) FROM sqlite_schema WHERE name IN ({IndexNames})"));

        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(old, 64, CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.Equal(3L, await ScalarAsync(old, $"SELECT COUNT(*) FROM sqlite_schema WHERE name IN ({IndexNames})"));

        Assert.Equal(4L, await ScalarAsync(old, "SELECT COUNT(*) FROM Entries"));

        Assert.Equal(4L, await ScalarAsync(old, "SELECT COUNT(*) FROM memory_search_fts WHERE memory_search_fts MATCH 'ret'"));

        await ExecuteAsync(old, "INSERT INTO memory_search_fts(memory_search_fts,rank) VALUES('integrity-check',1)");

        using EvolutionScratchDatabase freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection fresh = await freshFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAndDrainCoreAsync(fresh, 64, CancellationToken.None);

        Assert.Equal(await CatalogAsync(fresh), await CatalogAsync(old));

        Exception refused = await Assert.ThrowsAnyAsync<Exception>(() => GrimoireSchemaTestInstaller.InstallAsync(
            old, CoreSchemaVersionTwentyNineFixture.ChainSet(), 64, CancellationToken.None));

        Assert.Contains("IncompatibleNewerVersion", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interrupted_index_transition_restores_the_exact_Core29_catalog_and_data()
    {
        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        GrimoireSchemaVersionStep step = Assert.Single(core.Steps, static item => item.ToVersion == 30);

        foreach (GrimoireSchemaTransitionStatement stop in step.Statements)
        {
            using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentyNineFixture.ChainSet(), 64, CancellationToken.None);

            await SeedAsync(connection);

            string[] before = await CatalogAsync(connection);

            GrimoireSchemaVersionStep interrupted = step with
            {
                Statements = [.. step.Statements.SelectMany(statement => statement == stop
                    ? new[] { statement, new GrimoireSchemaTransitionStatement("InjectedFailure", stop.Ordinal + 1,
                        "InjectedFailure", "INSERT INTO MissingAnalyticsIndexTable VALUES(1)") }
                    : new[] { statement })],
            };

            GrimoireSchemaVersionChainSet chains = new(
            [
                new(core.HeadManifest, core.HeadObjects, [.. core.Steps.Select(item => item.ToVersion == 30 ? interrupted : item)]),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            ]);

            await Assert.ThrowsAsync<SqliteException>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 64, CancellationToken.None));

            Assert.Equal(before, await CatalogAsync(connection));

            Assert.Equal(29L, await ScalarAsync(connection, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=0 AND TransactionTierCode=0"));

            Assert.Equal(4L, await ScalarAsync(connection, "SELECT COUNT(*) FROM Entries"));

            Assert.Equal(4L, await ScalarAsync(connection, "SELECT COUNT(*) FROM memory_search_fts WHERE memory_search_fts MATCH 'ret'"));
        }
    }

    private static Task SeedAsync(SqliteConnection connection) => ExecuteAsync(connection, """
        INSERT INTO Sessions(Id,CreatedAt,UpdatedAt)
        VALUES('11111111-1111-4111-8111-111111111111','2026-10-26T12:00:00.0000000Z','2026-10-26T12:00:00.0000000Z');
        WITH RECURSIVE rows(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM rows WHERE i<4)
        INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence)
        SELECT printf('%08X-0000-4000-8000-000000000001',i),'11111111-1111-4111-8111-111111111111',1,
            'retainedtoken',CASE i WHEN 1 THEN '' WHEN 2 THEN 'model' WHEN 3 THEN 'MODEL' ELSE 'Émodel' END,
            '2026-10-26T12:00:00.0000000Z',i FROM rows;
        """);

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

    private static async Task<string[]> CatalogAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT type || ':' || name || ':' || sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY type,name";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> rows = [];

        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
    }
}
