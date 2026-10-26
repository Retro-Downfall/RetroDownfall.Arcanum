using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class NativeMemorySearchReaderTests
{
    [Fact]
    public async Task Deep_tied_mixed_direction_pages_bound_native_work_and_eventually_reach_the_last_hit()
    {
        using var file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<16000)
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            SELECT printf('%064d',v),'/fake/workspace','same-path.cs',0,CASE WHEN v=16000 THEN 'z' ELSE 'ordinary' END,0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM n;
            """;
        _ = await command.ExecuteNonQueryAsync();
        MemorySearchPosition position = new(3);
        List<string> hits = [];
        List<long> work = [];
        List<string> statements = [];
        for (int pageNumber = 0; pageNumber < 40; pageNumber++)
        {
            long steps = 0;
            raw.sqlite3_progress_handler(connection.Handle, 1, (object _) => { steps++; return 0; }, null);
            raw.sqlite3_trace(connection.Handle, (object _, string sql) => { if (sql.Contains("SELECT d.rowid", StringComparison.Ordinal)) statements.Add(sql); }, null);
            NativeMemorySearchPage page;
            try
            {
                page = await ReadNativeAsync(connection, "z", MemorySearchScope.Workspace, null,
                    ("/fake/workspace", "Fake workspace"), position, 20, CancellationToken.None);
            }
            finally
            {
                raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
                raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
            }
            work.Add(steps);
            hits.AddRange(page.Hits.Select(hit => hit.Result.SourceId));
            if (page.Reason is null) break;
            position = page.Position;
        }
        Assert.Equal(new[] { 16000.ToString("D64", System.Globalization.CultureInfo.InvariantCulture) }, hits);
        Assert.Equal(32, work.Count);
        output.WriteLine($"Native deep-tie traversal: rows=16000, pages={work.Count}, VM per page=[{string.Join(',', work)}].");
        output.WriteLine("Actual metadata SQL: " + statements.First());
        output.WriteLine("Actual hydration SQL: " + statements.First(sql => sql.StartsWith("WITH selected", StringComparison.Ordinal)));
        Assert.All(work, steps => Assert.InRange(steps, 1, 40000));
        Assert.Contains(statements, sql => sql.Contains("ChunkIndex=", StringComparison.Ordinal) && sql.Contains("ChunkId>", StringComparison.Ordinal));
        foreach (string sql in statements)
        {
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            List<string> plan = [];
            while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
            if (sql.StartsWith("WITH selected", StringComparison.Ordinal))
            {
                output.WriteLine("Actual bounded hydration plan: " + string.Join(" | ", plan));
                Assert.Contains(plan, line => line.Contains("MATERIALIZE selected", StringComparison.Ordinal));
                Assert.Contains(plan, line => line.Contains("SEARCH d USING INTEGER PRIMARY KEY", StringComparison.Ordinal));
            }
            else
            {
                Assert.Contains(plan, line => line.Contains("idx_memory_search_workspace_file_chunks_owner", StringComparison.Ordinal));
                Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task Distributed_capped_seeds_find_the_complete_sparse_set_when_the_first_seed_is_globally_dense()
    {
        using var file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);
        string query = "commontoken " + new string('x', 180) + " xyzrare";
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<1000)
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            SELECT printf('%064d',v),'/fake/workspace','same.cs',v,@common||CASE WHEN v=1000 THEN ' xyzrare' ELSE '' END,0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM n;
            """;
        _ = command.Parameters.AddWithValue("@common", "commontoken " + new string('x', 180));
        _ = await command.ExecuteNonQueryAsync();
        int seeds = 0;
        raw.sqlite3_trace(connection.Handle, (object _, string sql) => { if (sql.StartsWith("SELECT rowid FROM memory_search_fts", StringComparison.Ordinal)) seeds++; }, null);
        NativeMemorySearchPage page;
        try
        {
            page = await ReadNativeAsync(connection, query, MemorySearchScope.Workspace, null, null, new(3), 20, CancellationToken.None);
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }
        Assert.Single(page.Hits);
        Assert.Null(page.Reason);
        Assert.Equal(3, seeds);
        output.WriteLine($"Distributed complete-candidate search: source rows=1000, seed probes={seeds}, result rows={page.Hits.Length}.");
    }

    [Fact]
    public async Task Native_read_cancellation_propagates_without_an_index_failure_result()
    {
        using var file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeMemorySearchReader.ReadAsync(connection,
            "needle", MemorySearchScope.Workspace, null, null, new(3), 20, cancelled.Token));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(16000)]
    public async Task Null_summaries_do_not_hide_unbounded_native_work_before_the_selected_window(int count)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<@count)
            INSERT INTO Sessions(Id,Title,CreatedAt,UpdatedAt,Summary)
            SELECT printf('00000000-0000-4000-8000-%012X',v),'ordinary','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z',
                CASE WHEN v=1 THEN 'a' ELSE NULL END FROM n;
            """;

        _ = command.Parameters.AddWithValue("@count", count);

        _ = await command.ExecuteNonQueryAsync();

        long steps = 0;

        string? metadata = null;

        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) => { steps++; return 0; }, null);

        raw.sqlite3_trace(connection.Handle, (object _, string sql) => { if (sql.StartsWith("SELECT d.rowid", StringComparison.Ordinal)) metadata = sql; }, null);

        NativeMemorySearchPage page;

        try
        {
            page = await ReadNativeAsync(connection, "a", MemorySearchScope.Session, null, null, new(1), 20, CancellationToken.None);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);

            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        Assert.Equal("00000000-0000-4000-8000-000000000001", Assert.Single(page.Hits).Result.SourceId);

        Assert.Null(page.Reason);

        output.WriteLine($"Null-summary native fallback: source Sessions={count}, searchable summaries=1, VM steps={steps}; actual metadata SQL={metadata}.");

        Assert.InRange(steps, 1, 2000);

        command.CommandText = "EXPLAIN QUERY PLAN " + Assert.IsType<string>(metadata);

        await using SqliteDataReader planReader = await command.ExecuteReaderAsync();

        List<string> plan = [];

        while (await planReader.ReadAsync()) plan.Add(planReader.GetString(3));

        output.WriteLine("Actual summary-window plan: " + string.Join(" | ", plan));

        Assert.Contains(plan, line => line.Contains("idx_memory_search_Sessions_order", StringComparison.Ordinal));

        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }
}
