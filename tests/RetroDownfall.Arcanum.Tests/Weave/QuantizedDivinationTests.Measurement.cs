using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed partial class QuantizedDivinationTests
{
    [Fact]
    public async Task Largest_admitted_malformed_companion_falls_back_with_bounded_page_reads()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        SqliteConnectionStringBuilder settings = new(scratch.ConnectionString)
        {
            Password = "quantized-oversized-companion-fixture",
        };

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(settings.ConnectionString, CancellationToken.None);

        await CreateCorpusAsync(connection);

        float[][] corpus = Enumerable.Repeat(Axis(1), 2048).ToArray();

        corpus[1] = Axis(0);

        await SeedAsync(connection, corpus);

        await ExecuteAsync(connection, $"UPDATE entry_embeddings_quantized SET Representation=zeroblob({QuantizedEmbeddingCodec.MaximumDimensions + QuantizedEmbeddingCodec.RepresentationOverheadBytes}) WHERE EntryId='00000001'");

        await ExecuteAsync(connection, "PRAGMA mmap_size=0; PRAGMA cache_size=-512;");

        await ColdAsync(connection);

        Result<DivinationResult[]> result = await SearchAsync(connection, Axis(0), 1, 0.5f);

        int pages = CacheMisses(connection);

        Assert.True(result.IsSuccess);

        DivinationResult winner = Assert.Single(result.Value);

        Assert.Equal("00000001", winner.Id);

        Assert.Equal(0x3F800000, BitConverter.SingleToInt32Bits(winner.Similarity));

        output.WriteLine($"Largest admitted malformed companion: {pages} cold pager misses with exact fallback.");

        // The storage CHECK caps every companion at the maximum supported representation.
        // Even a maximum-sized malformed record must preserve bounded reads and exact rescue.
        Assert.InRange(pages, 1, 1024);
    }

    [Fact]
    public async Task Encrypted_cold_scan_measures_payload_pages_and_allocations_at_shipped_dimensions()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        SqliteConnectionStringBuilder settings = new(scratch.ConnectionString)
        {
            Password = "quantized-retrieval-measurement-fixture",
        };

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(settings.ConnectionString, CancellationToken.None);

        await CreateCorpusAsync(connection);

        Random random = new(170173);

        float[][] corpus = Enumerable.Range(0, 8192).Select(_ => RandomVector(random)).ToArray();

        corpus[0] = Axis(0);

        corpus[1] = Axis(1);

        corpus[1][0] = 2f;

        await SeedAsync(connection, corpus);

        await ExecuteAsync(connection, "PRAGMA mmap_size=0; PRAGMA cache_size=-512;");

        float[] query = Axis(0);

        const float threshold = 0.70f;

        await ColdAsync(connection);

        long exhaustiveBytes = 0;

        List<(string Id, int Bits)> expected = [];

        await using (SqliteCommand exhaustive = connection.CreateCommand())
        {
            exhaustive.CommandText = "SELECT EntryId, Embedding FROM entry_embeddings";

            await using SqliteDataReader reader = await exhaustive.ExecuteReaderAsync();

            double norm = EmbeddingBlobCodec.NormSquared(query);

            while (await reader.ReadAsync())
            {
                exhaustiveBytes += SQLitePCL.raw.sqlite3_column_bytes(reader.Handle, 1);

                if (DivinationService.TryScoreRow(reader, 1, query, norm, out float score)
                    == DivinationService.RowScore.Scored && score >= threshold)
                {
                    expected.Add((reader.GetString(0), BitConverter.SingleToInt32Bits(score)));
                }
            }
        }

        int exhaustivePages = CacheMisses(connection);

        await ColdAsync(connection);

        int rescored = 0;

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) =>
        {
            if (sql.Contains("quantized exact rescore", StringComparison.Ordinal))
            {
                rescored++;
            }
        }, null);

        Result<DivinationResult[]> actual;

        try
        {
            actual = await SearchAsync(connection, query, 10, threshold);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        int compactPages = CacheMisses(connection);

        Assert.True(actual.IsSuccess);

        Assert.Equal(expected.ToArray(), actual.Value.Select(hit => (hit.Id, BitConverter.SingleToInt32Bits(hit.Similarity))).ToArray());

        await using SqliteCommand sizes = connection.CreateCommand();

        sizes.CommandText = "SELECT SUM(length(Representation)) FROM entry_embeddings_quantized";

        long compactBytes = Convert.ToInt64(await sizes.ExecuteScalarAsync()) + (long)rescored * Dimensions * sizeof(float);

        sizes.CommandText = "PRAGMA page_size";

        long pageSize = Convert.ToInt64(await sizes.ExecuteScalarAsync());

        output.WriteLine($"8192 x 768, K=10, threshold=.70: exhaustive payload={exhaustiveBytes:N0} bytes, compact+exact payload={compactBytes:N0} bytes, exact rows={rescored}; cold pager misses={exhaustivePages} -> {compactPages}, encrypted page bytes={exhaustivePages * pageSize:N0} -> {compactPages * pageSize:N0}.");

        Assert.InRange(rescored, 2, 10);

        Assert.True(compactBytes < exhaustiveBytes * 0.30, "Compact payload plus exact rescue must approach the quantization ratio.");

        Assert.True(compactPages < exhaustivePages * 0.35, "Measure encrypted pager reads, not just logical vector bytes.");

        // The synchronous provider keeps this measurement on one thread, exactly like #172's gate.
        _ = await SearchAsync(connection, query, 10, threshold);

        long before = GC.GetAllocatedBytesForCurrentThread();

        Task<Result<DivinationResult[]>> pending = SearchAsync(connection, query, 10, threshold);

        Assert.True(pending.IsCompletedSuccessfully);

        _ = await pending;

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"Compact scan managed allocation: {allocated:N0} bytes for 8,192 rows.");

        // Row ids and bounded heap/command state remain; a compact byte[] per row alone is >6 MiB.
        Assert.True(allocated < 2 * 1024 * 1024, $"Unexpected per-row vector allocation: {allocated:N0} bytes.");
    }

    private static async Task ColdAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, "PRAGMA shrink_memory");

        Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_db_status(connection.Handle,
            SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out _, out _, 1));
    }

    private static int CacheMisses(SqliteConnection connection)
    {
        Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_db_status(connection.Handle,
            SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out int misses, out _, 0));

        return misses;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }
}
