using System.Reflection;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(32, false, 0)]
    [InlineData(8192, false, 0)]
    [InlineData(8192, true, 0)]
    [InlineData(32, false, 512)]
    [InlineData(8192, false, 512)]
    public async Task Workspace_reset_preview_work_does_not_scan_unrelated_nodes(int unrelated, bool candidateIndexControl, int targetNodes)
    {
        RequireSqlCipher();

        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        await SeedWorkspaceResetUnrelatedNodesAsync(graph.SiblingRoot + "-bulk", unrelated);

        if (targetNodes > 0)
        {
            await SeedWorkspaceResetUnrelatedNodesAsync(graph.TargetRoot, targetNodes, "Superseded");
        }

        if (candidateIndexControl)
        {
            await ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_tapestry_nodes_scope ON tapestry_nodes(ScopeKind, ScopeId, NodeId);");
        }

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        int steps = 0;

        using SqliteStatementTrace trace = new(connection);

        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) => { steps++; return 0; }, null);

        DataRetentionPlan plan;

        try
        {
            plan = await CreateService().PlanAsync(WorkspaceResetRequest(graph));
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        Assert.Empty(plan.Conflicts);

        Assert.Empty(plan.Blockers);

        Assert.Equal(1, plan.Rows);

        Console.WriteLine($"Workspace preview unrelated={unrelated}; candidateIndex={candidateIndexControl}; targetNodes={targetNodes}; VM={steps}; statements={trace.Count}");

        Assert.True(steps < 12_000 + targetNodes * 64, $"Preview of a fixed workspace used {steps} VM steps with {unrelated} unrelated nodes.");

        string[] nodeReads = trace.Statements.Where(static sql => sql.Contains("FROM tapestry_nodes", StringComparison.Ordinal)
            && sql.Contains("ScopeKind = 'Workspace' AND ScopeId", StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(nodeReads);

        foreach (string sql in nodeReads)
        {
            await using SqliteCommand explain = connection.CreateCommand();

            explain.CommandText = "EXPLAIN QUERY PLAN " + sql;

            await using SqliteDataReader reader = await explain.ExecuteReaderAsync();

            List<string> details = [];

            while (await reader.ReadAsync())
            {
                details.Add(reader.GetString(3));
            }

            Assert.Contains(details, static detail => detail.Contains("USING COVERING INDEX idx_tapestry_nodes_scope", StringComparison.Ordinal));

            Assert.DoesNotContain(details, static detail => detail.Contains("TEMP B-TREE", StringComparison.Ordinal));
        }
    }

    [SkippableFact]
    public async Task Workspace_reset_reconciliation_reads_counts_without_repeating_authority_fingerprint()
    {
        RequireSqlCipher();

        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = WorkspaceResetRequest(graph);

        DataRetentionPlan plan = await service.PlanAsync(request);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        using SqliteStatementTrace trace = new(connection);

        var applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.True(applied.Value.Reconciled);

        string[] statements = trace.Statements.ToArray();

        int delete = Array.FindIndex(statements, static sql => sql.Contains("DELETE FROM \"WorkspaceContexts\"", StringComparison.Ordinal));

        Assert.True(delete >= 0);

        int begin = Array.FindLastIndex(statements, delete, static sql => sql.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase));

        int commit = Array.FindIndex(statements, delete, static sql => sql.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase));

        Assert.True(begin >= 0 && commit > delete);

        Assert.Contains(statements[begin..delete], IsWorkspaceResetAuthorityRead);

        string[] postCommitFingerprints = statements[(commit + 1)..].Where(IsWorkspaceResetAuthorityRead).ToArray();

        Assert.Empty(postCommitFingerprints);

        Assert.Equal(1, await CountAsync("tapestry_nodes", "NodeId", graph.SiblingNodeId));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workspace_reset_recovery_existence_reads_only_inventory(bool emptied)
    {
        RequireSqlCipher();

        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        IDataRetentionService service = CreateService();

        if (emptied)
        {
            DataRetentionRequest request = WorkspaceResetRequest(graph);

            DataRetentionPlan plan = await service.PlanAsync(request);

            Assert.True((await service.ApplyAsync(new(request, plan.PlanId))).IsSuccess);
        }

        Type journalType = typeof(DataRetentionService).GetNestedType("RetentionMutationJournal", BindingFlags.NonPublic)!;

        Type entryType = typeof(DataRetentionService).GetNestedType("RetentionMutationJournalEntry", BindingFlags.NonPublic)!;

        object journal = Activator.CreateInstance(journalType,
            ["reset-workspace", graph.TargetCampaignId.ToString("N") + ":" + graph.TargetRoot, Array.CreateInstance(entryType, 0)])!;

        MethodInfo exists = typeof(DataRetentionService).GetMethod("MutationTargetExistsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        using SqliteStatementTrace trace = new(connection);

        Task<bool> task = (Task<bool>)exists.Invoke(service, [journal, CancellationToken.None, null])!;

        Assert.Equal(!emptied, await task);

        Assert.DoesNotContain(trace.Statements, IsWorkspaceResetAuthorityRead);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workspace_reset_preserves_both_legacy_scope_contradiction_directions(bool foreignNodeInTargetGeneration)
    {
        RequireSqlCipher();

        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        string selectedNode = foreignNodeInTargetGeneration ? graph.TargetNodeId : graph.SiblingNodeId;

        string selectedScope = foreignNodeInTargetGeneration ? graph.SiblingRoot : graph.TargetRoot;

        // Inject an already-corrupt legacy row, then restore the exact guard before exercising
        // current planning and mutation. Current writes must never manufacture this mismatch.
        await using (SqliteCommand guard = connection.CreateCommand())
        {
            guard.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'tapestry_nodes_guard_update'";

            string ddl = Assert.IsType<string>(await guard.ExecuteScalarAsync());

            await ExecuteAsync("DROP TRIGGER tapestry_nodes_guard_update;");

            try
            {
                await ExecuteAsync("UPDATE tapestry_nodes SET ScopeId = @scope WHERE NodeId = @node;",
                    ("@scope", selectedScope), ("@node", selectedNode));
            }
            finally
            {
                await ExecuteAsync(ddl);
            }

            Assert.Equal(ddl, await guard.ExecuteScalarAsync());
        }

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = WorkspaceResetRequest(graph);

        DataRetentionPlan plan = await service.PlanAsync(request);

        if (foreignNodeInTargetGeneration)
        {
            Assert.Contains(plan.Conflicts, static row => row.Code == ErrorCodes.Data.Conflict);
        }
        else
        {
            Assert.Empty(plan.Conflicts);
        }

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(foreignNodeInTargetGeneration ? ErrorCodes.Data.Conflict : ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        Assert.Equal(1, await CountAsync("tapestry_nodes", "NodeId", selectedNode));

        Assert.Equal(1, await CountAsync("tapestry_generations", "GenerationId", graph.SiblingGenerationId));

        Assert.Equal(foreignNodeInTargetGeneration ? 1 : 0,
            await CountAsync("tapestry_generations", "GenerationId", graph.TargetGenerationId));
    }

    [SkippableTheory]
    [InlineData(32)]
    [InlineData(8192)]
    public async Task Workspace_reset_absent_inventory_and_actual_mutation_use_scope_index(int unrelated)
    {
        RequireSqlCipher();

        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        await SeedWorkspaceResetUnrelatedNodesAsync(graph.SiblingRoot + "-bulk", unrelated);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = WorkspaceResetRequest(graph);

        DataRetentionPlan plan = await service.PlanAsync(request);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        using SqliteStatementTrace trace = new(connection);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.Reconciled);

        string[] actual = trace.Statements.Where(static sql => sql.Contains("FROM tapestry_nodes", StringComparison.Ordinal)
            && sql.Contains("ScopeKind = 'Workspace' AND ScopeId", StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(actual);

        foreach (string sql in actual)
        {
            await using SqliteCommand explain = connection.CreateCommand();

            explain.CommandText = "EXPLAIN QUERY PLAN " + sql;

            await using SqliteDataReader reader = await explain.ExecuteReaderAsync();

            List<string> details = [];

            while (await reader.ReadAsync())
            {
                details.Add(reader.GetString(3));
            }

            Assert.Contains(details, static detail => detail.Contains("USING COVERING INDEX idx_tapestry_nodes_scope", StringComparison.Ordinal));
        }

        int steps = 0;

        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) => { steps++; return 0; }, null);

        try
        {
            DataRetentionPlan empty = await service.PlanAsync(request);

            Assert.Equal(0, empty.Rows);

            Assert.Equal(0, empty.DerivedRecords);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        Console.WriteLine($"Absent workspace preview unrelated={unrelated}; VM={steps}");

        Assert.True(steps < 12_000, $"Empty workspace preview used {steps} VM steps with {unrelated} unrelated nodes.");

        Assert.Equal(1, await CountAsync("tapestry_nodes", "NodeId", graph.SiblingNodeId));
    }

    [SkippableFact]
    public async Task Workspace_reset_cancelled_recovery_inventory_preserves_target_and_cancellation()
    {
        RequireSqlCipher();

        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        IDataRetentionService service = CreateService();

        Type journalType = typeof(DataRetentionService).GetNestedType("RetentionMutationJournal", BindingFlags.NonPublic)!;

        Type entryType = typeof(DataRetentionService).GetNestedType("RetentionMutationJournalEntry", BindingFlags.NonPublic)!;

        object journal = Activator.CreateInstance(journalType,
            ["reset-workspace", graph.TargetCampaignId.ToString("N") + ":" + graph.TargetRoot, Array.CreateInstance(entryType, 0)])!;

        MethodInfo exists = typeof(DataRetentionService).GetMethod("MutationTargetExistsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        using CancellationTokenSource cancelled = new();

        await cancelled.CancelAsync();

        Task<bool> task = (Task<bool>)exists.Invoke(service, [journal, cancelled.Token, null])!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);

        Assert.Equal(1, await CountAsync("tapestry_nodes", "NodeId", graph.TargetNodeId));
    }

    [SkippableFact]
    public async Task Workspace_scope_index_has_bounded_native_write_and_page_cost()
    {
        RequireSqlCipher();

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await ExecuteAsync("DROP INDEX idx_tapestry_nodes_scope;");

        int without = 0;

        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) => { without++; return 0; }, null);

        try
        {
            await SeedWorkspaceResetUnrelatedNodesAsync("/fixed-length-scope-A", 4096);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        long before = await WorkspaceResetAllocatedPagesAsync(connection);

        await ExecuteAsync("CREATE INDEX idx_tapestry_nodes_scope ON tapestry_nodes(ScopeKind, ScopeId, NodeId);");

        long indexPages = await WorkspaceResetAllocatedPagesAsync(connection) - before;

        int with = 0;

        raw.sqlite3_progress_handler(connection.Handle, 1, (object _) => { with++; return 0; }, null);

        try
        {
            await SeedWorkspaceResetUnrelatedNodesAsync("/fixed-length-scope-B", 4096);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        Console.WriteLine($"Scope index 4096-row native write steps without={without}; with={with}; index pages={indexPages}");

        Assert.InRange(indexPages, 1, 256);

        Assert.True(with > without && with < without * 2, $"Scope index changed native write work from {without} to {with} VM steps.");
    }

    private static async Task<long> WorkspaceResetAllocatedPagesAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT (SELECT page_count FROM pragma_page_count) - (SELECT freelist_count FROM pragma_freelist_count)";

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsWorkspaceResetAuthorityRead(string sql) =>
        sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
        && (sql.Contains("FROM tapestry_", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("FROM workspace_file_", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("FROM \"WorkspaceContexts\"", StringComparison.OrdinalIgnoreCase));

    private async Task SeedWorkspaceResetUnrelatedNodesAsync(string scope, int count, string status = "Complete")
    {
        string generation = "unrelated-generation-" + Guid.NewGuid().ToString("N");

        await ExecuteAsync(
            """
            INSERT INTO tapestry_generations
                (GenerationId, ScopeKind, ScopeId, Status, AlgorithmVersion, SettingsFingerprint,
                 SummaryModel, SummaryRecipeVersion, EmbeddingDimension, CorpusFingerprint,
                 LayerCount, NodeCount, RootNodeCount, StartedAt, CompletedAt)
            VALUES (@generation, 'Workspace', @scope, 'Building', '1', 'SETTINGS', 'test-model', '1', 1,
                'CORPUS', 1, @count, @count, @at, @at);
            WITH RECURSIVE numbers(i) AS (VALUES(1) UNION ALL SELECT i + 1 FROM numbers WHERE i < @count)
            INSERT INTO tapestry_nodes
                (NodeId, GenerationId, ScopeKind, ScopeId, Layer, ParentScopeKey, NodeKind,
                 SourceLabel, Content, ContentHash, EmbeddingDimension, CreatedAt)
            SELECT @generation || '-' || i, @generation, 'Workspace', @scope, 0, @scope, 'Summary',
                'unrelated summary', 'summary', 'HASH', 1, @at FROM numbers;
            UPDATE tapestry_generations SET Status = @status WHERE GenerationId = @generation;
            """,
            ("@generation", generation), ("@scope", scope), ("@count", count), ("@at", OldTimestamp), ("@status", status));
    }
}
