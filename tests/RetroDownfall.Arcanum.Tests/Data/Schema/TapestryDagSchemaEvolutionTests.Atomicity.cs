using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class TapestryDagSchemaEvolutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_transition_statement_rolls_back_data_catalog_and_version_on_interruption(bool cancel)
    {
        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        GrimoireSchemaVersionStep step = Assert.Single(core.Steps, item => item.ToVersion == 26);

        foreach (GrimoireSchemaTransitionStatement stop in step.Statements)
        {
            using var file = EvolutionScratchDatabase.Create();

            await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentyFiveFixture.ChainSet(), 64, CancellationToken.None);

            await ExecuteAsync(connection, Generation("bad", "/scope") + Node("bad-leaf", "bad", 0, "Leaf", parent: "'missing'"));

            string before = await CatalogAsync(connection);

            using CancellationTokenSource cancellation = new();

            connection.CreateFunction("interrupt_dag", () =>
            {
                cancellation.Cancel();

                return 0L;
            });

            GrimoireSchemaVersionStep interrupted = step with
            {
                Statements = [.. step.Statements, new GrimoireSchemaTransitionStatement(
                    "InjectedFailure", stop.Ordinal + 1, "InjectedFailure",
                    cancel ? "SELECT interrupt_dag();" : "INSERT INTO MissingDagTable VALUES(1);")],
            };

            GrimoireSchemaVersionChainSet chains = new(
            [
                new GrimoireSchemaVersionChain(core.HeadManifest, core.HeadObjects, [.. core.Steps.Where(item => item.ToVersion < 26), interrupted]),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            ]);

            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 64, cancellation.Token));
            }
            else
            {
                Exception? failure = await Record.ExceptionAsync(() => GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 64, CancellationToken.None));

                Assert.True(failure is SqliteException, $"Phase {stop.Ordinal}: expected SQLite failure, got {failure?.ToString() ?? "success"}.");
            }

            Assert.Equal(before, await CatalogAsync(connection));

            Assert.Equal(25L, await ScalarAsync(connection, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=0 AND TransactionTierCode=0"));

            Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_nodes WHERE NodeId='bad-leaf'"));

            await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

            Assert.Equal(26L, await ScalarAsync(connection, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=0 AND TransactionTierCode=0"));
        }
    }

    [Fact]
    public async Task Upgraded_catalog_equals_fresh_and_old_binary_refuses_it()
    {
        using var oldFile = EvolutionScratchDatabase.Create();

        using var freshFile = EvolutionScratchDatabase.Create();

        await using SqliteConnection old = await oldFile.OpenAsync(CancellationToken.None);

        await using SqliteConnection fresh = await freshFile.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(old, CoreSchemaVersionTwentyFiveFixture.ChainSet(), 64, CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(old, 64, CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(fresh, 64, CancellationToken.None);

        Assert.Equal(await CatalogAsync(fresh), await CatalogAsync(old));

        Exception refused = await Assert.ThrowsAnyAsync<Exception>(() => GrimoireSchemaTestInstaller.InstallAsync(old, CoreSchemaVersionTwentyFiveFixture.ChainSet(), 64, CancellationToken.None));

        Assert.Contains("IncompatibleNewerVersion", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("INSERT INTO tapestry_edges (GenerationId,ParentNodeId,ChildNodeId,MembershipReason) VALUES('g','leaf','a','Posterior')")]
    [InlineData("INSERT INTO tapestry_edges (GenerationId,ParentNodeId,ChildNodeId,MembershipReason) VALUES('g','a','a','Posterior')")]
    [InlineData("INSERT INTO tapestry_edges (GenerationId,ParentNodeId,ChildNodeId,MembershipReason) VALUES('g','other','leaf','Posterior')")]
    [InlineData("INSERT INTO tapestry_edges (GenerationId,ParentNodeId,ChildNodeId,MembershipReason) VALUES('g','missing','leaf','Posterior')")]
    [InlineData("UPDATE tapestry_nodes SET Layer=0 WHERE NodeId='a'")]
    [InlineData("UPDATE tapestry_nodes SET Layer=2 WHERE NodeId='leaf'")]
    [InlineData("UPDATE tapestry_nodes SET NodeKind='Leaf' WHERE NodeId='a'")]
    [InlineData("UPDATE tapestry_edges SET ChildNodeId='a' WHERE ParentNodeId='a'")]
    [InlineData("INSERT INTO tapestry_edges (GenerationId,ParentNodeId,ChildNodeId,MembershipReason) VALUES('g','a','leaf','Posterior')")]
    public async Task Every_graph_writer_preserves_strict_dag_shape(string mutation)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, Generation("g", "/scope") + Generation("other-g", "/other")
            + Node("leaf", "g", 0, "Leaf") + Node("a", "g", 1, "Summary") + Node("other", "other-g", 2, "Summary", "/other")
            + "INSERT INTO tapestry_edges (GenerationId,ParentNodeId,ChildNodeId,MembershipReason) VALUES('g','a','leaf','Posterior');");

        await AssertConstraintAsync(connection, mutation);

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM tapestry_edges"));
    }

    [Fact]
    public async Task Published_edge_changes_fail_but_owner_delete_cascades_all_graph_rows()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, Generation("g", "/scope") + Node("a", "g", 1, "Summary")
            + Node("leaf", "g", 0, "Leaf", parent: "'a'")
            + "INSERT INTO tapestry_node_embeddings VALUES('leaf',x'00000000',64); UPDATE tapestry_generations SET Status='Complete' WHERE GenerationId='g';");

        await AssertConstraintAsync(connection, "DELETE FROM tapestry_edges");

        await AssertConstraintAsync(connection, "UPDATE tapestry_edges SET MembershipReason='changed'");

        await AssertConstraintAsync(connection, "UPDATE tapestry_nodes SET ParentNodeId=NULL WHERE NodeId='leaf'");

        await ExecuteAsync(connection, "DELETE FROM tapestry_generations WHERE GenerationId='g'");

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT (SELECT COUNT(*) FROM tapestry_nodes) + (SELECT COUNT(*) FROM tapestry_edges) + (SELECT COUNT(*) FROM tapestry_node_embeddings)"));
    }

    private static async Task<string> CatalogAsync(SqliteConnection connection)
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
