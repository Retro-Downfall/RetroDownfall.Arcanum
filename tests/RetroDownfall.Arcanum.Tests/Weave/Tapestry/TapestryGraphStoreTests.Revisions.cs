using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryGraphStoreTests
{
    [Fact]
    public async Task LegacyPublication_RefusesSourceRevisionChangeWithoutRetiringPriorGeneration()
    {
        (string current, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(current, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        string staged = await _store.BeginGenerationAsync(Scope, SphericalKMeans.AlgorithmVersion, "legacy-settings", "summary-model", TapestryHash.SummaryRecipeVersion, 8, snapshot.Corpus.Fingerprint, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendNodesAsync([Leaf(staged, "one"), Leaf(staged, "two"), Leaf(staged, "three")], CancellationToken.None);

        await ExecuteAsync(Chunk("four"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.PublishGenerationAsync(staged, 1, 3, 3, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(current, (await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None))?.GenerationId);
    }

    [Fact]
    public async Task LegacyCheckpoint_AfterUpgradeUsesCurrentRevisionAndKeepsLegacyTraversal()
    {
        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGenerationAsync(Scope, SphericalKMeans.AlgorithmVersion, "legacy-settings", "summary-model", TapestryHash.SummaryRecipeVersion, 8, snapshot.Corpus.Fingerprint, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendNodesAsync([Leaf(id, "one"), Leaf(id, "two"), Leaf(id, "three")], CancellationToken.None);

        await _store.AppendGraphCheckpointAsync(new(id, [Summary(id, "a", 1)], [Edge(id, "a", "one"), Edge(id, "a", "two"), Edge(id, "a", "three")], true), CancellationToken.None);

        await _store.PublishGenerationAsync(id, 2, 4, 1, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(3L, await ScalarAsync($"SELECT COUNT(*) FROM tapestry_nodes WHERE ParentNodeId='{id}a' AND ParentScopeKey='{id}#{id}a'"));

        Assert.Equal(3L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_edges"));

        Assert.NotNull(await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None));
    }

    [Fact]
    public async Task Begin_refuses_a_forged_partial_snapshot_even_at_the_current_revision()
    {
        TapestryBuildSnapshot actual = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        TapestryBuildSnapshot partial = actual with { Corpus = new(1, TapestryHash.OfCorpus([("one", TapestryHash.OfContent("one"))]), false) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.BeginGraphGenerationAsync(partial, Recipe, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM tapestry_generations"));
    }

    [Fact]
    public async Task PostUpgradeLegacyLookup_PreservesValidationAcrossPureAppend()
    {
        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGenerationAsync(Scope, SphericalKMeans.AlgorithmVersion, "legacy-settings", "summary-model", TapestryHash.SummaryRecipeVersion, 8, snapshot.Corpus.Fingerprint, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendNodesAsync([Leaf(id, "one"), Leaf(id, "two"), Leaf(id, "three")], CancellationToken.None);

        await _store.PublishGenerationAsync(id, 1, 3, 3, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(id, (await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None))?.GenerationId);

        Assert.Equal(0L, await ScalarAsync($"SELECT ValidatedInvalidationRevision FROM tapestry_generations WHERE GenerationId='{id}'"));

        // Rehashing after append would reject the old fingerprint; validated safe summaries stay usable.
        await ExecuteAsync(Chunk("four"));

        Assert.Equal(id, (await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None))?.GenerationId);
    }

    [Fact]
    public async Task PostUpgradeLegacyValidation_CannotResurrectAnEditRevertHashMatch()
    {
        TapestryBuildSnapshot snapshot = await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        string id = await _store.BeginGenerationAsync(Scope, SphericalKMeans.AlgorithmVersion, "legacy-settings", "summary-model", TapestryHash.SummaryRecipeVersion, 8, snapshot.Corpus.Fingerprint, DateTimeOffset.UtcNow, CancellationToken.None);

        await _store.AppendNodesAsync([Leaf(id, "one"), Leaf(id, "two"), Leaf(id, "three")], CancellationToken.None);

        await _store.PublishGenerationAsync(id, 1, 3, 3, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow, CancellationToken.None);

        await ExecuteAsync("UPDATE workspace_file_chunks SET Content='changed' WHERE ChunkId='one'; UPDATE workspace_file_chunks SET Content='one' WHERE ChunkId='one';");

        Assert.Equal(snapshot.Corpus.Fingerprint, (await _store.ReadBuildSnapshotAsync(Scope, CancellationToken.None)).Corpus.Fingerprint);

        Assert.Null(await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None));
    }
}
