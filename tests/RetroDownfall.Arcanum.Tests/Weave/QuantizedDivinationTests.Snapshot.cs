using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed partial class QuantizedDivinationTests
{
    [Fact]
    public async Task Compact_scan_and_authoritative_rescue_share_a_snapshot_during_concurrent_replacement()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL");

        await CreateCorpusAsync(connection);

        float[][] corpus = Enumerable.Repeat(Axis(1), 2048).ToArray();

        corpus[0] = Axis(0);

        await SeedAsync(connection, corpus);

        await using SqliteConnection writer = await scratch.OpenAsync(CancellationToken.None);

        bool changed = false;

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) =>
        {
            if (changed || !sql.Contains("quantized exact rescore", StringComparison.Ordinal))
            {
                return;
            }

            // The compact cursor has stepped and established its read snapshot before the first
            // rescue. WAL lets another admitted connection commit without waiting for that reader.
            changed = true;

            using SqliteCommand update = writer.CreateCommand();

            update.CommandText = "UPDATE entry_embeddings SET Embedding=@vector WHERE EntryId='00000000'; DELETE FROM entry_embeddings_quantized WHERE EntryId='00000000';";

            update.Parameters.AddWithValue("@vector", EmbeddingBlobCodec.Encode(Axis(1)));

            update.ExecuteNonQuery();
        }, null);

        Result<DivinationResult[]> before;

        try
        {
            before = await SearchAsync(connection, Axis(0), 1, 0.5f);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(changed);

        Assert.True(before.IsSuccess);

        Assert.Equal("00000000", Assert.Single(before.Value).Id);

        Assert.Equal(1f, before.Value[0].Similarity);

        Result<DivinationResult[]> after = await SearchAsync(connection, Axis(0), 1, 0.5f);

        Assert.True(after.IsSuccess);

        Assert.Empty(after.Value);
    }

    [Fact]
    public async Task Missing_companion_table_and_swapped_owner_rows_fall_back_to_authoritative_vectors()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await CreateCorpusAsync(connection);

        float[][] corpus = Enumerable.Repeat(Axis(1), 2048).ToArray();

        corpus[1] = Axis(0);

        await SeedAsync(connection, corpus);

        await ExecuteAsync(connection, "UPDATE entry_embeddings_quantized SET Representation=(SELECT Representation FROM entry_embeddings_quantized WHERE EntryId='00000000') WHERE EntryId='00000001';");

        Result<DivinationResult[]> swapped = await SearchAsync(connection, Axis(0), 1, 0.5f);

        Assert.True(swapped.IsSuccess);

        Assert.Equal("00000001", Assert.Single(swapped.Value).Id);

        await ExecuteAsync(connection, "DROP TABLE entry_embeddings_quantized");

        Result<DivinationResult[]> missing = await SearchAsync(connection, Axis(0), 1, 0.5f);

        Assert.True(missing.IsSuccess);

        Assert.Equal(swapped.Value.Select(hit => (hit.Id, hit.Similarity)), missing.Value.Select(hit => (hit.Id, hit.Similarity)));
    }
}
