using System.Collections.Concurrent;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

/// <summary>Runs the real sweep and weaver; only persistence and outbound providers are substituted.</summary>
internal sealed class TapestryAdmissionHarness : IAsyncDisposable
{
    internal static readonly TapestryScope First = new(TapestryScopeKind.Workspace, "/first");

    internal static readonly TapestryScope Second = new(TapestryScopeKind.Workspace, "/second");

    internal ConcurrentQueue<string> Events { get; } = new();

    internal Func<string, CancellationToken, Task> OnStep { get; set; } = static (_, _) => Task.CompletedTask;

    internal GrimoireConnectionAdmissionGate Inner { get; } = new(TimeProvider.System);

    internal RecordingGrimoireWorkAdmissionGate Gate { get; }

    internal Store Persistence { get; }

    internal TapestryWeavingService Service { get; }

    internal ObservingScopeFactory Scopes { get; }

    internal ArcanumSettings Configuration { get; } = new();

    private readonly ServiceProvider _provider;

    internal TapestryAdmissionHarness(TimeProvider? clock = null)
    {
        Gate = new RecordingGrimoireWorkAdmissionGate(Inner)
        {
            BeforeEffectGroupDisposalAsync = () => new ValueTask(StepAsync("group-dispose", CancellationToken.None)),
            BeforeWorkLeaseDisposalAsync = () => new ValueTask(StepAsync("lease-dispose", CancellationToken.None)),
        };

        Persistence = new Store(this);

        ServiceCollection services = new();

        services.AddSingleton(clock ?? TimeProvider.System);

        services.AddSingleton<ITapestryStore>(Persistence);

        services.AddSingleton(new TapestryWeaver(
            Persistence,
            new Embeddings(this),
            new Summarizer(this),
            TimeProvider.System,
            NullLogger<TapestryWeaver>.Instance));

        _provider = services.BuildServiceProvider();

        Scopes = new ObservingScopeFactory(this, _provider.GetRequiredService<IServiceScopeFactory>());

        Service = ActivatorUtilities.CreateInstance<TapestryWeavingService>(
            _provider,
            Scopes,
            new TestOptionsMonitor<ArcanumSettings>(Configuration),
            Gate,
            NullLogger<TapestryWeavingService>.Instance);
    }

    internal static EmbeddingSettings Settings() => new()
    {
        Enabled = true,
        TapestryEnabled = true,
        Dimensions = 64,
        Tapestry = new TapestryEmbeddingSettings(),
    };

    internal async Task<IReadOnlyList<TapestryWeaveOutcome>> SweepAsync(CancellationToken token = default) =>
        (await Service.RunSweepAsync(Settings(), token)).Outcomes;

    internal async Task StepAsync(string step, CancellationToken token)
    {
        Events.Enqueue(step);

        await OnStep(step, token);
    }

    public async ValueTask DisposeAsync()
    {
        Service.Dispose();

        await _provider.DisposeAsync();
    }

    internal sealed class Checkpoint
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task PauseAsync(CancellationToken token = default)
        {
            Reached.TrySetResult();

            await Release.Task.WaitAsync(token);
        }

        internal Task WaitAsync() => Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal sealed class ObservingScopeFactory(TapestryAdmissionHarness harness, IServiceScopeFactory inner) : IServiceScopeFactory
    {
        internal int Created { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;

            harness.Events.Enqueue("scope-create");

            return new Scope(harness, inner.CreateScope());
        }

        private sealed class Scope(TapestryAdmissionHarness harness, IServiceScope inner) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => inner.ServiceProvider;

            public void Dispose() => throw new InvalidOperationException("The sweep must dispose its scope asynchronously.");

            public async ValueTask DisposeAsync()
            {
                await harness.StepAsync("scope-dispose", CancellationToken.None);

                await ((IAsyncDisposable)inner).DisposeAsync();
            }
        }
    }

    internal sealed class Store(TapestryAdmissionHarness harness) : ITapestryStore
    {
        internal IReadOnlyList<TapestryScope> Scopes { get; set; } = [First];

        internal Dictionary<string, TapestryGeneration> Generations { get; } = [];

        internal int Begins { get; private set; }

        internal int Cleanups { get; private set; }

        internal string Corpus { get; set; } = "initial";

        public async Task<IReadOnlyList<TapestryScope>> DiscoverScopesAsync(bool includeWorkspace, bool includeSessionAttachments, bool includeSessions, CancellationToken cancellationToken)
        {
            await harness.StepAsync("discover", cancellationToken);

            return Scopes;
        }

        public async Task<IReadOnlyList<TapestryLeafSource>> EnumerateLeafSourcesAsync(TapestryScope scope, int expectedDimensions, bool includeEmbeddings, CancellationToken cancellationToken)
        {
            await harness.StepAsync("leaves:" + scope.Id, cancellationToken);

            return [new("a", "a.cs", Corpus + " alpha", "hash-a-" + Corpus, null), new("b", "b.cs", Corpus + " beta", "hash-b-" + Corpus, null)];
        }

        public Task<TapestryGeneration?> GetCurrentGenerationAsync(TapestryScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(Generations.Values.SingleOrDefault(generation => generation.ScopeId == scope.Id && generation.Status == TapestryGenerationStatus.Complete));

        public async Task<string> BeginGenerationAsync(TapestryScope scope, string algorithmVersion, string settingsFingerprint, string? summaryModel, string summaryRecipeVersion, int embeddingDimension, string corpusFingerprint, DateTimeOffset startedAt, CancellationToken cancellationToken)
        {
            Begins++;

            await harness.StepAsync("begin:" + scope.Id, cancellationToken);

            string id = Guid.NewGuid().ToString("N");

            Generations.Add(id, new(id, scope.Kind, scope.Id, TapestryGenerationStatus.Building, algorithmVersion, settingsFingerprint, summaryModel, summaryRecipeVersion, embeddingDimension, corpusFingerprint, 0, 0, 0, null, startedAt, null));

            return id;
        }

        public Task AppendNodesAsync(IReadOnlyList<TapestryNodeWrite> nodes, CancellationToken cancellationToken) =>
            harness.StepAsync(nodes[0].Node.NodeKind == TapestryNodeKind.Leaf ? "append-leaves" : "append-summary", cancellationToken);

        public Task SetParentAsync(string generationId, string parentNodeId, IReadOnlyList<string> childNodeIds, CancellationToken cancellationToken) =>
            harness.StepAsync("parents", cancellationToken);

        public async Task PublishGenerationAsync(string generationId, int layerCount, int nodeCount, int rootNodeCount, TapestryTerminalReason terminalReason, DateTimeOffset completedAt, CancellationToken cancellationToken)
        {
            await harness.StepAsync("publish", cancellationToken);

            TapestryGeneration built = Generations[generationId];

            foreach (TapestryGeneration prior in Generations.Values.Where(generation => generation.ScopeId == built.ScopeId && generation.Status == TapestryGenerationStatus.Complete).ToArray())
            {
                Generations[prior.GenerationId] = prior with { Status = TapestryGenerationStatus.Superseded };
            }

            Generations[generationId] = built with { Status = TapestryGenerationStatus.Complete, LayerCount = layerCount, NodeCount = nodeCount, RootNodeCount = rootNodeCount, TerminalReason = terminalReason, CompletedAt = completedAt };

            await harness.StepAsync("published", cancellationToken);
        }

        public async Task AbandonGenerationAsync(string generationId, CancellationToken cancellationToken)
        {
            await harness.StepAsync("abandon", cancellationToken);

            if (Generations[generationId].Status == TapestryGenerationStatus.Building)
            {
                Generations.Remove(generationId);
            }
        }

        public async Task<int> ReconcileGenerationsAsync(CancellationToken cancellationToken)
        {
            Cleanups++;

            await harness.StepAsync("cleanup", cancellationToken);

            string[] removed = Generations.Values.Where(generation => generation.Status != TapestryGenerationStatus.Complete).Select(generation => generation.GenerationId).ToArray();

            foreach (string id in removed)
            {
                Generations.Remove(id);
            }

            return removed.Length;
        }

        public async Task<int> PruneRemovedScopesAsync(bool includeWorkspace, bool includeSessionAttachments, bool includeSessions, CancellationToken cancellationToken)
        {
            await harness.StepAsync("prune", cancellationToken);

            return 0;
        }

        public Task<TapestrySummaryReuseCandidate?> TryGetReusableSummaryAsync(TapestryScope scope, string childMembershipHash, CancellationToken cancellationToken) => Task.FromResult<TapestrySummaryReuseCandidate?>(null);

        public Task<IReadOnlyList<TapestryNode>> GetLayerNodesAsync(string generationId, int layer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, float[]>> GetNodeEmbeddingsAsync(IReadOnlyList<string> nodeIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TapestryRetrievedNode>> HydrateRetrievedNodesAsync(TapestryGeneration generation, IReadOnlyList<(string NodeId, float Similarity)> hits, TapestryRetrievalMode mode, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> GetTerminalLayerAsync(string generationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TapestryScopeStatus>> GetScopeStatusesAsync(Guid? sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountPublishedNodesAsync(Guid? sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static Embedding<float> Vector()
    {
        float[] vector = new float[64];

        vector[0] = 1;

        return new Embedding<float>(vector);
    }

    private sealed class Embeddings(TapestryAdmissionHarness harness) : IWeaveService
    {
        public bool IsAvailable => true;

        public async Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            await harness.StepAsync("embed-summary", cancellationToken);

            return Vector();
        }

        public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            await harness.StepAsync("embed-leaves", cancellationToken);

            return texts.Select(static _ => Vector()).ToArray();
        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Summarizer(TapestryAdmissionHarness harness) : ITapestrySummarizer
    {
        public string? ResolveSummaryModel() => "summary-model";

        public bool FitsOneRequest(TapestrySummaryRequest request) => true;

        public async Task<Result<string>> SummarizeAsync(TapestrySummaryRequest request, CancellationToken cancellationToken)
        {
            await harness.StepAsync("summarize", cancellationToken);

            return "the cluster summary";
        }
    }
}
