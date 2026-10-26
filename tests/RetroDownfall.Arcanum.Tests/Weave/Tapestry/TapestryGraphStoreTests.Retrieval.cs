using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryGraphStoreTests
{
    private static readonly TapestryRetrievalIdentity QueryIdentity = new(TapestryHash.OfEmbeddingModel("provider", "model"), 8);

    [Fact]
    public async Task RetrievalIdentity_RefusesWrongModelAndActualWidth()
    {
        (string id, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        Assert.True((await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity, CancellationToken.None)).IsSuccess);

        Assert.True((await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity with { EmbeddingIdentity = "other-model" }, CancellationToken.None)).IsFailure);

        Assert.True((await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity with { EmbeddingDimension = 9 }, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Hydration_RevalidatesCapturedAccessAndRefusesStaleSummariesOnEitherBranch()
    {
        (string id, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        Result<TapestryGenerationAccess> lookup = await _store.GetRetrievableGenerationAsync(Scope, QueryIdentity, CancellationToken.None);

        Assert.True(lookup.IsSuccess);

        IReadOnlyList<TapestryRetrievedNode> before = await _store.HydrateRetrievedNodesAsync(lookup.Value, [(id + "two", 0.9f)], TapestryRetrievalMode.CollapsedTree, CancellationToken.None);

        Assert.Equal([id + "a", id + "b", id + "root"], Assert.Single(before).AncestorNodeIds);

        await ExecuteAsync("UPDATE workspace_file_chunks SET Content='edited' WHERE ChunkId='two'");

        Assert.Empty(await _store.HydrateRetrievedNodesAsync(lookup.Value, [(id + "a", 0.9f), (id + "b", 0.8f), (id + "root", 0.7f)], TapestryRetrievalMode.CollapsedTree, CancellationToken.None));
    }

    [Fact]
    public async Task Hydration_RefusesForgedIdentityEvenWithCapturedGeneration()
    {
        (string id, TapestryBuildSnapshot snapshot) = await DiamondAsync();

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, Recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        TapestryGeneration generation = (await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        Assert.Empty(await _store.HydrateRetrievedNodesAsync(new TapestryGenerationAccess(generation, QueryIdentity with { EmbeddingIdentity = "other-model" }), [(id + "root", 1f)], TapestryRetrievalMode.CollapsedTree, CancellationToken.None));
    }
}
