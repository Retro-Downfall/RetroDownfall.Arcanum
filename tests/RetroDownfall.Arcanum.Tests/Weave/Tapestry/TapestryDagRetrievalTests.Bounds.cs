using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryGraphStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteGenerationScan_Beyond4096_IsRankedOrExplicitlyIncomplete(bool lastRanksFirst)
    {
        string id = await PublishWideDagAsync(4100, lastRanksFirst);

        var access = await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity, CancellationToken.None);

        var graph = await _store.ReadRetrievalGraphAsync(access.Value, CancellationToken.None);

        var ranked = await _store.RankRetrievalNodesAsync(access.Value, DagQuery, graph.Value, null, 4096, CancellationToken.None);

        Assert.True(ranked.IsSuccess);

        Assert.Equal(4100, ranked.Value.ScoredCount);

        Assert.True(ranked.Value.Truncated);

        var result = await Retriever().RetrieveAsync(DagRequest(maxNodes: 1, maxBytes: 1500), CancellationToken.None);

        if (lastRanksFirst)
        {
            Assert.True(result.IsSuccess);

            Assert.Equal(id + "wide4099", Assert.Single(result.Value).NodeId);
        }
        else
        {
            Assert.True(result.IsFailure);

            Assert.Equal("Tapestry.CandidateLimitReached", result.Error.Code);
        }
    }

    [Fact]
    public async Task Beyond512Rows_RejectedCandidatesBackfill_AndCancellationReleasesReader()
    {
        string id = await PublishWideDagAsync(600, false);

        Assert.Equal([id + "wide0599"], await RetrieveIdsAsync(DagRequest(maxNodes: 1, maxBytes: 1500)));

        var access = await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity, CancellationToken.None);

        var graph = await _store.ReadRetrievalGraphAsync(access.Value, CancellationToken.None);

        using CancellationTokenSource cancellation = new();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        raw.sqlite3_progress_handler(connection.Handle, 100, (object _) => { cancellation.Cancel(); return 0; }, null);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.RankRetrievalNodesAsync(access.Value, DagQuery, graph.Value, null, 32, cancellation.Token));
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        var after = await _store.RankRetrievalNodesAsync(access.Value, DagQuery, graph.Value, null, 32, CancellationToken.None);

        Assert.True(after.IsSuccess);

        Assert.Equal(600, after.Value.ScoredCount);
    }

    private async Task<string> PublishWideDagAsync(int count, bool lastRanksFirst)
    {
        await ExecuteAsync("DELETE FROM workspace_file_chunks");

        string large = new('x', 2048);

        await ExecuteAsync($"""
            WITH RECURSIVE numbers(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM numbers WHERE n<{count - 1})
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt)
            SELECT printf('wide%04d',n),'/graph','a.cs',n,CASE WHEN n={count - 1} THEN 'tail' ELSE '{large}' END,0,2048,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM numbers
            """);

        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        List<TapestryNodeWrite> nodes = [];

        for (int index = 0; index < count; index++)
        {
            string source = "wide" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

            TapestryNodeWrite node = Leaf(id, source);

            nodes.Add(node with
            {
                Node = node.Node with { ContentHash = TapestryHash.OfContent(index == count - 1 ? "tail" : large) },
                Embedding = lastRanksFirst && index != count - 1 ? [0.5f, 0.8660254f, 0f, 0f, 0f, 0f, 0f, 0f] : DagQuery,
            });
        }

        await _store.AppendGraphCheckpointAsync(new(id, nodes, []), CancellationToken.None);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        return id;
    }

    [Theory]
    [InlineData(TapestryScopeKind.Session, false)]
    [InlineData(TapestryScopeKind.SessionAttachment, false)]
    [InlineData(TapestryScopeKind.SessionAttachment, true)]
    public async Task SourceEventDate_UsesEntryTime_AndNeverSnapshotUploadTime(TapestryScopeKind kind, bool referenced)
    {
        TapestryScope scope = await SeedSnapshotScopeAsync(kind);

        await AddSnapshotSourcesAsync(scope, 1, 1);

        await ExecuteAsync("UPDATE SessionAttachments SET CreatedAt='2099-01-01T00:00:00.0000000Z',SourceLastWriteAt='2098-01-01T00:00:00.0000000Z'");

        if (referenced)
        {
            await ExecuteAsync("UPDATE SessionAttachments SET SourceKind='WorkspaceFile',SourceStatus='Refreshable',SourceWorkspaceIdentity='workspace',SourceRelativePath='source.txt',SourceContentSha256='hash'");
        }

        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        TapestryNodeWrite leaf = Leaf(id, SnapshotSourceId(1));

        leaf = leaf with { Node = leaf.Node with { ScopeKind = kind, ScopeId = scope.Id, SourceKind = kind == TapestryScopeKind.Session ? TapestryLeafSourceKind.Entry : TapestryLeafSourceKind.SessionAttachmentChunk, ContentHash = TapestryHash.OfContent("body") } };

        await _store.AppendGraphCheckpointAsync(new(id, [leaf], []), CancellationToken.None);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        var access = await _store.GetRetrievableGenerationAsync(scope, QueryIdentity, CancellationToken.None);

        var graph = await _store.ReadRetrievalGraphAsync(access.Value, CancellationToken.None);

        Assert.True(graph.IsSuccess);

        DateTimeOffset? expected = kind == TapestryScopeKind.Session
            ? new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            : referenced ? new DateTimeOffset(2098, 1, 1, 0, 0, 0, TimeSpan.Zero) : null;

        Assert.Equal(expected, Assert.Single(graph.Value.Vertices).Value.SourceEventAt);
    }
}
