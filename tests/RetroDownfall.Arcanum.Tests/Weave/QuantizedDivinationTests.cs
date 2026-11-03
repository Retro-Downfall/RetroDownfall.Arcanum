using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed partial class QuantizedDivinationTests(ITestOutputHelper output)
{
    private const int Dimensions = 768;

    static QuantizedDivinationTests() => SqliteNativeRuntime.Instance.Initialize();

    [Theory]
    [InlineData(31, false)]
    [InlineData(2048, true)]
    public async Task Only_large_corpora_read_companions_and_corrupt_rows_still_score_exactly(int count, bool accelerated)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await CreateCorpusAsync(connection);

        await using SqliteCommand insert = connection.CreateCommand();

        insert.CommandText = """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM rows WHERE n<@count)
            INSERT INTO entry_embeddings SELECT printf('%08d',n),@vector,768 FROM rows;
            INSERT INTO entry_embeddings_quantized SELECT EntryId,X'01' FROM entry_embeddings;
            """;

        insert.Parameters.AddWithValue("@count", count);

        insert.Parameters.AddWithValue("@vector", EmbeddingBlobCodec.Encode(Axis(0)));

        await insert.ExecuteNonQueryAsync();

        List<string> statements = [];

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) => statements.Add(sql), null);

        Result<DivinationResult[]> result;

        try
        {
            result = await SearchAsync(connection, Axis(0), 3, 0f);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(result.IsSuccess);

        Assert.Equal(new[] { "00000001", "00000002", "00000003" }, result.Value.Select(hit => hit.Id));

        Assert.All(result.Value, hit => Assert.Equal(0x3F800000, BitConverter.SingleToInt32Bits(hit.Similarity)));

        Assert.Equal(accelerated, statements.Any(sql => sql.Contains("JOIN \"entry_embeddings_quantized\"", StringComparison.Ordinal)));

        output.WriteLine($"Read {count} rows; compact scan expected: {accelerated}.");

        if (!accelerated)
        {
            Assert.DoesNotContain(statements, sql => sql.Contains("_quantized", StringComparison.Ordinal));

            const string baselineSql = "SELECT \"EntryId\", \"Embedding\"\nFROM \"entry_embeddings\"";

            string scoringSql = Assert.Single(statements, sql => sql == baselineSql);

            string[] actualPlan = await ExplainAsync(connection, scoringSql);

            await using SqliteCommand removeCompanion = connection.CreateCommand();

            removeCompanion.CommandText = "DROP TABLE entry_embeddings_quantized";

            await removeCompanion.ExecuteNonQueryAsync();

            // The frozen post-#172 statement and its pre-companion schema must yield the same
            // complete EXPLAIN rows as the small-corpus statement that actually executed.
            Assert.Equal(await ExplainAsync(connection, baselineSql), actualPlan);

            Assert.Contains(actualPlan, row => row.EndsWith("|SCAN entry_embeddings", StringComparison.Ordinal));
        }
    }

    private static async Task<string[]> ExplainAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        List<string> rows = [];

        while (await reader.ReadAsync())
        {
            rows.Add($"{reader.GetInt64(0)}|{reader.GetInt64(1)}|{reader.GetInt64(2)}|{reader.GetString(3)}");
        }

        return rows.ToArray();
    }

    private static async Task CreateCorpusAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            CREATE TABLE entry_embeddings(EntryId TEXT PRIMARY KEY, Embedding BLOB NOT NULL, Dim INTEGER NOT NULL);
            CREATE TABLE entry_embeddings_quantized(EntryId TEXT PRIMARY KEY, Representation BLOB NOT NULL CHECK(typeof(Representation)='blob' AND length(Representation)<=65624)) WITHOUT ROWID;
            """;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Result<DivinationResult[]>> SearchAsync(SqliteConnection connection, float[] query, int take, float threshold)
    {
        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        DivinationService service = new(db, NullLogger<DivinationService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        return await service.SearchAsync("entry_embeddings", "EntryId", "Embedding", new Embedding<float>(query), take, threshold, CancellationToken.None);
    }

    private static float[] Axis(int index)
    {
        float[] vector = new float[Dimensions];

        vector[index] = 1f;

        return vector;
    }

    [Fact]
    public async Task Randomized_compact_scan_matches_exhaustive_ids_order_and_score_bits_with_partial_and_corrupt_coverage()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await CreateCorpusAsync(connection);

        Random random = new(173);

        float[][] corpus = Enumerable.Range(0, 2048).Select(_ => RandomVector(random)).ToArray();

        // Duplicate rows force the full-precision tie-break at the K boundary.
        corpus[1023] = corpus[1022].ToArray();

        await SeedAsync(connection, corpus);

        await using SqliteCommand damage = connection.CreateCommand();

        damage.CommandText = """
            DELETE FROM entry_embeddings_quantized WHERE CAST(EntryId AS INTEGER)%11=0;
            UPDATE entry_embeddings_quantized SET Representation=zeroblob(length(Representation)) WHERE CAST(EntryId AS INTEGER)%13=0;
            """;

        await damage.ExecuteNonQueryAsync();

        for (int i = 0; i < 128; i++)
        {
            float[] query = i % 4 == 0 ? corpus[(i * 31) % corpus.Length] : RandomVector(random);

            int take = new[] { 1, 3, 17, 50 }[i % 4];

            float threshold = i % 3 == 0 ? 0.08f : -1f;

            (string Id, int Bits)[] expected = Exhaustive(corpus, query, take, threshold);

            Result<DivinationResult[]> actual = await SearchAsync(connection, query, take, threshold);

            Assert.True(actual.IsSuccess);

            Assert.Equal(expected, actual.Value.Select(hit => (hit.Id, BitConverter.SingleToInt32Bits(hit.Similarity))).ToArray());
        }

        output.WriteLine("128 queries over 2,048 x 768-dimensional vectors matched the exhaustive ids, ordering and float bits with partial/corrupt coverage.");
    }

    private static float[] RandomVector(Random random) => Enumerable.Range(0, Dimensions)
        .Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();

    [Fact]
    public async Task Error_bound_rescues_a_true_winner_that_rounds_below_both_the_heap_and_threshold()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await CreateCorpusAsync(connection);

        float[] loser = Axis(0);

        loser[1] = 0.0030f;

        float[] winner = Axis(0);

        winner[1] = 0.0039f;

        float[][] corpus = Enumerable.Repeat(loser, 2048).Append(winner).ToArray();

        await SeedAsync(connection, corpus);

        foreach (float threshold in new[] { 0f, 0.0038f, EmbeddingBlobCodec.CosineSimilarity(Axis(1), winner) })
        {
            Result<DivinationResult[]> result = await SearchAsync(connection, Axis(1), 1, threshold);

            Assert.True(result.IsSuccess);

            DivinationResult hit = Assert.Single(result.Value);

            Assert.Equal("00002048", hit.Id);

            Assert.Equal(BitConverter.SingleToInt32Bits(EmbeddingBlobCodec.CosineSimilarity(Axis(1), winner)),
                BitConverter.SingleToInt32Bits(hit.Similarity));
        }
    }

    private static (string Id, int Bits)[] Exhaustive(float[][] corpus, float[] query, int take, float threshold) => corpus
        .Select((vector, index) => (Id: index.ToString("D8"), Score: EmbeddingBlobCodec.CosineSimilarity(query, vector)))
        .Where(hit => hit.Score >= threshold)
        .OrderByDescending(hit => hit.Score)
        .ThenBy(hit => hit.Id, StringComparer.Ordinal)
        .Take(take)
        .Select(hit => (hit.Id, BitConverter.SingleToInt32Bits(hit.Score))).ToArray();

    [Fact]
    public async Task Session_compact_scan_applies_campaign_status_and_timestamp_ties_before_taking_the_page()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await CreateCorpusAsync(connection);

        float[] scoped = Axis(0);

        scoped[1] = 0.1f;

        await SeedAsync(connection, Enumerable.Repeat(Axis(0), 2048).Concat(Enumerable.Repeat(scoped, 3)).ToArray(),
            i => $"{i:D8}-0000-4000-8000-000000000000");

        await using SqliteCommand setup = connection.CreateCommand();

        setup.CommandText = """
            CREATE TABLE Sessions(Id TEXT PRIMARY KEY, CampaignId TEXT, Status TEXT, Title TEXT);
            CREATE TABLE Entries(Id TEXT PRIMARY KEY, SessionId TEXT, Content TEXT, Role INTEGER, CreatedAt TEXT);
            INSERT INTO Sessions VALUES ('A0000000-0000-4000-8000-000000000000',NULL,'active','Excluded');
            INSERT INTO Sessions VALUES ('B0000000-0000-4000-8000-000000000000','C0000000-0000-4000-8000-000000000000','archived','Included');
            INSERT INTO Entries SELECT EntryId,
                CASE WHEN EntryId<'00002048' THEN 'A0000000-0000-4000-8000-000000000000' ELSE 'B0000000-0000-4000-8000-000000000000' END,
                'Content',1,CASE WHEN EntryId LIKE '00002049%' THEN '2026-01-02T00:00:00.0000000Z' ELSE '2026-01-01T00:00:00.0000000Z' END
                FROM entry_embeddings;
            """;

        await setup.ExecuteNonQueryAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        SessionDivinationService service = new(db, FixtureOrdinaryConnectionFactory.For(db), NullLogger<SessionDivinationService>.Instance);

        List<string> statements = [];

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) => statements.Add(sql), null);

        Result<SemanticSearchResult> result;

        try
        {
            result = await service.SearchAsync(new Embedding<float>(Axis(0)), Guid.Parse("C0000000-0000-4000-8000-000000000000"),
                "archived", 2, 0.5f, CancellationToken.None);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(result.IsSuccess);

        Assert.True(result.Value.HasMore);

        Assert.Equal(new[] { "00002049-0000-4000-8000-000000000000", "00002048-0000-4000-8000-000000000000" },
            result.Value.Results.Select(hit => hit.EntryId.ToString("D")));

        Assert.Contains(statements, sql => sql.Contains("JOIN \"entry_embeddings_quantized\"", StringComparison.Ordinal));
    }

    private static async Task SeedAsync(SqliteConnection connection, float[][] corpus, Func<int, string>? identity = null)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand insert = connection.CreateCommand();

        insert.Transaction = transaction;

        insert.CommandText = """
            INSERT INTO entry_embeddings VALUES (@id,@vector,768);
            INSERT INTO entry_embeddings_quantized VALUES (@id,@compact);
            """;

        SqliteParameter id = insert.Parameters.Add("@id", SqliteType.Text);

        SqliteParameter vector = insert.Parameters.Add("@vector", SqliteType.Blob);

        SqliteParameter compact = insert.Parameters.Add("@compact", SqliteType.Blob);

        for (int i = 0; i < corpus.Length; i++)
        {
            string key = identity?.Invoke(i) ?? i.ToString("D8");

            id.Value = key;

            vector.Value = EmbeddingBlobCodec.Encode(corpus[i]);

            compact.Value = QuantizedEmbeddingCodec.Encode(corpus[i], Encoding.UTF8.GetBytes(key))!;

            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
