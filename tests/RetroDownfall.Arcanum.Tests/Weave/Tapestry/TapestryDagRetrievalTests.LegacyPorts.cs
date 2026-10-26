using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryGraphStoreTests
{
    // These assertions used to call Wizard's approximate single-parent selector. They now exercise
    // actual admitted storage/retrieval and the shared rendered-token budget.
    [Fact]
    public async Task FormerWizardSelection_NodeBoundUsesActualRetriever()
    {
        string id = await PublishDagAsync();

        Assert.Equal([id + "one", id + "three"], await RetrieveIdsAsync(DagRequest(maxNodes: 2)));
    }

    [Fact]
    public async Task FormerWizardSelection_ByteAndTokenBoundsUseActualRenderer()
    {
        await ExecuteAsync("UPDATE workspace_file_chunks SET Content='" + new string('x', 4000) + "' WHERE ChunkId='one'");

        var snapshot = await _store.ReadBuildSnapshotAsync(Scope, default);

        string id = await _store.BeginGraphGenerationAsync(snapshot, Recipe, DateTimeOffset.UtcNow, default);

        var large = Leaf(id, "one");

        large = large with { Node = large.Node with { ContentHash = TapestryHash.OfContent(new string('x', 4000)) } };

        await _store.AppendGraphCheckpointAsync(new(id, [large, Leaf(id, "two"), Leaf(id, "three")], []), default);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), default)).IsSuccess);

        Assert.Equal([id + "three", id + "two"], await RetrieveIdsAsync(DagRequest(maxBytes: 1500)));

        var access = await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity, default);

        var small = await _store.HydrateRetrievedNodesAsync(access.Value, [(id + "three", 1f), (id + "two", 1f)], TapestryRetrievalMode.CollapsedTree, default);

        int tokens = DagEstimator().EstimateText(DagProvider, "gpt-4o", SystemPromptBuilder.RenderTapestryContext(small)).TokenCount;

        Assert.Equal([id + "three", id + "two"], await RetrieveIdsAsync(DagRequest(maxTokens: tokens)));
    }

    [Fact]
    public async Task FormerWizardSelection_EmptyPublishedInputSelectsNothing()
    {
        Assert.Empty(await RetrieveIdsAsync(DagRequest()));
    }
}
