using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryGraphStoreTests
{
    [Fact]
    public async Task Reuse_CannotSwitchToAnUnvalidatedGenerationBetweenReads()
    {
        await ExecuteAsync("PRAGMA journal_mode=WAL");

        (string current, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(current, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        string next = await _store.BeginGenerationAsync(Scope, "unsupported", "settings", "summary-model", TapestryHash.SummaryRecipeVersion, 8, snapshot.Corpus.Fingerprint, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendGraphCheckpointAsync(new(next, [Leaf(next, "one"), Summary(next, "a", 1)], [Edge(next, "a", "one")]), CancellationToken.None);

        await using ArcanumDbContext writer = fixture.CreateContext(_path);

        await using DbCommand replace = writer.Database.GetDbConnection().CreateCommand();

        replace.CommandText = $"UPDATE tapestry_generations SET Status='Superseded' WHERE GenerationId='{current}'; UPDATE tapestry_generations SET Status='Complete' WHERE GenerationId='{next}'";

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        bool switched = false;

        raw.sqlite3_trace(connection.Handle, (object _, string sql) =>
        {
            if (!switched && sql.Contains("SELECT n.\"Content\"", StringComparison.Ordinal))
            {
                switched = true;

                _ = replace.ExecuteNonQuery();
            }
        }, null);

        try
        {
            Assert.Null(await _store.TryGetReusableSummaryAsync(Scope, "a", CancellationToken.None));
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
        }

        Assert.True(switched);
    }

    [Fact]
    public async Task Snapshot_WalWriterCannotMixNewCorpusWithOldRevision()
    {
        await ExecuteAsync("PRAGMA journal_mode=WAL;" + string.Concat(Enumerable.Range(0, 600).Select(index => Chunk($"bulk-{index:D4}"))));

        TapestryBuildSnapshot before = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        await using ArcanumDbContext writer = fixture.CreateContext(_path);

        await using DbCommand update = writer.Database.GetDbConnection().CreateCommand();

        update.CommandText = "UPDATE workspace_file_chunks SET Content='changed by writer' WHERE ChunkId='one'";

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        Task? write = null;

        using ManualResetEventSlim attempted = new();

        raw.sqlite3_progress_handler(connection.Handle, 100, (object _) =>
        {
            if (write is null)
            {
                write = Task.Run(() =>
                {
                    attempted.Set();

                    _ = update.ExecuteNonQuery();
                });

                if (!attempted.Wait(TimeSpan.FromSeconds(5)))
                {
                    return 1;
                }
            }

            return 0;
        }, null);

        TapestryBuildSnapshot snapshot;

        try
        {
            snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        Assert.NotNull(write);

        await write.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(before, snapshot);

        TapestryBuildSnapshot after = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        Assert.Equal(before.InvalidationRevision + 1, after.InvalidationRevision);

        Assert.NotEqual(before.Corpus.Fingerprint, after.Corpus.Fingerprint);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task Snapshot_CancellationDuringPagedReadRollsBackAndReleasesConnection()
    {
        await ExecuteAsync(string.Concat(Enumerable.Range(0, 600).Select(index => Chunk($"bulk-{index:D4}"))));

        using CancellationTokenSource cancellation = new();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        raw.sqlite3_progress_handler(connection.Handle, 100, (object _) => { cancellation.Cancel(); return 0; }, null);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.ReadBuildSnapshotAsync(Scope, cancellation.Token));
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        Assert.Equal(603, (await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None)).Corpus.LeafCount);
    }

    [Fact]
    public async Task Publication_CancellationAfterPromotionRollsBackTheWholeSwitch()
    {
        (string current, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(current, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        (string next, _) = await DiamondAsync();

        using CancellationTokenSource cancellation = new();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        connection.CreateFunction("cancel_publish", () => { cancellation.Cancel(); return 0L; });

        await ExecuteAsync("CREATE TRIGGER cancel_publication AFTER UPDATE OF Status ON tapestry_generations WHEN NEW.Status='Complete' BEGIN SELECT cancel_publish(); END");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.PublishGenerationAsync(new(next, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), cancellation.Token));

        Assert.Equal(current, (await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None))?.GenerationId);

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_generations WHERE Status='Building'"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_generations WHERE Status='Superseded'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_CancellationAfterEdgeRollsBackNodesVectorsAndLegacyParents(bool legacy)
    {
        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        TapestryGenerationRecipe recipe = legacy ? Recipe with { AlgorithmVersion = SphericalKMeans.AlgorithmVersion } : Recipe;

        string id = await _store.BeginGraphGenerationAsync(snapshot, recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendGraphCheckpointAsync(new(id, [Leaf(id, "one")], []), CancellationToken.None);

        using CancellationTokenSource cancellation = new();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        connection.CreateFunction("cancel_edge", () => { cancellation.Cancel(); return 0L; });

        await ExecuteAsync("CREATE TRIGGER cancel_checkpoint AFTER INSERT ON tapestry_edges BEGIN SELECT cancel_edge(); END");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.AppendGraphCheckpointAsync(new(id, [Summary(id, "a", 1)], [Edge(id, "a", "one")], legacy), cancellation.Token));

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_nodes"));

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_node_embeddings"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_edges"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_nodes WHERE ParentNodeId IS NOT NULL"));
    }
}
