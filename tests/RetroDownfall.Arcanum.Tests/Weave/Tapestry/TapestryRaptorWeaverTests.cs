using RetroDownfall.Arcanum.Core.Weave;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Api.Intelligence;
using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryWeaverTests
{
    [SkippableFact]
    public async Task Raptor_FeatureOff_MakesNoCallsOrGeneration()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedBulkChunksAsync(24);

        var settings = Settings();

        settings.TapestryEnabled = false;

        _ = await CreateWeaver().WeaveAsync(Scope, settings, CancellationToken.None);

        Assert.Equal(0, _summarizer!.CallCount);

        Assert.Empty(_weave!.ProducedVectors);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM tapestry_generations"));
    }

    [SkippableFact]
    public async Task Raptor_SmallLayerStopsWithoutPaidRestatementOrForcedRoot()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.Woven, outcome.Status);

        Assert.Equal(0, outcome.SummaryCallsMade);

        Assert.Equal(10, outcome.RootNodeCount);

        Assert.Equal(10, outcome.NodeCount);

        TapestryGeneration current = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        Assert.Equal("raptor-umap-gmm-v1", current.AlgorithmVersion);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM tapestry_edges"));
    }

    [SkippableFact]
    public async Task Raptor_SoftChild_WritesTwoEdgesWithoutOverwrite()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedBulkChunksAsync(24);

        _summarizer!.AlwaysFits = true;

        var clusterer = new OverlappingClusterer();

        var weaver = new TapestryWeaver(_store!, _weave!, _summarizer, _backoff, TimeProvider.System, _logger, clusterer);

        TapestryWeaveOutcome outcome = await weaver.WeaveAsync(Scope, Settings(maxChildren: 24), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.Woven, outcome.Status);

        Assert.Equal(2, outcome.SummaryCallsMade);

        Assert.Equal(25, await CountAsync("SELECT COUNT(*) FROM tapestry_edges"));

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM (SELECT ChildNodeId FROM tapestry_edges GROUP BY ChildNodeId HAVING COUNT(*)=2)"));

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM tapestry_nodes WHERE ParentNodeId IS NOT NULL"));

        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM tapestry_nodes WHERE NodeKind='Summary' AND DescendantLeafCount IN (12,13)"));

        Assert.Equal(25, await CountAsync("SELECT COUNT(*) FROM tapestry_edges WHERE GlobalPosterior=0.8 AND LocalPosterior=0.7"));
    }

    [SkippableFact]
    public async Task Raptor_MissingEmbeddingModelProvenance_ReembedsExistingVector()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedChunksAsync(("existing", "a.cs", "existing body"));

        await using (var command = _db!.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "INSERT INTO workspace_file_embeddings(ChunkId,Embedding,Dim) VALUES('existing',@vector,64)";

            AddParameter(command, "@vector", EmbeddingBlobCodec.Encode(UnitVector()));

            await command.ExecuteNonQueryAsync();
        }

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.Woven, outcome.Status);

        Assert.Equal(1, outcome.LeavesEmbedded);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("other-model")]
    public async Task Raptor_FreshEmbeddingIdentityMustMatchActualLease(string? model)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        _weave!.Provenance = model is null ? null : new EmbeddingProvenance("local-embeddings", model);

        TapestryWeaveOutcome outcome = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.Failed, outcome.Status);

        Assert.Equal(0, _summarizer!.CallCount);

        Assert.Null(await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Raptor_WorkLimit_RefusesPublicationAndBacksOffWhilePriorGenerationSurvives()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        Assert.Equal(TapestryWeaveStatus.Woven, (await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None)).Status);

        TapestryGeneration prior = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        await SeedBulkChunksAsync(90);

        var clusterer = new ExplodingClusterer();

        var weaver = new TapestryWeaver(_store, _weave!, _summarizer!, _backoff, TimeProvider.System, _logger, clusterer);

        TapestryWeaveOutcome outcome = await weaver.WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.WorkLimit, outcome.Status);

        Assert.Equal(0, _summarizer!.CallCount);

        Assert.Equal(prior, await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None));

        Assert.Equal(0, await CountGenerationsWithStatusAsync("Building"));

        Assert.Equal(TapestryWeaveStatus.BackingOff, (await weaver.WeaveAsync(Scope, Settings(), CancellationToken.None)).Status);

        Assert.Equal(1, clusterer.Calls);
    }

    [SkippableFact]
    public async Task Raptor_SummaryEmbeddingIdentityChangeCannotPublishOrReplacePriorGeneration()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        Assert.Equal(TapestryWeaveStatus.Woven, (await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None)).Status);

        TapestryGeneration prior = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        await SeedBulkChunksAsync(14);

        _summarizer!.AlwaysFits = true;

        _summarizer.OnSummarize = () => _weave!.Provenance = new("local-embeddings", "replacement-model");

        var weaver = new TapestryWeaver(_store, _weave!, _summarizer, _backoff, TimeProvider.System, _logger, new OverlappingClusterer());

        TapestryWeaveOutcome outcome = await weaver.WeaveAsync(Scope, Settings(maxChildren: 24), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.Failed, outcome.Status);

        Assert.Equal(prior, await _store.GetCurrentGenerationAsync(Scope, CancellationToken.None));

        Assert.Equal(0, await CountGenerationsWithStatusAsync("Building"));
    }

    [SkippableFact]
    public async Task Raptor_ReuseRequiresOrderedPrompt_NotTheSameBagOfChildren()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedChunksAsync(Enumerable.Range(0, 24).Select(i => ($"c{i:D3}", $"f{i}.cs", $"ordered body {i}")).ToArray());

        _summarizer!.AlwaysFits = true;

        var weaver = new TapestryWeaver(_store!, _weave!, _summarizer, _backoff, TimeProvider.System, _logger, new FixedWidthClusterer(12));

        var first = await weaver.WeaveAsync(Scope, Settings(maxChildren: 24), CancellationToken.None);

        Assert.Equal(2, first.SummaryCallsMade);

        await SeedChunksAsync(Enumerable.Range(0, 24).Select(i => ($"c{i + 24:D3}", $"f{i + 24}.cs", $"ordered body {(i / 12 * 12) + 11 - i % 12}")).ToArray());

        var second = await weaver.WeaveAsync(Scope, Settings(maxChildren: 24), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.Woven, second.Status);

        Assert.Equal(2, second.SummaryCallsMade);

        Assert.Equal(2, second.SummariesReused);
    }

    [SkippableFact]
    public async Task Raptor_SummaryEmbeddings_AreNewFullWidthProviderEmbeddingsAtEveryRecursiveLayer()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedBulkChunksAsync(144);

        _summarizer!.AlwaysFits = true;

        var clusterer = new RecordingRecursiveClusterer(_weave!);

        var weaver = new TapestryWeaver(_store!, _weave!, _summarizer, _backoff, TimeProvider.System, _logger, clusterer);

        var outcome = await weaver.WeaveAsync(Scope, Settings(maxChildren: 24), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.Woven, outcome.Status);

        Assert.Equal(13, outcome.SummaryCallsMade);

        Assert.Equal([144, 12], clusterer.FrontierSizes);

        Assert.Equal(3, outcome.LayerCount);

        var generation = (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!;

        var summaries = (await _store.GetLayerNodesAsync(generation.GenerationId, 1, CancellationToken.None))
            .Concat(await _store.GetLayerNodesAsync(generation.GenerationId, 2, CancellationToken.None)).ToArray();

        var vectors = await _store.GetNodeEmbeddingsAsync(summaries.Select(node => node.NodeId).ToArray(), CancellationToken.None);

        Assert.Equal(13, summaries.Length);

        foreach (var node in summaries)
        {
            Assert.Equal(TapestryHash.OfContent(node.Content), node.ContentHash);

            Assert.Equal(_weave!.ProducedVectors[node.Content!], vectors[node.NodeId]);

            Assert.Equal(TestDimensions, vectors[node.NodeId].Length);
        }

        var root = Assert.Single(await _store.GetRootIdsAsync(generation.GenerationId, CancellationToken.None));

        var closure = await _store.GetDescendantLeafIdsAsync(generation.GenerationId, [root], CancellationToken.None);

        Assert.Equal(144, closure[root].Count);
    }

    [SkippableFact]
    public async Task Raptor_LaterLayerWorkLimit_PreservesPriorGenerationAndRecordsEarlierSummaryWork()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        var prior = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        await SeedBulkChunksAsync(14);

        _summarizer!.AlwaysFits = true;

        var clusterer = new LaterLayerWorkLimitClusterer();

        var weaver = new TapestryWeaver(_store!, _weave!, _summarizer, _backoff, TimeProvider.System, _logger, clusterer);

        var outcome = await weaver.WeaveAsync(Scope, Settings(), CancellationToken.None);

        Assert.Equal(TapestryWeaveStatus.WorkLimit, outcome.Status);

        Assert.Equal(12, _summarizer.CallCount);

        Assert.Equal(12, outcome.SummaryCallsMade);

        Assert.Equal(prior.GenerationId, (await _store!.GetCurrentGenerationAsync(Scope, CancellationToken.None))!.GenerationId);

        Assert.Equal(0, await CountGenerationsWithStatusAsync("Building"));

        Assert.Equal(TapestryWeaveStatus.BackingOff, (await weaver.WeaveAsync(Scope, Settings(), CancellationToken.None)).Status);

        Assert.Equal(12, _summarizer.CallCount);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Raptor_PublicationFailureOrCancellation_PreservesPriorAndDoesNotRepeatPaidPrefix(bool cancel)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        var prior = await CreateWeaver().WeaveAsync(Scope, Settings(), CancellationToken.None);

        await SeedBulkChunksAsync(14);

        _summarizer!.AlwaysFits = true;

        using var stopping = new CancellationTokenSource();

        var store = new BeginHookTapestryStore(_store!, () => Task.CompletedTask)
        {
            OnPublish = (_, _) =>
            {
                if (cancel) { stopping.Cancel(); stopping.Token.ThrowIfCancellationRequested(); }

                return Task.FromResult(Result.Failure(new Error(ErrorCodes.Hub.InvalidAdmission, "Publication refused.")));
            },
        };

        var weaver = new TapestryWeaver(store, _weave!, _summarizer, _backoff, TimeProvider.System, _logger, new FixedWidthClusterer(12));

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => weaver.WeaveAsync(Scope, Settings(maxChildren: 24), stopping.Token));
        else
            Assert.Equal(TapestryWeaveStatus.Failed, (await weaver.WeaveAsync(Scope, Settings(maxChildren: 24), default)).Status);

        Assert.Equal(2, _summarizer.CallCount);

        Assert.Null(TapestryBuildContext.Current);

        Assert.Equal(prior.GenerationId, (await _store!.GetCurrentGenerationAsync(Scope, default))!.GenerationId);

        Assert.Equal(0, await CountGenerationsWithStatusAsync("Building"));

        if (!cancel)
        {
            Assert.Equal(TapestryWeaveStatus.BackingOff, (await weaver.WeaveAsync(Scope, Settings(maxChildren: 24), default)).Status);

            Assert.Equal(2, _summarizer.CallCount);
        }
    }

    [SkippableFact]
    public async Task Raptor_EditAfterFinalProviderCheck_CannotPublishThroughTheDocumentedWindow()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedSummaryCorpusAsync();

        _summarizer!.AlwaysFits = true;

        var store = new BeginHookTapestryStore(_store!, () => Task.CompletedTask)
        {
            OnPublish = async (request, token) =>
            {
                await SeedChunksAsync(("c00", "file0.cs", "edited after the last provider returned"));

                return await _store!.PublishGenerationAsync(request, token);
            },
        };

        var weaver = new TapestryWeaver(store, _weave!, _summarizer, _backoff, TimeProvider.System, _logger, new FixedWidthClusterer(12));

        Assert.Equal(TapestryWeaveStatus.Failed, (await weaver.WeaveAsync(Scope, Settings(maxChildren: 24), default)).Status);

        Assert.Equal(2, _summarizer.CallCount);

        Assert.Null(await _store!.GetCurrentGenerationAsync(Scope, default));

        Assert.Equal(0, await CountGenerationsWithStatusAsync("Building"));
    }

    [SkippableFact]
    public async Task Raptor_CancellationAfterCommittedPublication_CannotAbandonTheNewCompleteGeneration()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        var prior = await CreateWeaver().WeaveAsync(Scope, Settings(), default);

        await SeedBulkChunksAsync(14);

        _summarizer!.AlwaysFits = true;

        using var stopping = new CancellationTokenSource();

        var store = new BeginHookTapestryStore(_store!, () => Task.CompletedTask)
        {
            OnPublish = async (request, token) =>
            {
                var result = await _store!.PublishGenerationAsync(request, token);

                Assert.True(result.IsSuccess);

                stopping.Cancel();

                return result;
            },
        };

        var weaver = new TapestryWeaver(store, _weave!, _summarizer, _backoff, TimeProvider.System, _logger, new FixedWidthClusterer(12));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => weaver.WeaveAsync(Scope, Settings(maxChildren: 24), stopping.Token));

        var current = (await _store!.GetCurrentGenerationAsync(Scope, default))!;

        Assert.NotEqual(prior.GenerationId, current.GenerationId);

        Assert.Equal(1, await CountGenerationsWithStatusAsync("Complete"));

        Assert.Equal(0, await CountGenerationsWithStatusAsync("Building"));

        Assert.Null(TapestryBuildContext.Current);

        Assert.Equal(TapestryWeaveStatus.UpToDate, (await weaver.WeaveAsync(Scope, Settings(maxChildren: 24), default)).Status);
    }

    private sealed class LaterLayerWorkLimitClusterer : ITapestryClusterer
    {
        public Result<RaptorClusterResult> Cluster(IReadOnlyList<RaptorPoint> points, TapestryClusterIdentity identity, CancellationToken token) =>
            identity.Layer == 1 ? new FixedWidthClusterer(2).Cluster(points, identity, token)
                : new ExplodingClusterer().Cluster(points, identity, token);
    }

    [SkippableFact]
    public async Task Raptor_LegacyKMeansKnobs_DoNotChangeAlgorithmOrRebillAnUnchangedGraph()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedSummaryCorpusAsync();

        var weaver = CreateWeaver();

        Assert.Equal(TapestryWeaveStatus.Woven, (await weaver.WeaveAsync(Scope, Settings(), default)).Status);

        int calls = _summarizer!.CallCount;

        var obsoleteKnobs = Settings(target: 8);

        obsoleteKnobs.Tapestry.MaxClustersPerLayer = 1;

        Assert.Equal(TapestryWeaveStatus.UpToDate, (await weaver.WeaveAsync(Scope, obsoleteKnobs, default)).Status);

        Assert.Equal(calls, _summarizer.CallCount);
    }

    private sealed class RecordingRecursiveClusterer(FakeWeaveService weave) : ITapestryClusterer
    {
        public List<int> FrontierSizes { get; } = [];

        public Result<RaptorClusterResult> Cluster(IReadOnlyList<RaptorPoint> points, TapestryClusterIdentity identity, CancellationToken token)
        {
            FrontierSizes.Add(points.Count);

            Assert.All(points, point =>
            {
                Assert.Equal(TestDimensions, point.Vector.Length);

                Assert.Contains(weave.ProducedVectors.Values, vector => vector.SequenceEqual(point.Vector.ToArray()));
            });

            return new FixedWidthClusterer(12).Cluster(points, identity, token);
        }
    }

    private sealed class FixedWidthClusterer(int width) : ITapestryClusterer
    {
        public Result<RaptorClusterResult> Cluster(IReadOnlyList<RaptorPoint> points, TapestryClusterIdentity identity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            return new RaptorClusterResult(Enumerable.Range(0, (points.Count + width - 1) / width).Select(group =>
                new RaptorCluster($"group-{group}", Enumerable.Range(group * width, Math.Min(width, points.Count - group * width))
                    .Select(i => new RaptorMembership(i, group, 0, 0.9, 0.8, RaptorRepairReason.None)).ToImmutableArray())).ToImmutableArray(), []);
        }
    }

    private sealed class ExplodingClusterer : ITapestryClusterer
    {
        public int Calls { get; private set; }

        public Result<RaptorClusterResult> Cluster(IReadOnlyList<RaptorPoint> points, TapestryClusterIdentity identity, CancellationToken token)
        {
            Calls++;

            return new RaptorClusterResult(Enumerable.Range(0, 80).Select(skip => new RaptorCluster($"c{skip}",
                Enumerable.Range(0, points.Count).Where(i => i != skip).Select(i => new RaptorMembership(i, skip, 0, 0.8, 0.7, RaptorRepairReason.None)).ToImmutableArray())).ToImmutableArray(), []);
        }
    }

    private sealed class OverlappingClusterer : ITapestryClusterer
    {
        public Result<RaptorClusterResult> Cluster(IReadOnlyList<RaptorPoint> points, TapestryClusterIdentity identity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            Assert.Equal(24, points.Count);

            return new RaptorClusterResult(
                [new("left", Enumerable.Range(0, 12).Select(i => new RaptorMembership(i, 0, 0, 0.8, 0.7, RaptorRepairReason.None)).ToImmutableArray()),
                 new("right", Enumerable.Range(11, 13).Select(i => new RaptorMembership(i, 1, 0, 0.8, 0.7, RaptorRepairReason.None)).ToImmutableArray())], []);
        }
    }
}
