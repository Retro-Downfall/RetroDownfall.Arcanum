using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed partial class QuantizedDivinationTests
{
    [Fact]
    public async Task Encrypted_shuffled_guid_scan_preserves_clustered_companion_page_savings()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        SqliteConnectionStringBuilder settings = new(scratch.ConnectionString)
        {
            Password = "quantized-shuffled-guid-fixture",
        };

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(settings.ConnectionString, CancellationToken.None);

        await CreateCorpusAsync(connection);

        Random random = new(170173);

        float[][] corpus = Enumerable.Range(0, 8192).Select(_ => RandomVector(random)).ToArray();

        corpus[0] = Axis(0);

        corpus[1] = Axis(1);

        corpus[1][0] = 2f;

        string[] identities = new string[corpus.Length];

        for (int index = 0; index < identities.Length; index++)
        {
            byte[] bytes = new byte[16];

            random.NextBytes(bytes);

            identities[index] = new Guid(bytes).ToString("D");
        }

        await SeedAsync(connection, corpus, index => identities[index]);

        await ExecuteAsync(connection, "PRAGMA mmap_size=0; PRAGMA cache_size=-512; ALTER TABLE entry_embeddings_quantized RENAME TO held_companion;");

        await ColdAsync(connection);

        var expected = await SearchAsync(connection, Axis(0), 10, 0.70f);

        int exhaustivePages = CacheMisses(connection);

        await ExecuteAsync(connection, "ALTER TABLE held_companion RENAME TO entry_embeddings_quantized");

        await ColdAsync(connection);

        string? compactSql = null;

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) =>
        {
            if (sql.Contains("LEFT JOIN", StringComparison.Ordinal))
            {
                compactSql = sql;
            }
        }, null);

        var actual = await SearchAsync(connection, Axis(0), 10, 0.70f);

        int compactPages = CacheMisses(connection);

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);

        Assert.True(expected.IsSuccess);

        Assert.True(actual.IsSuccess);

        Assert.Equal(expected.Value.Select(hit => (hit.Id, BitConverter.SingleToInt32Bits(hit.Similarity))),
            actual.Value.Select(hit => (hit.Id, BitConverter.SingleToInt32Bits(hit.Similarity))));

        output.WriteLine($"8192 shuffled GUIDs, clustered companion: cold misses {exhaustivePages} -> {compactPages}.");

        Assert.True(compactPages < exhaustivePages * 0.35,
            "Random GUID insertion order must retain encrypted page savings with a 512 KiB cache.");

        Assert.NotNull(compactSql);

        output.WriteLine(string.Join(System.Environment.NewLine, await ExplainAsync(connection, compactSql)));
    }
}
