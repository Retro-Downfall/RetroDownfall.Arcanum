using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>RAG Phase 5 — <see cref="SemanticSpellRouter"/> pure/hybrid/disabled modes and graceful degradation.</summary>
public sealed class SemanticSpellRouterTests
{

    private static readonly SpellMetadata AlphaSpell = new("Alpha", "alpha description", "/a/SPELL.md");

    private static readonly SpellMetadata BetaSpell = new("Beta", "beta description", "/b/SPELL.md");

    private static SemanticSpellRouter CreateRouter(FakeWeaveService weave, ArcanumSettings settings) =>
        new(
            new SpellWeaveCache(weave, new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<SpellWeaveCache>.Instance),
            weave,
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            NullLogger<SemanticSpellRouter>.Instance);

    private static ArcanumSettings BaseSettings(bool enabled) => new()
    {
        Features = new FeatureSettings
        {
            Embeddings = enabled,
            SemanticSpellRouting = enabled,
        },
    };

    [Fact]
    public async Task ResolveAsync_Disabled_ReturnsFullGrimoire_WithoutTouchingWeave()
    {
        FakeWeaveService weave = new();

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings
            {
                Embeddings = false,
                SemanticSpellRouting = false,
            },
        };

        SemanticSpellRouter router = CreateRouter(weave, settings);

        SpellRoutingDecision decision = await router.ResolveAsync([AlphaSpell, BetaSpell], "prompt", CancellationToken.None);

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, decision.Mode);

        Assert.Null(decision.ResolvedSpell);

        Assert.Null(decision.Candidates);

        Assert.Equal(0, weave.EmbedCallCount);

        Assert.Equal(0, weave.EmbedBatchCallCount);
    }

    [Fact]
    public async Task ResolveAsync_EmptySpellList_ReturnsFullGrimoire()
    {
        FakeWeaveService weave = new();

        SemanticSpellRouter router = CreateRouter(weave, BaseSettings(enabled: true));

        SpellRoutingDecision decision = await router.ResolveAsync([], "prompt", CancellationToken.None);

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, decision.Mode);
    }

    [Fact]
    public async Task ResolveAsync_PureMode_PicksHighestSimilaritySpellAboveThreshold()
    {
        FakeWeaveService weave = new() { QueryVector = [1f, 0f] };

        weave.BatchVectorsByText["alpha description"] = [1f, 0f];

        weave.BatchVectorsByText["beta description"] = [0f, 1f];

        SemanticSpellRouter router = CreateRouter(weave, BaseSettings(enabled: true));

        SpellRoutingDecision decision = await router.ResolveAsync([AlphaSpell, BetaSpell], "prompt", CancellationToken.None);

        Assert.Equal(SpellRoutingDecisionMode.DirectResonance, decision.Mode);

        Assert.NotNull(decision.ResolvedSpell);

        Assert.Equal("Alpha", decision.ResolvedSpell!.Name);

        Assert.Null(decision.Candidates);
    }

    [Fact]

    public async Task ResolveAsync_ReportsEveryAuxiliaryEmbeddingInvocation()

    {

        FakeWeaveService weave = new() { QueryVector = [1f, 0f] };

        weave.BatchVectorsByText["alpha description"] = [1f, 0f];

        List<(string Purpose, int InputCount)> observed = [];

        SemanticSpellRouter router = CreateRouter(weave, BaseSettings(enabled: true));

        SpellRoutingDecision decision = await router.ResolveAsync(

            [AlphaSpell],

            "prompt",

            CancellationToken.None,

            (purpose, inputCount) => observed.Add((purpose, inputCount)));

        Assert.Equal(SpellRoutingDecisionMode.DirectResonance, decision.Mode);

        Assert.Equal(

            [("spell-catalog-embedding", 1), ("spell-routing-embedding", 1)],

            observed);

    }

    [Fact]
    public async Task ResolveAsync_PureMode_NoSpellAboveThreshold_ReturnsDirectResonanceWithNullSpell()
    {
        FakeWeaveService weave = new() { QueryVector = [1f, 0f] };

        weave.BatchVectorsByText["alpha description"] = [0f, 1f];

        weave.BatchVectorsByText["beta description"] = [-1f, 0f];

        SemanticSpellRouter router = CreateRouter(weave, BaseSettings(enabled: true));

        SpellRoutingDecision decision = await router.ResolveAsync([AlphaSpell, BetaSpell], "prompt", CancellationToken.None);

        Assert.Equal(SpellRoutingDecisionMode.DirectResonance, decision.Mode);

        Assert.Null(decision.ResolvedSpell);
    }

    [Fact]
    public async Task ResolveAsync_PureMode_DimensionMismatch_FallsBackToFullGrimoire_NotDirectResonanceWithNullSpell()
    {

        // The query embedding (3-dim) does not match either cached spell embedding's dimension
        // (2-dim) — e.g. a stale spell-catalog cache after an embedding model/provider change.
        // EmbeddingBlobCodec.CosineSimilarity would silently return 0 for this, which is
        // indistinguishable from a genuine "no match"; the router must detect the mismatch and
        // fall back to full-catalog LLM routing instead of a confident DirectResonance(null).
        FakeWeaveService weave = new() { QueryVector = [1f, 0f, 0f] };

        weave.BatchVectorsByText["alpha description"] = [1f, 0f];

        weave.BatchVectorsByText["beta description"] = [0f, 1f];

        SemanticSpellRouter router = CreateRouter(weave, BaseSettings(enabled: true));

        SpellRoutingDecision decision = await router.ResolveAsync([AlphaSpell, BetaSpell], "prompt", CancellationToken.None);

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, decision.Mode);

    }

    [Fact]
    public async Task ResolveAsync_PureMode_CacheUnavailable_FallsBackToFullGrimoire()
    {
        FakeWeaveService weave = new() { Available = false };

        SemanticSpellRouter router = CreateRouter(weave, BaseSettings(enabled: true));

        SpellRoutingDecision decision = await router.ResolveAsync([AlphaSpell], "prompt", CancellationToken.None);

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, decision.Mode);
    }

    [Fact]
    public async Task ResolveAsync_PureMode_PromptEmbedFails_FallsBackToFullGrimoire()
    {
        FakeWeaveService weave = new() { FailEmbed = true };

        SemanticSpellRouter router = CreateRouter(weave, BaseSettings(enabled: true));

        SpellRoutingDecision decision = await router.ResolveAsync([AlphaSpell], "prompt", CancellationToken.None);

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, decision.Mode);
    }

    [Fact]
    public async Task ResolveAsync_NeverThrows_OnUnexpectedEmbedException()
    {
        FakeWeaveService weave = new() { ThrowOnEmbed = true };

        SemanticSpellRouter router = CreateRouter(weave, BaseSettings(enabled: true));

        SpellRoutingDecision decision = await router.ResolveAsync([AlphaSpell], "prompt", CancellationToken.None);

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, decision.Mode);
    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public bool Available { get; set; } = true;

        public bool FailEmbed { get; set; }

        public bool FailBatch { get; set; }

        public bool ThrowOnEmbed { get; set; }

        public float[] QueryVector { get; set; } = [1f, 0f];

        public Dictionary<string, float[]> BatchVectorsByText { get; } = new(StringComparer.Ordinal);

        public int EmbedCallCount { get; private set; }

        public int EmbedBatchCallCount { get; private set; }

        public bool IsAvailable => Available;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            EmbedCallCount++;

            if (ThrowOnEmbed)
            {
                throw new InvalidOperationException("Simulated unexpected embedding failure.");
            }

            if (FailEmbed)
            {
                return Task.FromResult(Result<Embedding<float>>.Failure(
                    new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated embedding failure.")));
            }

            return Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(QueryVector)));
        }

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            EmbedBatchCallCount++;

            if (FailBatch)
            {
                return Task.FromResult(Result<Embedding<float>[]>.Failure(
                    new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated batch embedding failure.")));
            }

            Embedding<float>[] result = new Embedding<float>[texts.Count];

            for (int i = 0; i < texts.Count; i++)
            {
                float[] vector = BatchVectorsByText.TryGetValue(texts[i], out float[]? registered) ? registered : QueryVector;

                result[i] = new Embedding<float>(vector);
            }

            return Task.FromResult(Result<Embedding<float>[]>.Success(result));
        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SemanticSpellRouter.");

    }

}
