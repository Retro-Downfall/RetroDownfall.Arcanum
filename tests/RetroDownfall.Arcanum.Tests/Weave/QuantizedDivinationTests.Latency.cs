using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed partial class QuantizedDivinationTests
{
    [Fact]
    [Trait("Category", "Perf")]
    public async Task Encrypted_scan_reports_cold_and_warm_latency_without_a_timing_gate()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        SqliteConnectionStringBuilder settings = new(scratch.ConnectionString)
        {
            Password = "quantized-retrieval-latency-fixture",
        };

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(settings.ConnectionString, CancellationToken.None);

        await CreateCorpusAsync(connection);

        Random random = new(170173);

        float[][] corpus = Enumerable.Range(0, 8192).Select(_ => RandomVector(random)).ToArray();

        float[] query = corpus[0].ToArray();

        corpus[1] = query.ToArray();

        await SeedAsync(connection, corpus);

        await ExecuteAsync(connection, "PRAGMA mmap_size=0");

        (string Id, int Bits)[] expected = Exhaustive(corpus, query, 10, 0.70f);

        bool compactEnabled = true;

        async Task<double> MeasureAsync(bool compact, bool cold)
        {
            if (compact != compactEnabled)
            {
                await ExecuteAsync(connection, compact
                    ? "ALTER TABLE latency_held_companion RENAME TO entry_embeddings_quantized"
                    : "ALTER TABLE entry_embeddings_quantized RENAME TO latency_held_companion");

                compactEnabled = compact;
            }

            if (cold)
            {
                await ColdAsync(connection);
            }

            long started = Stopwatch.GetTimestamp();

            var result = await SearchAsync(connection, query, 10, 0.70f);

            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            Assert.True(result.IsSuccess);

            Assert.Equal(expected, result.Value.Select(hit => (hit.Id, BitConverter.SingleToInt32Bits(hit.Similarity))).ToArray());

            return milliseconds;
        }

        foreach (bool cold in new[] { true, false })
        {
            await ExecuteAsync(connection, cold ? "PRAGMA cache_size=-512" : "PRAGMA cache_size=-131072");

            // Warm both code paths before measurement. The cold case still clears the SQLite
            // pager before each timed call; OS cache state is intentionally not claimed cold.
            _ = await MeasureAsync(false, cold);

            _ = await MeasureAsync(true, cold);

            List<double> exhaustive = [];

            List<double> compact = [];

            for (int iteration = 0; iteration < 7; iteration++)
            {
                exhaustive.Add(await MeasureAsync(false, cold));

                compact.Add(await MeasureAsync(true, cold));
            }

            exhaustive.Sort();

            compact.Sort();

            output.WriteLine($"8192 x 768, K=10, threshold=.70, {(cold ? "cold 512KiB pager" : "warm 128MiB pager")}: median exhaustive={exhaustive[3]:F2} ms, compact={compact[3]:F2} ms; seven exact paired queries, no timing assertion.");
        }
    }
}
