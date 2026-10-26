using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class TapestryRevisionSchemaTests
{
    [Fact]
    public void Capture_approved_core_twenty_six_before_edit()
    {
        Assert.Equal(CoreSchemaVersionTwentySixFixture.PublishedFingerprint, CoreSchemaVersionTwentySixFixture.Fingerprint);
    }

    [Fact]
    public async Task Pure_append_then_edit_and_move_have_distinct_revision_effects()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, Chunk("a", "/scope", "one"));

        Assert.Equal("1:0", await RevisionsAsync(connection, "/scope"));

        await ExecuteAsync(connection, "UPDATE workspace_file_chunks SET IndexedAt='2026-01-02T00:00:00.0000000Z', Content=Content WHERE ChunkId='a';");

        Assert.Equal("1:0", await RevisionsAsync(connection, "/scope"));

        await ExecuteAsync(connection, "UPDATE workspace_file_chunks SET Content='two' WHERE ChunkId='a';");

        Assert.Equal("2:1", await RevisionsAsync(connection, "/scope"));

        await ExecuteAsync(connection, "UPDATE workspace_file_chunks SET WorkspacePath='/other' WHERE ChunkId='a';");

        Assert.Equal("3:2", await RevisionsAsync(connection, "/scope"));

        Assert.Equal("1:1", await RevisionsAsync(connection, "/other"));
    }

    [Fact]
    public async Task Replacement_conflict_invalidates_even_when_recursive_triggers_are_off()
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await ExecuteAsync(connection, "PRAGMA recursive_triggers=OFF;" + Chunk("a", "/scope", "one") + Chunk("b", "/unrelated", "safe"));

        await ExecuteAsync(connection, Chunk("a", "/other", "two").Replace("INSERT INTO", "INSERT OR REPLACE INTO", StringComparison.Ordinal));

        Assert.Equal("2:1", await RevisionsAsync(connection, "/scope"));

        Assert.Equal("1:1", await RevisionsAsync(connection, "/other"));

        Assert.Equal("1:0", await RevisionsAsync(connection, "/unrelated"));
    }

    private static string Chunk(string id, string scope, string content) => $"""
        INSERT INTO workspace_file_chunks (ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
        VALUES ('{id}','{scope}','a.cs',0,'{content}',0,3,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
        """;

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> RevisionsAsync(SqliteConnection connection, string scope, string kind = "Workspace")
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT CorpusRevision || ':' || InvalidationRevision FROM tapestry_scope_revisions WHERE ScopeKind=$kind AND ScopeId=$scope";

        _ = command.Parameters.AddWithValue("$scope", scope);

        _ = command.Parameters.AddWithValue("$kind", kind);

        return await command.ExecuteScalarAsync();
    }
}
