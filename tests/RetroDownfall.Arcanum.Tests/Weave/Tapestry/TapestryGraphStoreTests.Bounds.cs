using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryGraphStoreTests
{
    [Fact]
    public async Task ReverseLookupAndRootAntiJoinUseTheGenerationAndChildIndexes()
    {
        (string id, _) = await DiamondAsync();

        string reverse = await ExplainAsync("SELECT ParentNodeId FROM tapestry_edges WHERE GenerationId=@id AND ChildNodeId=@node", id);

        Assert.Contains("idx_tapestry_edges_child", reverse, StringComparison.Ordinal);

        string roots = await ExplainAsync("SELECT n.NodeId FROM tapestry_nodes n WHERE n.GenerationId=@id AND NOT EXISTS (SELECT 1 FROM tapestry_edges e WHERE e.GenerationId=n.GenerationId AND e.ChildNodeId=n.NodeId)", id);

        Assert.Contains("idx_tapestry_edges_child", roots, StringComparison.Ordinal);

        Assert.Contains("ux_tapestry_nodes_generation_node", roots, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedDiamondsAndMoreThanOneParameterPageHaveBoundedDistinctClosure()
    {
        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        List<TapestryNodeWrite> nodes = [Leaf(id, "one"), Leaf(id, "two"), Leaf(id, "three")];

        List<TapestryGraphEdge> edges = [];

        string[] prior = ["one", "two", "three"];

        for (int layer = 1; layer <= 24; layer++)
        {
            string[] next = [$"a{layer:D2}", $"b{layer:D2}"];

            foreach (string parent in next)
            {
                nodes.Add(Summary(id, parent, layer));

                edges.AddRange(prior.Select(child => Edge(id, parent, child)));
            }

            prior = next;
        }

        await _store.AppendGraphCheckpointAsync(new(id, nodes, edges), CancellationToken.None);

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        long instructions = 0;

        raw.sqlite3_progress_handler(connection.Handle, 1000, (object _) => { instructions += 1000; return instructions > 200_000 ? 1 : 0; }, null);

        try
        {
            IReadOnlyDictionary<string, IReadOnlyList<string>> ancestors = await _store.GetAncestorIdsAsync(id,
                [id + "two", .. Enumerable.Range(0, 600).Select(index => "absent-" + index)], CancellationToken.None);

            Assert.Equal(601, ancestors.Count);

            Assert.Equal(48, ancestors[id + "two"].Count);

            IReadOnlyDictionary<string, IReadOnlyList<string>> leaves = await _store.GetDescendantLeafIdsAsync(id,
                [id + "a24", id + "b24"], CancellationToken.None);

            Assert.Equal(["one", "three", "two"], leaves[id + "a24"]);

            Assert.Equal(leaves[id + "a24"], leaves[id + "b24"]);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }

        Assert.InRange(instructions, 1, 200_000);
    }

    private async Task<string> ExplainAsync(string sql, string id)
    {
        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        _ = command.Parameters.Add(new SqliteParameter("@id", id));

        _ = command.Parameters.Add(new SqliteParameter("@node", id + "two"));

        await using DbDataReader reader = await command.ExecuteReaderAsync();

        List<string> details = [];

        while (await reader.ReadAsync())
        {
            details.Add(reader.GetString(3));
        }

        return string.Join("\n", details);
    }

    [Fact]
    public async Task InsertOrReplace_InvalidatesEvenWhenSourceBytesAndIdsAreIdentical()
    {
        (string id, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        await ExecuteAsync("PRAGMA recursive_triggers=OFF;" + Chunk("one").Replace("INSERT INTO", "INSERT OR REPLACE INTO", StringComparison.Ordinal));

        Assert.Equal(snapshot.Corpus.Fingerprint, (await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None)).Corpus.Fingerprint);

        Assert.True((await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity, CancellationToken.None)).IsFailure);

        Assert.Null(await _store.TryGetReusableSummaryAsync(Scope, "a", CancellationToken.None));
    }

    [Fact]
    public async Task SourceMove_InvalidatesOldAndNewScope()
    {
        (string oldId, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(oldId, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        TapestryScope other = new(TapestryScopeKind.Workspace, "/other");

        await ExecuteAsync(Chunk("other").Replace("'/graph'", "'/other'", StringComparison.Ordinal));

        TapestryBuildSnapshot otherSnapshot = await _store.ReadBuildSnapshotAsync(other, CancellationToken.None);

        string id = await _store.BeginGraphGenerationAsync(otherSnapshot, Recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        TapestryNodeWrite leaf = Leaf(id, "other");

        await _store.AppendGraphCheckpointAsync(new(id, [leaf with { Node = leaf.Node with { ScopeId = other.Id } }], []), CancellationToken.None);

        Assert.True((await _store.PublishGenerationAsync(new(id, otherSnapshot, Recipe, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        await ExecuteAsync("UPDATE workspace_file_chunks SET WorkspacePath='/other' WHERE ChunkId='one'");

        Assert.True((await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity, CancellationToken.None)).IsFailure);

        Assert.True((await _store.GetRetrievableGenerationAsync(other, QueryIdentity, CancellationToken.None)).IsFailure);
    }
}
