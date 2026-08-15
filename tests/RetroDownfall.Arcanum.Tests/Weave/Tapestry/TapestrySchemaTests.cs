using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

/// <summary>
/// The Tapestry's durable tables are declared in the same <c>Data/Schema/</c> tree as every other
/// object and installed by <see cref="GrimoireSchemaInstaller"/> — no EF entity, no compiled model
/// regeneration (DESIGN §5.4.4, §21.11).
/// </summary>
public sealed class TapestrySchemaTests
{

    private static async Task<SqliteConnection> OpenInitializedAsync(int dimensions = 64)
    {

        SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            "Data Source=:memory:",
            CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            dimensions,
            CancellationToken.None);

        return connection;

    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, string parameter)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        command.Parameters.AddWithValue("@name", parameter);

        return (long)(await command.ExecuteScalarAsync())!;

    }

    [Fact]
    public async Task EnsureSchemaAsync_CreatesTapestryTables()
    {

        await using SqliteConnection connection = await OpenInitializedAsync();

        string[] expected =
        [
            "tapestry_generations",
            "tapestry_nodes",
            "tapestry_node_embeddings",
        ];

        foreach (string table in expected)
        {

            Assert.Equal(
                1L,
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name",
                    table));

        }

    }

    [Fact]
    public async Task EnsureSchemaAsync_TapestryNodesCarryGenerationAndLineageColumns()
    {

        await using SqliteConnection connection = await OpenInitializedAsync();

        HashSet<string> columns = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT name FROM pragma_table_info('tapestry_nodes')";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {

            _ = columns.Add(reader.GetString(0));

        }

        string[] expected =
        [
            "NodeId",
            "GenerationId",
            "ScopeKind",
            "ScopeId",
            "Layer",
            "ParentScopeKey",
            "NodeKind",
            "ParentNodeId",
            "SourceKind",
            "SourceId",
            "SourceLabel",
            "Content",
            "ContentHash",
            "ChildMembershipHash",
            "DescendantLeafCount",
            "ClusterOrdinal",
            "PartitionReason",
            "EmbeddingDimension",
            "CreatedAt",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), columns.Order(StringComparer.Ordinal));

    }

    [Fact]
    public async Task EnsureSchemaAsync_TapestryGenerationsCarryProvenanceColumns()
    {

        await using SqliteConnection connection = await OpenInitializedAsync();

        HashSet<string> columns = [];

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT name FROM pragma_table_info('tapestry_generations')";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {

            _ = columns.Add(reader.GetString(0));

        }

        foreach (string required in new[]
        {
            "GenerationId",
            "ScopeKind",
            "ScopeId",
            "Status",
            "AlgorithmVersion",
            "SettingsFingerprint",
            "SummaryModel",
            "SummaryRecipeVersion",
            "EmbeddingDimension",
            "CorpusFingerprint",
            "LayerCount",
            "NodeCount",
            "RootNodeCount",
            "TerminalReason",
            "StartedAt",
            "CompletedAt",
        })
        {

            Assert.Contains(required, columns);

        }

    }

    [Fact]
    public async Task EnsureSchemaAsync_IsIdempotent()
    {

        await using SqliteConnection connection = await OpenInitializedAsync();

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            64,
            CancellationToken.None);

        Assert.Equal(
            1L,
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name",
                "tapestry_nodes"));

    }

    [Fact]
    public async Task EnsureSchemaAsync_DeletingAGenerationCascadesToNodesAndEmbeddings()
    {

        await using SqliteConnection connection = await OpenInitializedAsync();

        await using (SqliteCommand pragma = connection.CreateCommand())
        {

            pragma.CommandText = "PRAGMA foreign_keys=ON;";

            _ = await pragma.ExecuteNonQueryAsync();

        }

        await using (SqliteCommand seed = connection.CreateCommand())
        {

            seed.CommandText =
                """
                INSERT INTO tapestry_generations
                    (GenerationId, ScopeKind, ScopeId, Status, AlgorithmVersion, SettingsFingerprint,
                     SummaryModel, SummaryRecipeVersion, EmbeddingDimension, CorpusFingerprint,
                     LayerCount, NodeCount, RootNodeCount, TerminalReason, StartedAt, CompletedAt)
                VALUES ('g1', 'Workspace', '/repo', 'Complete', 'v1', 'fp', NULL, 'r1', 64, 'cf',
                        1, 1, 1, 'LeafOnly', '2026-01-01T00:00:00Z', '2026-01-01T00:00:01Z');

                INSERT INTO tapestry_nodes
                    (NodeId, GenerationId, ScopeKind, ScopeId, Layer, ParentScopeKey, NodeKind,
                     ParentNodeId, SourceKind, SourceId, SourceLabel, Content, ContentHash,
                     ChildMembershipHash, DescendantLeafCount, ClusterOrdinal, PartitionReason,
                     EmbeddingDimension, CreatedAt)
                VALUES ('n1', 'g1', 'Workspace', '/repo', 0, 'g1#root', 'Leaf', NULL,
                        'WorkspaceFileChunk', 'c1', 'a.cs', NULL, 'hash', NULL, 1, 0, 'None',
                        64, '2026-01-01T00:00:00Z');

                INSERT INTO tapestry_node_embeddings (NodeId, Embedding, Dim)
                VALUES ('n1', x'00000000', 64);
                """;

            _ = await seed.ExecuteNonQueryAsync();

        }

        await using (SqliteCommand delete = connection.CreateCommand())
        {

            delete.CommandText = "DELETE FROM tapestry_generations WHERE GenerationId = 'g1'";

            _ = await delete.ExecuteNonQueryAsync();

        }

        await using SqliteCommand counts = connection.CreateCommand();

        counts.CommandText =
            "SELECT (SELECT COUNT(*) FROM tapestry_nodes) + (SELECT COUNT(*) FROM tapestry_node_embeddings)";

        Assert.Equal(0L, (long)(await counts.ExecuteScalarAsync())!);

    }

}
