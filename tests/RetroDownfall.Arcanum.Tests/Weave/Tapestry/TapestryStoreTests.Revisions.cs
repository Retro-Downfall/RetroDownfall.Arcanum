using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryStoreTests
{
    [Fact]
    public async Task AttachmentScopeReplacement_InvalidatesSummaryDespiteUnchangedStoredTextAndHash()
    {
        string sessionId = Guid.NewGuid().ToString("D");

        TapestryScope scope = new(TapestryScopeKind.SessionAttachment, sessionId);

        await SeedAttachmentChunkAsync(sessionId, "attachment-old", "private old body");

        TapestryBuildSnapshot snapshot = await _store!.ReadBuildSnapshotAsync(scope, CancellationToken.None);

        TapestryGenerationRecipe recipe = new(RaptorAlgorithmManifest.Version, SettingsFingerprint, "fast",
            TapestryHash.SummaryRecipeVersion, TestDimensions, QueryIdentity.EmbeddingIdentity);

        string id = await _store.BeginGraphGenerationAsync(snapshot, recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        TapestryNodeWrite summary = Summary(id, "summary", "derived private old body", "membership", 1);

        summary = summary with { Node = summary.Node with { ScopeKind = scope.Kind, ScopeId = scope.Id } };

        await _store.AppendGraphCheckpointAsync(new(id,
            [LeafIn(scope, id, "leaf", "attachment-old", "private old body"), summary],
            [new("summary", "leaf", 1, 1, "Posterior")]), CancellationToken.None);

        Assert.True((await _store.PublishGenerationAsync(new(id, snapshot, recipe, TapestryTerminalReason.SingleRoot, DateTimeOffset.UtcNow), CancellationToken.None)).IsSuccess);

        var lookup = await _store.GetRetrievableGenerationAsync(scope, QueryIdentity, CancellationToken.None);

        Assert.True(lookup.IsSuccess);

        Assert.Single(await _store.HydrateRetrievedNodesAsync(lookup.Value, [("summary", 1f)], TapestryRetrievalMode.CollapsedTree, CancellationToken.None));

        await RetireAttachmentChunkAsync("attachment-old");

        Assert.Empty(await _store.HydrateRetrievedNodesAsync(lookup.Value, [("summary", 1f)], TapestryRetrievalMode.CollapsedTree, CancellationToken.None));

        Assert.Null(await _store.TryGetReusableSummaryAsync(scope, "membership", CancellationToken.None));

        Assert.True((await _store.GetRetrievableGenerationAsync(scope, QueryIdentity, CancellationToken.None)).IsFailure);
    }
}
