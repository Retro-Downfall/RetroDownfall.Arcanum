using System.Data.Common;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class SagaQuantizedTransactionTests
{
    [SkippableTheory]
    [InlineData(false, "delete")]
    [InlineData(true, "delete")]
    [InlineData(false, "delete-all")]
    [InlineData(true, "delete-all")]
    [InlineData(false, "retire")]
    [InlineData(true, "retire")]
    public async Task Removing_authoritative_saga_embeddings_removes_compact_copies_with_either_foreign_key_mode(bool foreignKeys, string operation)
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        const string content = "remove both embedding copies";

        await harness.Store.InsertAsync("removed", content, DateTimeOffset.UtcNow, null, null, null,
            harness.Embedding(), CancellationToken.None);

        Assert.Equal(1, await harness.CountAsync("saga_memory_embeddings_quantized", "MemoryId='removed'"));

        await using DbCommand command = harness.Connection.CreateCommand();

        command.CommandText = foreignKeys ? "PRAGMA foreign_keys=ON" : "PRAGMA foreign_keys=OFF";

        await command.ExecuteNonQueryAsync();

        switch (operation)
        {
            case "delete":
                Assert.True(await harness.Store.DeleteAsync("removed", CancellationToken.None));

                break;

            case "delete-all":
                await harness.Store.DeleteAllAsync(CancellationToken.None);

                break;

            case "retire":
                _ = await harness.Store.RetireAsync("removed", AnnalContentDigest.ForSagaMemory(content), DateTimeOffset.UtcNow, CancellationToken.None);

                break;
        }

        Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings", "MemoryId='removed'"));

        Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings_quantized", "MemoryId='removed'"));
    }

    [SkippableFact]
    public async Task Failed_companion_insert_rolls_back_the_production_memory_and_authoritative_embedding()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await RefuseCompactWritesAsync(harness.Connection);

        Exception? failure = await Record.ExceptionAsync(() => harness.Store.InsertAsync(
            "atomic-new", "new memory", DateTimeOffset.UtcNow, null, null, null,
            harness.Embedding(), CancellationToken.None));

        Assert.NotNull(failure);

        Assert.Equal(0, await harness.CountAsync("saga_memories", "Id='atomic-new'"));

        Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings", "MemoryId='atomic-new'"));

        Assert.Equal(0, await harness.CountAsync("saga_memory_embeddings_quantized", "MemoryId='atomic-new'"));

        Assert.IsType<SqliteException>(failure);
    }

    [SkippableFact]
    public async Task Failed_companion_replacement_restores_the_production_content_vector_and_old_projection()
    {
        await using SagaStoreHarness harness = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        const string original = "the operator prefers tabs";

        await harness.Store.InsertAsync("atomic-existing", original, DateTimeOffset.UtcNow, null, null, null,
            harness.Embedding(seed: 1), CancellationToken.None);

        byte[] beforeEmbedding = await harness.EmbeddingBytesAsync("atomic-existing");

        byte[] beforeCompact = await CompactAsync(harness.Connection);

        await RefuseCompactWritesAsync(harness.Connection);

        Exception? failure = await Record.ExceptionAsync(() => harness.Store.CorrectAsync(
            "atomic-existing", AnnalContentDigest.ForSagaMemory(original), "the operator prefers spaces",
            harness.Embedding(seed: 2), DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.NotNull(failure);

        Assert.Equal(original, (await harness.Store.ReadCurationRowAsync("atomic-existing", CancellationToken.None))!.Memory.Content);

        Assert.Equal(beforeEmbedding, await harness.EmbeddingBytesAsync("atomic-existing"));

        Assert.Equal(beforeCompact, await CompactAsync(harness.Connection));

        Assert.IsType<SqliteException>(failure);
    }

    private static async Task RefuseCompactWritesAsync(DbConnection connection)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            CREATE TEMP TRIGGER refuse_compact BEFORE INSERT ON main.saga_memory_embeddings_quantized
            BEGIN SELECT RAISE(ABORT, 'injected compact failure'); END;
            """;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<byte[]> CompactAsync(DbConnection connection)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = "SELECT Representation FROM saga_memory_embeddings_quantized WHERE MemoryId='atomic-existing'";

        return (byte[])(await command.ExecuteScalarAsync())!;
    }
}
