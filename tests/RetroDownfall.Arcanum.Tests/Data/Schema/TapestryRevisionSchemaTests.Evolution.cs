using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class TapestryRevisionSchemaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Core27_EveryTriggerTransitionIsAtomicOnFailureOrCancellation(bool cancel)
    {
        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        GrimoireSchemaVersionStep step = Assert.Single(core.Steps, item => item.ToVersion == 27);

        Assert.Equal(12, step.Statements.Count);

        foreach (GrimoireSchemaTransitionStatement stop in step.Statements)
        {
            using var file = EvolutionScratchDatabase.Create();

            await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentySixFixture.ChainSet(), 64, CancellationToken.None);

            await ExecuteAsync(connection, Chunk("legacy", "/scope", "kept"));

            string before = await RevisionCatalogAsync(connection);

            using CancellationTokenSource cancellation = new();

            connection.CreateFunction("interrupt_revision", () => { cancellation.Cancel(); return 0L; });

            GrimoireSchemaVersionStep interrupted = step with
            {
                Statements = [.. step.Statements, new GrimoireSchemaTransitionStatement("InjectedFailure", stop.Ordinal + 1,
                    "InjectedFailure", cancel ? "SELECT interrupt_revision();" : "INSERT INTO MissingRevisionTable VALUES(1);")],
            };

            GrimoireSchemaVersionChainSet chains = new(
            [
                new(core.HeadManifest, core.HeadObjects, [.. core.Steps.Where(item => item.ToVersion < 27), interrupted]),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            ]);

            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 64, cancellation.Token));
            }
            else
            {
                await Assert.ThrowsAsync<SqliteException>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 64, CancellationToken.None));
            }

            Assert.Equal(before, await RevisionCatalogAsync(connection));

            Assert.Equal(26L, await RevisionScalarAsync(connection, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=0 AND TransactionTierCode=0"));

            Assert.Equal("kept", await RevisionScalarAsync(connection, "SELECT Content FROM workspace_file_chunks WHERE ChunkId='legacy'"));

            Assert.Equal(0L, await RevisionScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_scope_revisions"));

            await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

            Assert.Equal(27L, await RevisionScalarAsync(connection, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=0 AND TransactionTierCode=0"));

            await ExecuteAsync(connection, "UPDATE workspace_file_chunks SET Content='edited' WHERE ChunkId='legacy'");

            Assert.Equal("1:1", await RevisionsAsync(connection, "/scope"));
        }
    }

    [Fact]
    public async Task Core27_UpgradedCatalogEqualsFreshAndCore26RefusesNewerSchema()
    {
        using var oldFile = EvolutionScratchDatabase.Create();

        using var freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection old = await oldFile.OpenAsync(CancellationToken.None);

        await using SqliteConnection fresh = await freshFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(old, CoreSchemaVersionTwentySixFixture.ChainSet(), 64, CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(old, 64, CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(fresh, 64, CancellationToken.None);

        Assert.Equal(await RevisionCatalogAsync(fresh), await RevisionCatalogAsync(old));

        Exception refused = await Assert.ThrowsAnyAsync<Exception>(() => GrimoireSchemaTestInstaller.InstallAsync(old, CoreSchemaVersionTwentySixFixture.ChainSet(), 64, CancellationToken.None));

        Assert.Contains("IncompatibleNewerVersion", refused.Message, StringComparison.Ordinal);
    }

    private static async Task<object?> RevisionScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    private static async Task<string> RevisionCatalogAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT type || ':' || name || ':' || sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type,name";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> rows = [];

        while (await reader.ReadAsync())
        {
            rows.Add(GrimoireSqlNormalizer.Normalize(reader.GetString(0)));
        }

        return string.Join("\n", rows);
    }
}
