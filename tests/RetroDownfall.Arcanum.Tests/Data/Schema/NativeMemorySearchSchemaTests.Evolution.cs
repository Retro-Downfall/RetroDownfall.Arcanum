using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class NativeMemorySearchSchemaTests
{
    [Fact]
    public async Task Core28_journals_bounded_backfill_restarts_and_refuses_an_older_reader()
    {
        using var file = EvolutionScratchDatabase.Create();
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
        await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentySevenFixture.ChainSet(), 64, CancellationToken.None);
        await using (SqliteCommand seed = connection.CreateCommand())
        {
            seed.CommandText = """
                WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<805)
                INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
                SELECT printf('%064d',v),'/fake/workspace','file.cs',v,'inheritedneedle',0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM n;
                """;
            _ = await seed.ExecuteNonQueryAsync();
        }

        await ExecuteAsync(connection, """
            INSERT INTO workspace_file_chunks(rowid,ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            VALUES(-9223372036854775808,'minimum','/fake/workspace','minimum.cs',0,'minimumneedle',0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z');
            """);
        GrimoireSchemaInstallResult pending = await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentyEightFixture.ChainSet(), 64, CancellationToken.None);
        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, pending.Core.Health);
        var refusedRead = await RetroDownfall.Arcanum.Infrastructure.Data.NativeMemorySearchReader.ReadAsync(connection,
            "inheritedneedle", RetroDownfall.Arcanum.Core.Memory.MemorySearchScope.Workspace, null, null, new(3), 20, CancellationToken.None);
        Assert.True(refusedRead.IsFailure);
        Assert.Equal("MemorySearch.IndexUnavailable", refusedRead.Error.Code);
        GrimoireSchemaTransitionJournalRow journal = (await JournalAsync(connection))!;
        Assert.Equal(27, journal.CompletedThroughVersion);
        GrimoireSchemaBackfillRunner runner = new(GrimoireSchemaTestInstaller.Create(), TimeProvider.System);
        GrimoireSchemaBackfillProgress first = await runner.AdvanceAsync(connection,
            CoreSchemaVersionTwentyEightFixture.ChainSet().ForTier(GrimoireSchemaTransactionTier.Core), journal,
            GrimoireSchemaTestInstaller.CreateContext(), 4, CancellationToken.None);
        Assert.False(first.StepComplete);
        Assert.Equal(200L, await ScalarAsync(connection, "SELECT COUNT(*) FROM memory_search_documents"));
        journal = (await JournalAsync(connection))!;
        string? committedCursor = journal.BackfillCursor;
        var backfill = new MemorySearchProjectionBackfill();
        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            GrimoireSchemaBackfillBatch uncommitted = await backfill.AdvanceBatchAsync(connection, transaction, committedCursor, CancellationToken.None);
            Assert.InRange(uncommitted.RowsProcessed, 1, 200);
            await transaction.RollbackAsync();
        }
        Assert.Equal(committedCursor, (await JournalAsync(connection))!.BackfillCursor);
        Assert.Equal(200L, await ScalarAsync(connection, "SELECT COUNT(*) FROM memory_search_documents"));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.AdvanceAsync(connection,
            CoreSchemaVersionTwentyEightFixture.ChainSet().ForTier(GrimoireSchemaTransactionTier.Core), journal,
            GrimoireSchemaTestInstaller.CreateContext(), 1, cancelled.Token));
        Assert.Equal(committedCursor, (await JournalAsync(connection))!.BackfillCursor);
        await ExecuteAsync(connection, "UPDATE workspace_file_chunks SET Content='editedneedle' WHERE ChunkId=printf('%064d',805)");
        Assert.Equal(201L, await ScalarAsync(connection, "SELECT COUNT(*) FROM memory_search_documents"));
        GrimoireSchemaBackfillRunner restarted = new(GrimoireSchemaTestInstaller.Create(), TimeProvider.System);
        GrimoireSchemaBackfillProgress complete = await restarted.AdvanceAsync(connection,
            CoreSchemaVersionTwentyEightFixture.ChainSet().ForTier(GrimoireSchemaTransactionTier.Core), (await JournalAsync(connection))!,
            GrimoireSchemaTestInstaller.CreateContext(), 8, CancellationToken.None);
        Assert.True(complete.StepComplete);
        Assert.Null(await JournalAsync(connection));
        Assert.Equal(28L, await ScalarAsync(connection, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=0 AND TransactionTierCode=0"));
        Assert.Equal(804L, await CountAsync(connection, "inh"));
        Assert.Equal(1L, await CountAsync(connection, "edi"));
        Assert.Equal(1L, await CountAsync(connection, "min"));
        await ExecuteAsync(connection, "INSERT INTO memory_search_fts(memory_search_fts,rank) VALUES('integrity-check',1)");
        Exception refused = await Assert.ThrowsAnyAsync<Exception>(() => GrimoireSchemaTestInstaller.InstallAsync(connection,
            CoreSchemaVersionTwentySevenFixture.ChainSet(), 64, CancellationToken.None));
        Assert.Contains("IncompatibleNewerVersion", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Core28_every_DDL_boundary_rolls_back_to_the_frozen_Core27_schema()
    {
        GrimoireSchemaVersionChain core = CoreSchemaVersionTwentyEightFixture.ChainSet().ForTier(GrimoireSchemaTransactionTier.Core);
        GrimoireSchemaVersionStep step = Assert.Single(core.Steps, item => item.ToVersion == 28);
        foreach (GrimoireSchemaTransitionStatement stop in step.Statements)
        {
            using var file = EvolutionScratchDatabase.Create();
            await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);
            await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionTwentySevenFixture.ChainSet(), 64, CancellationToken.None);
            await ExecuteAsync(connection, Chunk("legacy", "retainedtoken"));
            GrimoireSchemaVersionStep interrupted = step with
            {
                Statements = [.. step.Statements.SelectMany(statement => statement == stop
                    ? new[] { statement, new GrimoireSchemaTransitionStatement("InjectedFailure", stop.Ordinal + 1, "InjectedFailure", "INSERT INTO MissingLiteralSearchTable VALUES(1)") }
                    : new[] { statement })],
            };
            GrimoireSchemaVersionChainSet chains = new(
            [
                new(core.HeadManifest, core.HeadObjects, [.. core.Steps.Where(item => item.ToVersion < 28), interrupted]),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            ]);
            await Assert.ThrowsAsync<SqliteException>(() => GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 64, CancellationToken.None));
            Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='memory_search_documents'"));
            Assert.Equal(27L, await ScalarAsync(connection, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=0 AND TransactionTierCode=0"));
            Assert.Equal("retainedtoken", await ScalarAsync(connection, "SELECT Content FROM workspace_file_chunks WHERE ChunkId='legacy'"));
        }
    }

    private static Task<GrimoireSchemaTransitionJournalRow?> JournalAsync(SqliteConnection connection) =>
        GrimoireSchemaTransitionJournal.ReadAsync(connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None);

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
