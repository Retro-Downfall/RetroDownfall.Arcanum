using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
    private static readonly SpellMetadata DiagnosticSpell = new("private-spell-canary", "private-description-canary", "/private/spell-canary/SPELL.md");

    [Theory]
    [InlineData("catalog", "catalog_unavailable")]
    [InlineData("provider", "provider_unavailable")]
    [InlineData("prompt", "prompt_embedding_failed")]
    [InlineData("dimension", "dimension_mismatch")]
    [InlineData("missing", "catalog_vectors_missing")]
    [InlineData("unexpected", "unexpected_error")]
    public async Task Diagnostics_RealScopedRoutersWarnOncePerClosedReasonAndMonitorEpoch(string mode, string reason)
    {
        using ScopedRoutingHarness h = new();

        await h.PrepareAsync(mode);

        using IServiceScope first = h.Services.CreateScope();

        using IServiceScope second = h.Services.CreateScope();

        Assert.NotSame(first.ServiceProvider.GetRequiredService<SemanticSpellRouter>(), second.ServiceProvider.GetRequiredService<SemanticSpellRouter>());

        Assert.NotSame(first.ServiceProvider.GetRequiredService<IOptionsSnapshot<ArcanumSettings>>().Value,
            second.ServiceProvider.GetRequiredService<IOptionsSnapshot<ArcanumSettings>>().Value);

        ArcanumSettings epoch = h.Monitor.CurrentValue;

        Assert.Same(epoch, h.Monitor.CurrentValue);

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, (await h.ResolveAsync(first)).Mode);

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, (await h.ResolveAsync(second)).Mode);

        TestLogEntry warning = Assert.Single(h.RouterLog.Entries, e => e.Level == LogLevel.Warning);

        AssertSafeWarning(warning, reason);

        h.ReplaceEpoch();

        Assert.NotSame(epoch, h.Monitor.CurrentValue);

        using IServiceScope third = h.Services.CreateScope();

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, (await h.ResolveAsync(third)).Mode);

        Assert.Equal(2, h.RouterLog.Entries.Count(e => e.Level == LogLevel.Warning));

        Assert.All(h.RouterLog.Entries.Where(e => e.Level == LogLevel.Warning), e => AssertSafeWarning(e, reason));

        Assert.Equal(mode == "catalog" ? 3 : 1, h.Weave.EmbedBatchCallCount);

        Assert.Equal(mode is "catalog" or "provider" ? 0 : 3, h.Weave.EmbedCallCount);

        Assert.All(h.CacheLog.Entries, AssertSafeLog);
    }

    [Fact]
    public async Task Diagnostics_ParallelScopesShareReasonStateWithoutSuppressingOtherReasons()
    {
        using ScopedRoutingHarness h = new();

        await h.PrepareAsync("prompt");

        using CountdownEvent entered = new(8);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        h.Weave.BeforeQueryAsync = async ct =>
        {
            entered.Signal();

            await release.Task.WaitAsync(ct);
        };

        IServiceScope[] scopes = Enumerable.Range(0, 8).Select(_ => h.Services.CreateScope()).ToArray();

        try
        {
            Task<SpellRoutingDecision>[] calls = scopes.Select(scope => h.ResolveAsync(scope)).ToArray();

            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

            release.SetResult();

            Assert.All(await Task.WhenAll(calls), result => Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, result.Mode));

            Assert.Single(h.RouterLog.Entries, e => e.Level == LogLevel.Warning);

            h.Weave.BeforeQueryAsync = null;

            h.Weave.FailEmbed = false;

            h.Weave.QueryException = new IOException("private-exception-canary");

            using IServiceScope next = h.Services.CreateScope();

            Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, (await h.ResolveAsync(next)).Mode);

            Assert.Equal(2, h.RouterLog.Entries.Count(e => e.Level == LogLevel.Warning));

            Assert.Contains(h.RouterLog.Entries, e => e.Properties!.Any(p => p.Key == "Reason" && Equals(p.Value, "unexpected_error")));

            Assert.Equal(9, h.Weave.EmbedCallCount);

            Assert.Equal(1, h.Weave.EmbedBatchCallCount);

            Assert.All(h.RouterLog.Entries, AssertSafeLog);
        }
        finally
        {
            release.TrySetResult();

            foreach (IServiceScope scope in scopes) scope.Dispose();
        }
    }

    [Fact]
    public async Task Diagnostics_InFlightFailureKeepsCapturedEpochAfterMonitorReplacement()
    {
        using ScopedRoutingHarness h = new();

        await h.PrepareAsync("prompt");

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int calls = 0;

        h.Weave.BeforeQueryAsync = async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();

                await release.Task.WaitAsync(ct);
            }
        };

        using IServiceScope first = h.Services.CreateScope();

        Task<SpellRoutingDecision> old = h.ResolveAsync(first);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        h.ReplaceEpoch();

        using IServiceScope second = h.Services.CreateScope();

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, (await h.ResolveAsync(second)).Mode);

        release.SetResult();

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, (await old).Mode);

        using IServiceScope third = h.Services.CreateScope();

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, (await h.ResolveAsync(third)).Mode);

        Assert.Equal(2, h.RouterLog.Entries.Count(e => e.Level == LogLevel.Warning));

        Assert.All(h.RouterLog.Entries, e => AssertSafeWarning(e, "prompt_embedding_failed"));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("empty")]
    [InlineData("no-match")]
    [InlineData("match")]
    public async Task Diagnostics_NormalRoutingDecisionsDoNotWarn(string mode)
    {
        using ScopedRoutingHarness h = new();

        h.Enabled = mode != "disabled";

        if (mode == "no-match") h.Weave.QueryVector = [0f, 1f];

        using IServiceScope scope = h.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<SemanticSpellRouter>().ResolveAsync(
            mode == "empty" ? [] : [DiagnosticSpell], "private-prompt-canary", CancellationToken.None);

        Assert.DoesNotContain(h.RouterLog.Entries, e => e.Level >= LogLevel.Warning);

        Assert.Equal(mode is "disabled" or "empty" ? SpellRoutingDecisionMode.FullGrimoire : SpellRoutingDecisionMode.DirectResonance, result.Mode);

        if (mode == "match") Assert.Same(DiagnosticSpell, result.ResolvedSpell);

        if (mode == "no-match") Assert.Null(result.ResolvedSpell);

        Assert.Equal(mode is "disabled" or "empty" ? 0 : 1, h.Weave.EmbedCallCount);

        Assert.Equal(mode is "disabled" or "empty" ? 0 : 1, h.Weave.EmbedBatchCallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Diagnostics_RequestedCancellationPropagatesWithoutWarning(bool catalog)
    {
        using ScopedRoutingHarness h = new();

        using CancellationTokenSource cancellation = new();

        Func<CancellationToken, Task> cancel = ct =>
        {
            cancellation.Cancel();

            ct.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        };

        if (catalog) h.Weave.BeforeBatchAsync = cancel;
        else
        {
            await h.PrepareAsync("healthy");

            h.Weave.BeforeQueryAsync = cancel;
        }

        using IServiceScope scope = h.Services.CreateScope();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.ResolveAsync(scope, cancellation.Token));

        Assert.Empty(h.RouterLog.Entries);

        Assert.Empty(h.CacheLog.Entries);
    }

    [Fact]
    public async Task Diagnostics_UnrequestedCancellationRemainsUnexpectedFallbackWithoutPrivatePayload()
    {
        using ScopedRoutingHarness h = new();

        await h.PrepareAsync("healthy");

        h.Weave.QueryException = new OperationCanceledException("private-unrequested-canary", new CancellationToken(canceled: true));

        using IServiceScope scope = h.Services.CreateScope();

        Assert.Equal(SpellRoutingDecisionMode.FullGrimoire, (await h.ResolveAsync(scope)).Mode);

        TestLogEntry warning = Assert.Single(h.RouterLog.Entries);

        Assert.Equal(LogLevel.Warning, warning.Level);

        AssertSafeLog(warning);

        Assert.Equal("unexpected_error", warning.Properties!.Single(p => p.Key == "Reason").Value);

        Assert.Equal(typeof(OperationCanceledException).FullName, warning.Properties!.Single(p => p.Key == "ExceptionType").Value);

        Assert.Equal(1, h.Weave.EmbedCallCount);

        Assert.Equal(1, h.Weave.EmbedBatchCallCount);
    }

    private static void AssertSafeWarning(TestLogEntry entry, string reason)
    {
        Assert.Equal(LogLevel.Warning, entry.Level);

        AssertSafeLog(entry);

        Assert.Equal(reason, entry.Properties!.Single(p => p.Key == "Reason").Value);

        Assert.Contains("billable", entry.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("LLM", entry.Message);

        Assert.Contains("integrations.embeddings.provider", entry.Message);

        Assert.Contains("integrations.embeddings.model", entry.Message);

        Assert.Contains("integrations.embeddings.dimensions", entry.Message);

        Assert.Contains("arcanum doctor", entry.Message);

        Assert.Equal(reason == "unexpected_error" ? typeof(InvalidOperationException).FullName : "none",
            entry.Properties!.Single(p => p.Key == "ExceptionType").Value);
    }

    private static void AssertSafeLog(TestLogEntry entry)
    {
        Assert.Null(entry.Exception);

        Assert.DoesNotContain("private-", entry.Message);

        Assert.DoesNotContain("/private/", entry.Message);

        Assert.All(entry.Properties ?? [], p => Assert.False(p.Value is Exception));

        Assert.DoesNotContain("private-", string.Join(" ", (entry.Properties ?? []).Select(p => p.Value)));
    }

    private sealed class ScopedRoutingHarness : IDisposable
    {
        internal FakeWeaveService Weave { get; } = new();

        internal TestCapturingLogger<SemanticSpellRouter> RouterLog { get; } = new();

        internal TestCapturingLogger<SpellWeaveCache> CacheLog { get; } = new();

        internal ServiceProvider Services { get; }
        internal bool Enabled { get; set; } = true;

        internal IOptionsMonitor<ArcanumSettings> Monitor => Services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

        internal ScopedRoutingHarness()
        {
            Weave.BatchVectorsByText[DiagnosticSpell.Description] = [1f, 0f];

            ServiceCollection services = new();

            services.AddOptions<ArcanumSettings>().Configure(settings =>
            {
                settings.Features = new() { Embeddings = Enabled, SemanticSpellRouting = Enabled };

                settings.Integrations = new() { Embeddings = new() { Provider = "private-provider-canary", Model = "private-model-canary" } };
            });

            services.AddSingleton<IWeaveService>(Weave);

            services.AddSingleton<ILogger<SemanticSpellRouter>>(RouterLog);

            services.AddSingleton<ILogger<SpellWeaveCache>>(CacheLog);

            services.AddSingleton<SpellWeaveCache>();

            services.AddScoped<SemanticSpellRouter>();

            Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }

        internal async Task PrepareAsync(string mode)
        {
            if (mode == "catalog")
            {
                Weave.FailBatch = true;

                return;
            }

            var vectors = await Services.GetRequiredService<SpellWeaveCache>().GetOrCreateAsync([DiagnosticSpell], CancellationToken.None);

            Assert.NotNull(vectors);

            if (mode == "provider") Weave.Available = false;

            if (mode == "prompt") Weave.FailEmbed = true;

            if (mode == "dimension") Weave.QueryVector = [1f, 0f, 0f];

            if (mode == "missing") vectors.Clear();

            if (mode == "unexpected") Weave.QueryException = new InvalidOperationException("private-exception-canary");
        }

        internal void ReplaceEpoch() => Assert.True(Services.GetRequiredService<IOptionsMonitorCache<ArcanumSettings>>().TryRemove(Options.DefaultName));

        internal Task<SpellRoutingDecision> ResolveAsync(IServiceScope scope, CancellationToken ct = default) =>
            scope.ServiceProvider.GetRequiredService<SemanticSpellRouter>().ResolveAsync([DiagnosticSpell], "private-prompt-canary", ct);

        public void Dispose() => Services.Dispose();
    }

    private static readonly SpellMetadata AlphaSpell = new("Alpha", "alpha description", "/a/SPELL.md");

    private static readonly SpellMetadata BetaSpell = new("Beta", "beta description", "/b/SPELL.md");

    private static SemanticSpellRouter CreateRouter(FakeWeaveService weave, ArcanumSettings settings) =>
        new(
            new SpellWeaveCache(weave, new TestOptionsMonitor<ArcanumSettings>(settings), NullLogger<SpellWeaveCache>.Instance),
            weave,
            new TestOptionsMonitor<ArcanumSettings>(settings),
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
        public Exception? QueryException { get; set; }
        public Func<CancellationToken, Task>? BeforeQueryAsync { get; set; }
        public Func<CancellationToken, Task>? BeforeBatchAsync { get; set; }

        public float[] QueryVector { get; set; } = [1f, 0f];

        public Dictionary<string, float[]> BatchVectorsByText { get; } = new(StringComparer.Ordinal);

        private int _embedCalls;

        private int _batchCalls;

        public int EmbedCallCount => Volatile.Read(ref _embedCalls);

        public int EmbedBatchCallCount => Volatile.Read(ref _batchCalls);

        public bool IsAvailable => Available;

        public async Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _embedCalls);

            if (BeforeQueryAsync is not null) await BeforeQueryAsync(cancellationToken);

            if (QueryException is not null) throw QueryException;

            if (ThrowOnEmbed)
            {
                throw new InvalidOperationException("Simulated unexpected embedding failure.");
            }

            if (FailEmbed)
            {
                return Result<Embedding<float>>.Failure(new Error("private-error-code-canary", "private-error-message-canary"));
            }

            return Result<Embedding<float>>.Success(new Embedding<float>(QueryVector));
        }

        public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _batchCalls);

            if (BeforeBatchAsync is not null) await BeforeBatchAsync(cancellationToken);

            if (FailBatch)
            {
                return Result<Embedding<float>[]>.Failure(new Error("private-error-code-canary", "private-error-message-canary"));
            }

            Embedding<float>[] result = new Embedding<float>[texts.Count];

            for (int i = 0; i < texts.Count; i++)
            {
                float[] vector = BatchVectorsByText.TryGetValue(texts[i], out float[]? registered) ? registered : QueryVector;

                result[i] = new Embedding<float>(vector);
            }

            return Result<Embedding<float>[]>.Success(result);
        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SemanticSpellRouter.");
    }
}
