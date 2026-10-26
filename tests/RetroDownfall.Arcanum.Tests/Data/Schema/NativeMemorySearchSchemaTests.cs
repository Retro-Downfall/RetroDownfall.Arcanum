using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class NativeMemorySearchSchemaTests
{
    [Fact]
    public void Core27_is_a_complete_frozen_catalog()
    {
        Assert.Equal(372, CoreSchemaVersionTwentySevenFixture.Objects.Count);
        Assert.Equal(CoreSchemaVersionTwentySevenFixture.PublishedFingerprint, CoreSchemaVersionTwentySevenFixture.Fingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ignored_and_replaced_source_writes_preserve_native_literal_index_parity(bool recursive)
    {
        using var file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);
        await ExecuteAsync(connection, $"PRAGMA recursive_triggers={(recursive ? 1 : 0)};" + Chunk("one", "oldtoken"));
        Assert.Equal(1L, await CountAsync(connection, "old"));
        await ExecuteAsync(connection, Chunk("one", "ignoredtoken").Replace("INSERT INTO", "INSERT OR IGNORE INTO", StringComparison.Ordinal));
        Assert.Equal(1L, await CountAsync(connection, "old"));
        Assert.Equal(0L, await CountAsync(connection, "ign"));
        await ExecuteAsync(connection, Chunk("one", "newtoken").Replace("INSERT INTO", "INSERT OR REPLACE INTO", StringComparison.Ordinal));
        Assert.Equal(0L, await CountAsync(connection, "old"));
        Assert.Equal(1L, await CountAsync(connection, "new"));
        await ExecuteAsync(connection, "UPDATE workspace_file_chunks SET ChunkId='moved', Content='changedtoken' WHERE ChunkId='one';");
        Assert.Equal(0L, await CountAsync(connection, "new"));
        Assert.Equal(1L, await CountAsync(connection, "cha"));
        await ExecuteAsync(connection, "DELETE FROM workspace_file_chunks; INSERT INTO memory_search_fts(memory_search_fts,rank) VALUES('integrity-check',1);");
        Assert.Equal(0L, await CountAsync(connection, "cha"));
    }

    private static string Chunk(string id, string content) => $"""
        INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
        VALUES('{id}','/fake/workspace','file.cs',0,'{content}',0,8,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
        """;

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> CountAsync(SqliteConnection connection, string seed)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM memory_search_fts WHERE memory_search_fts MATCH @seed";
        _ = command.Parameters.AddWithValue("@seed", "\"" + seed + "\"");
        return await command.ExecuteScalarAsync();
    }
}
