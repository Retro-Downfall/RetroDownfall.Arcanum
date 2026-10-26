using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class NativeMemorySearchReaderTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(1000)]
    [InlineData(16000)]
    public async Task Sparse_absent_search_proves_exhaustion_without_reading_a_source_window(int count)
    {
        using var file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<@count)
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            SELECT printf('%064d',n),'/fake/workspace','file.cs',n,'commontoken retained content',0,10,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM numbers;
            """;
        _ = command.Parameters.AddWithValue("@count", count);
        _ = await command.ExecuteNonQueryAsync();
        long steps = 0;
        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) => { steps++; return 0; }, null);
        NativeMemorySearchPage page;
        try
        {
            page = await ReadNativeAsync(connection, "qqqmissing", MemorySearchScope.Workspace,
                null, null, new(3), 20, CancellationToken.None);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }
        Assert.Empty(page.Hits);
        Assert.Null(page.Reason);
        Assert.InRange(steps, 1, 2000);
        output.WriteLine($"Native sparse absence: rows={count}, VM steps={steps}.");
    }

    private static async Task<NativeMemorySearchPage> ReadNativeAsync(
        System.Data.Common.DbConnection connection, string query, MemorySearchScope scope, Guid? sessionId,
        (string Path, string Label)? workspace, MemorySearchPosition start, int limit, CancellationToken cancellationToken)
    {
        var result = await NativeMemorySearchReader.ReadAsync(connection, query, scope, sessionId, workspace, start, limit, cancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        return result.Value;
    }
}
