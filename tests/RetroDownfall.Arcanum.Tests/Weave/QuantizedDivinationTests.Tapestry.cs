using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed partial class QuantizedDivinationTests
{
    [Fact]
    public async Task Tapestry_largest_admitted_malformed_companion_preserves_exact_ranking_with_bounded_reads()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        SqliteConnectionStringBuilder settings = new(scratch.ConnectionString)
        {
            Password = "quantized-oversized-tapestry-fixture",
        };

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(settings.ConnectionString, CancellationToken.None);

        (TapestryGenerationAccess access, TapestryRetrievalGraph graph) = await SeedTapestryRankingAsync(connection, 1536);

        await ExecuteAsync(connection, $"UPDATE tapestry_node_embeddings_quantized SET Representation=zeroblob({QuantizedEmbeddingCodec.MaximumDimensions + QuantizedEmbeddingCodec.RepresentationOverheadBytes}) WHERE NodeId='00000001'; PRAGMA mmap_size=0; PRAGMA cache_size=-512;");

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        TapestryStore store = new(db);

        await ColdAsync(connection);

        Result<TapestryRankedPage> result = await store.RankRetrievalNodesAsync(access, Axis(0), graph, null, 2, CancellationToken.None);

        int pages = CacheMisses(connection);

        Assert.True(result.IsSuccess);

        Assert.Equal(new[] { "00000001", "00000002" }, result.Value.Nodes.Select(node => node.NodeId));

        Assert.All(result.Value.Nodes, node => Assert.Equal(0x3F800000, BitConverter.SingleToInt32Bits(node.Similarity)));

        output.WriteLine($"Tapestry largest admitted malformed companion: {pages} cold pager misses with exact fallback.");

        Assert.InRange(pages, 1, 1024);
    }

    [Theory]
    [InlineData(31, false)]
    [InlineData(1536, true)]
    public async Task Tapestry_compact_ranking_preserves_generation_graph_counts_ties_and_selected_pages(int count, bool accelerated)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        (TapestryGenerationAccess access, TapestryRetrievalGraph graph) = await SeedTapestryRankingAsync(connection, count);

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        TapestryStore store = new(db);

        List<string> statements = [];

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) => statements.Add(sql), null);

        try
        {
            foreach (IReadOnlyList<string>? selected in new IReadOnlyList<string>?[]
            {
                null,
                [.. Enumerable.Range(0, count).Select(index => index.ToString("D8")), "00000001", "foreign", "absent", "'); DROP TABLE tapestry_nodes; --"],
            })
            {
                Result<TapestryRankedPage> ranked = await store.RankRetrievalNodesAsync(access, Axis(0), graph, selected, 2, CancellationToken.None);

                Assert.True(ranked.IsSuccess);

                Assert.Equal(count - 2, ranked.Value.ScoredCount);

                Assert.True(ranked.Value.Truncated);

                Assert.Equal(new[] { "00000001", "00000002" }, ranked.Value.Nodes.Select(node => node.NodeId));

                Assert.All(ranked.Value.Nodes, node => Assert.Equal(0x3F800000, BitConverter.SingleToInt32Bits(node.Similarity)));
            }

            Result<TapestryRankedPage> all = await store.RankRetrievalNodesAsync(access, Axis(0), graph, null, count, CancellationToken.None);

            Assert.True(all.IsSuccess);

            Assert.False(all.Value.Truncated);

            Assert.Equal(count - 2, all.Value.Nodes.Count);

            Assert.Equal(new[] { "00000001", "00000002", "00000000" }, all.Value.Nodes.Take(3).Select(node => node.NodeId));

            Result<TapestryRankedPage> forged = await store.RankRetrievalNodesAsync(access with
            {
                Identity = access.Identity with { EmbeddingIdentity = "other-model" },
            }, Axis(0), graph, [], 2, CancellationToken.None);

            Assert.True(forged.IsFailure);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.Equal(accelerated, statements.Any(sql => sql.Contains("JOIN \"tapestry_node_embeddings_quantized\"", StringComparison.Ordinal)));

        if (!accelerated)
        {
            Assert.DoesNotContain(statements, sql => sql.Contains("_quantized", StringComparison.Ordinal));

            Assert.Contains(statements, sql => sql.Contains("CASE WHEN length(e.Embedding)=", StringComparison.Ordinal));
        }

        output.WriteLine($"Tapestry ranked {count} rows with graph and generation filtering; compact scan: {accelerated}; selected ids cross 512-row pages.");
    }

    private static async Task<(TapestryGenerationAccess Access, TapestryRetrievalGraph Graph)> SeedTapestryRankingAsync(SqliteConnection connection, int count)
    {
        await using SqliteCommand command = connection.CreateCommand();

        foreach (string name in new[] { "tapestry_generations", "tapestry_scope_revisions" })
        {
            command.CommandText = GrimoireSchemaCatalog.CoreObjects.Single(item => item.Name == name).Sql;

            await command.ExecuteNonQueryAsync();
        }

        command.CommandText = """
            CREATE TABLE tapestry_nodes(NodeId TEXT PRIMARY KEY, GenerationId TEXT, NodeKind TEXT, SourceId TEXT);
            CREATE TABLE session_attachment_chunks(ChunkId TEXT PRIMARY KEY, MimeType TEXT);
            CREATE TABLE tapestry_node_embeddings(NodeId TEXT PRIMARY KEY, Embedding BLOB, Dim INTEGER);
            CREATE TABLE tapestry_node_embeddings_quantized(NodeId TEXT PRIMARY KEY, Representation BLOB NOT NULL CHECK(typeof(Representation)='blob' AND length(Representation)<=65624)) WITHOUT ROWID;
            INSERT INTO tapestry_generations(GenerationId,ScopeKind,ScopeId,Status,AlgorithmVersion,SettingsFingerprint,
                SummaryRecipeVersion,EmbeddingDimension,CorpusFingerprint,StartedAt,ValidatedInvalidationRevision,EmbeddingIdentity)
            VALUES('current','Workspace','/repo','Complete',@algorithm,'settings','recipe',768,'corpus','2026-01-01T00:00:00.0000000Z',0,'model');
            INSERT INTO tapestry_nodes VALUES('foreign','old','Summary',NULL);
            INSERT INTO tapestry_node_embeddings VALUES('foreign',@winner,768);
            """;

        command.Parameters.AddWithValue("@algorithm", SphericalKMeans.AlgorithmVersion);

        command.Parameters.AddWithValue("@winner", EmbeddingBlobCodec.Encode(Axis(0)));

        await command.ExecuteNonQueryAsync();

        command.Parameters.Clear();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO tapestry_nodes VALUES(@id,'current','Summary',NULL);
            INSERT INTO tapestry_node_embeddings VALUES(@id,@vector,768);
            INSERT INTO tapestry_node_embeddings_quantized VALUES(@id,@compact);
            """;

        SqliteParameter id = command.Parameters.Add("@id", SqliteType.Text);

        SqliteParameter vector = command.Parameters.Add("@vector", SqliteType.Blob);

        SqliteParameter compact = command.Parameters.Add("@compact", SqliteType.Blob);

        Dictionary<string, TapestryRetrievalVertex> vertices = new(StringComparer.Ordinal);

        for (int index = 0; index < count; index++)
        {
            string key = index.ToString("D8");

            float[] embedding = index < 3 ? Axis(0) : Axis(1);

            if (index == count - 2)
            {
                embedding[0] = float.NaN;
            }

            id.Value = key;

            vector.Value = EmbeddingBlobCodec.Encode(embedding);

            compact.Value = index % 7 == 0 || index == count - 2
                ? new byte[] { 1 }
                : QuantizedEmbeddingCodec.Encode(embedding, Encoding.UTF8.GetBytes(key))!;

            await command.ExecuteNonQueryAsync();

            if (index != count - 1)
            {
                vertices.Add(key, new(0, DateTimeOffset.Parse(index is 1 or 2 ? "2026-01-02T00:00:00Z" : "2026-01-01T00:00:00Z"), [], 1));
            }
        }

        command.CommandText = "DELETE FROM tapestry_node_embeddings_quantized WHERE NodeId='00000002'";

        command.Parameters.Clear();

        await command.ExecuteNonQueryAsync();

        await transaction.CommitAsync();

        TapestryGeneration generation = new("current", TapestryScopeKind.Workspace, "/repo", TapestryGenerationStatus.Complete,
            SphericalKMeans.AlgorithmVersion, "settings", null, "recipe", Dimensions, "corpus", 1, count, count, null,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), null, 0, 0, "model");

        return (new(generation, new("model", Dimensions)), new(vertices));
    }
}
