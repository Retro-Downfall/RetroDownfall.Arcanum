using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>RAG Phase 5 — <see cref="SpellWeaveCache"/> caching, invalidation, and graceful degradation.</summary>
public sealed class SpellWeaveCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagnostics_CacheFailuresKeepOnlyClosedReasonAndSafeExceptionType(bool thrown)
    {
        FakeWeaveService weave = new() { FailBatch = !thrown, BatchException = thrown ? new IOException("private-exception-canary") : null };

        TestCapturingLogger<SpellWeaveCache> logger = new();

        using SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new()), logger);

        Assert.Null(await cache.GetOrCreateAsync([new("private-spell-canary", "private-description-canary", "/private/spell-canary/SPELL.md")], CancellationToken.None));

        TestLogEntry entry = Assert.Single(logger.Entries);

        Assert.Equal(LogLevel.Debug, entry.Level);

        Assert.Null(entry.Exception);

        Assert.DoesNotContain("private-", entry.Message);

        Assert.DoesNotContain("/private/", entry.Message);

        Assert.Equal(thrown ? "catalog_refresh_failed" : "catalog_embedding_failed", entry.Properties!.Single(p => p.Key == "Reason").Value);

        if (thrown) Assert.Equal(typeof(IOException).FullName, entry.Properties!.Single(p => p.Key == "ExceptionType").Value);

        Assert.DoesNotContain(entry.Properties!, p => p.Value is Exception);

        Assert.DoesNotContain("private-", string.Join(" ", entry.Properties!.Select(p => p.Value)));

        Assert.Equal(1, weave.EmbedBatchCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_FirstCall_EmbedsTheCatalogOnce()
    {
        FakeWeaveService weave = new();

        SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), NullLogger<SpellWeaveCache>.Instance);

        List<SpellMetadata> spells = [new SpellMetadata("Alpha", "alpha desc", "/a/SPELL.md")];

        IReadOnlyDictionary<string, Embedding<float>>? result = await cache.GetOrCreateAsync(spells, CancellationToken.None);

        Assert.NotNull(result);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        Assert.True(result!.ContainsKey("Alpha"));
    }

    [Fact]
    public async Task GetOrCreateAsync_SameMetadataOnSecondCall_ReusesCache_DoesNotReEmbed()
    {
        FakeWeaveService weave = new();

        SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), NullLogger<SpellWeaveCache>.Instance);

        List<SpellMetadata> spells = [new SpellMetadata("Alpha", "alpha desc", "/a/SPELL.md")];

        IReadOnlyDictionary<string, Embedding<float>>? first = await cache.GetOrCreateAsync(spells, CancellationToken.None);

        IReadOnlyDictionary<string, Embedding<float>>? second = await cache.GetOrCreateAsync(spells, CancellationToken.None);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetOrCreateAsync_NewSpellAddedToCatalog_ReEmbeds()
    {
        FakeWeaveService weave = new();

        SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), NullLogger<SpellWeaveCache>.Instance);

        List<SpellMetadata> original = [new SpellMetadata("Alpha", "alpha desc", "/a/SPELL.md")];

        _ = await cache.GetOrCreateAsync(original, CancellationToken.None);

        List<SpellMetadata> withNewSpell = [.. original, new SpellMetadata("Beta", "beta desc", "/b/SPELL.md")];

        IReadOnlyDictionary<string, Embedding<float>>? result = await cache.GetOrCreateAsync(withNewSpell, CancellationToken.None);

        Assert.Equal(2, weave.EmbedBatchCallCount);

        Assert.True(result!.ContainsKey("Alpha"));

        Assert.True(result.ContainsKey("Beta"));
    }

    [Fact]
    public async Task GetOrCreateAsync_SpellRemovedFromCatalog_ReEmbeds()
    {
        FakeWeaveService weave = new();

        SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), NullLogger<SpellWeaveCache>.Instance);

        List<SpellMetadata> original =
        [
            new SpellMetadata("Alpha", "alpha desc", "/a/SPELL.md"),
            new SpellMetadata("Beta", "beta desc", "/b/SPELL.md"),
        ];

        _ = await cache.GetOrCreateAsync(original, CancellationToken.None);

        List<SpellMetadata> withoutBeta = [original[0]];

        IReadOnlyDictionary<string, Embedding<float>>? result = await cache.GetOrCreateAsync(withoutBeta, CancellationToken.None);

        Assert.Equal(2, weave.EmbedBatchCallCount);

        Assert.True(result!.ContainsKey("Alpha"));

        Assert.False(result.ContainsKey("Beta"));
    }

    [Fact]
    public async Task GetOrCreateAsync_DescriptionChanged_ReEmbeds()
    {
        FakeWeaveService weave = new();

        SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), NullLogger<SpellWeaveCache>.Instance);

        List<SpellMetadata> original = [new SpellMetadata("Alpha", "original desc", "/a/SPELL.md")];

        _ = await cache.GetOrCreateAsync(original, CancellationToken.None);

        List<SpellMetadata> changed = [new SpellMetadata("Alpha", "changed desc", "/a/SPELL.md")];

        _ = await cache.GetOrCreateAsync(changed, CancellationToken.None);

        Assert.Equal(2, weave.EmbedBatchCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_EmbeddingModelChangedViaHotReload_ReEmbeds()
    {
        // Same spell catalog, but the operator hot-reloads Arcanum:Integrations:Embeddings:Model — a cache key
        // built only from spell name/description pairs would keep serving stale vectors (embedded
        // against the OLD model) forever, since the catalog content itself never changed.
        FakeWeaveService weave = new();

        MutableTestOptionsMonitor<ArcanumSettings> monitor = new(new ArcanumSettings
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "p1",
                    Model = "model-a",
                    Dimensions = 768,
                },
            },
        });

        SpellWeaveCache cache = new(weave, monitor, NullLogger<SpellWeaveCache>.Instance);

        List<SpellMetadata> spells = [new SpellMetadata("Alpha", "alpha desc", "/a/SPELL.md")];

        _ = await cache.GetOrCreateAsync(spells, CancellationToken.None);

        monitor.CurrentValue = monitor.CurrentValue with
        {
            Integrations = monitor.CurrentValue.Integrations with
            {
                Embeddings = monitor.CurrentValue.Integrations.Embeddings with
                {
                    Model = "model-b",
                },
            },
        };

        _ = await cache.GetOrCreateAsync(spells, CancellationToken.None);

        Assert.Equal(2, weave.EmbedBatchCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_ProviderAnswersFewerVectorsThanDescriptions_ReturnsNull()
    {
        // A provider answering fewer vectors than requested is turned into a failure by the real
        // WeaveService, once, at the provider boundary; the cache does not re-check the count and
        // only has to degrade to LLM routing instead of pairing the wrong spell with a vector.
        IWeaveService weave = ShortAnsweringEmbeddingGeneratorFactory.CreateWeaveService();

        TestCapturingLogger<SpellWeaveCache> logger = new();

        SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), logger);

        List<SpellMetadata> spells =
        [
            new SpellMetadata("Alpha", "alpha desc", "/a/SPELL.md"),
            new SpellMetadata("Beta", "beta desc", "/b/SPELL.md"),
        ];

        IReadOnlyDictionary<string, Embedding<float>>? result = await cache.GetOrCreateAsync(spells, CancellationToken.None);

        Assert.Null(result);

        // The null must come from the service's typed failure, not from the cache's catch-all swallowing
        // an index fault, or this test would pass with the service's count guard removed: the failure
        // is reported by code, and no exception was ever logged.
        Assert.Contains(
            logger.Entries,
            static entry => entry.Properties!.Any(property => property.Key == "Reason" && Equals(property.Value, "catalog_embedding_failed")));

        Assert.DoesNotContain(logger.Entries, static entry => entry.Exception is not null);
    }

    [Fact]
    public async Task GetOrCreateAsync_WeaveUnavailable_ReturnsNull()
    {
        FakeWeaveService weave = new() { Available = false };

        SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), NullLogger<SpellWeaveCache>.Instance);

        IReadOnlyDictionary<string, Embedding<float>>? result = await cache.GetOrCreateAsync(
            [new SpellMetadata("Alpha", "desc", "/a/SPELL.md")],
            CancellationToken.None);

        Assert.Null(result);

        Assert.Equal(0, weave.EmbedBatchCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_EmbedBatchFails_ReturnsNull()
    {
        FakeWeaveService weave = new() { FailBatch = true };

        SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), NullLogger<SpellWeaveCache>.Instance);

        IReadOnlyDictionary<string, Embedding<float>>? result = await cache.GetOrCreateAsync(
            [new SpellMetadata("Alpha", "desc", "/a/SPELL.md")],
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOrCreateAsync_ConcurrentFirstAccess_EmbedsOnlyOnceUnderLock()
    {
        FakeWeaveService weave = new() { Delay = TimeSpan.FromMilliseconds(75) };

        SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), NullLogger<SpellWeaveCache>.Instance);

        List<SpellMetadata> spells = [new SpellMetadata("Alpha", "desc", "/a/SPELL.md")];

        Task<IReadOnlyDictionary<string, Embedding<float>>?>[] tasks =
        [
            cache.GetOrCreateAsync(spells, CancellationToken.None),
            cache.GetOrCreateAsync(spells, CancellationToken.None),
            cache.GetOrCreateAsync(spells, CancellationToken.None),
        ];

        IReadOnlyDictionary<string, Embedding<float>>?[] results = await Task.WhenAll(tasks);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        Assert.All(results, static r => Assert.NotNull(r));
    }

    [Fact]
    public async Task Alternating_catalogs_reuse_each_catalog_without_reembedding_or_crossing_vectors()
    {
        FakeWeaveService weave = new();

        using SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new()), NullLogger<SpellWeaveCache>.Instance);

        SpellMetadata[] first = [new("Shared", "global description", "/global/SPELL.md"), new("LocalA", "local A", "/a/SPELL.md")];

        SpellMetadata[] second = [new("Shared", "global description", "/global/SPELL.md"), new("LocalB", "local B", "/b/SPELL.md")];

        for (int turn = 0; turn < 6; turn++)
        {
            var result = await cache.GetOrCreateAsync(turn % 2 == 0 ? first : second, CancellationToken.None);

            Assert.NotNull(result);

            Assert.Equal(2, result.Count);

            Assert.True(result.ContainsKey(turn % 2 == 0 ? "LocalA" : "LocalB"));

            Assert.False(result.ContainsKey(turn % 2 == 0 ? "LocalB" : "LocalA"));
        }

        Assert.Equal(2, weave.EmbedBatchCallCount);
    }

    [Fact]
    public async Task Catalog_cache_has_a_fixed_eight_snapshot_bound_and_retains_recently_used_catalog()
    {
        FakeWeaveService weave = new();

        using SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new()), NullLogger<SpellWeaveCache>.Instance);

        SpellMetadata[] Catalog(int index) => [new($"Spell{index}", $"description {index}", $"/{index}/SPELL.md")];

        for (int index = 0; index < 8; index++) Assert.NotNull(await cache.GetOrCreateAsync(Catalog(index), CancellationToken.None));

        Assert.NotNull(await cache.GetOrCreateAsync(Catalog(0), CancellationToken.None));

        Assert.Equal(8, weave.EmbedBatchCallCount);

        Assert.NotNull(await cache.GetOrCreateAsync(Catalog(8), CancellationToken.None));

        Assert.NotNull(await cache.GetOrCreateAsync(Catalog(0), CancellationToken.None));

        Assert.Equal(9, weave.EmbedBatchCallCount);

        Assert.NotNull(await cache.GetOrCreateAsync(Catalog(1), CancellationToken.None));

        Assert.Equal(10, weave.EmbedBatchCallCount);
    }

    [Fact]
    public async Task Published_catalog_map_cannot_be_mutated_by_a_caller()
    {
        FakeWeaveService weave = new();

        using SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new()), NullLogger<SpellWeaveCache>.Instance);

        var result = await cache.GetOrCreateAsync([new("Alpha", "description", "/a/SPELL.md")], CancellationToken.None);

        Assert.NotNull(result);

        Assert.False(result is IDictionary<string, Embedding<float>> { IsReadOnly: false });
    }

    [Fact]
    public async Task Waiting_catalog_refresh_uses_the_captured_metadata_not_a_mutated_caller_list()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeWeaveService weave = new()
        {
            BeforeBatchAsync = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); },
        };

        using SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new()), NullLogger<SpellWeaveCache>.Instance);

        var first = cache.GetOrCreateAsync([new("First", "first", "/first/SPELL.md")], CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        SpellMetadata[] metadata = [new("Original", "original", "/original/SPELL.md")];

        var waiting = cache.GetOrCreateAsync(metadata, CancellationToken.None);

        metadata[0] = new("Replacement", "replacement", "/replacement/SPELL.md");

        release.SetResult();

        Assert.NotNull(await first);

        var result = await waiting;

        Assert.NotNull(result);

        Assert.True(result.ContainsKey("Original"));

        Assert.False(result.ContainsKey("Replacement"));
    }

    [Fact]
    public async Task Configuration_change_during_generation_does_not_publish_under_the_old_binding()
    {
        MutableTestOptionsMonitor<ArcanumSettings> monitor = new(new()
        {
            Integrations = new() { Embeddings = new() { Provider = "provider", Model = "before", Dimensions = 768 } },
        });

        ArcanumSettings original = monitor.CurrentValue;

        FakeWeaveService weave = new()
        {
            BeforeBatchAsync = _ =>
            {
                monitor.CurrentValue = original with
                {
                    Integrations = original.Integrations with { Embeddings = original.Integrations.Embeddings with { Model = "after" } },
                };

                return Task.CompletedTask;
            },
        };

        using SpellWeaveCache cache = new(weave, monitor, NullLogger<SpellWeaveCache>.Instance);

        SpellMetadata[] catalog = [new("Alpha", "description", "/a/SPELL.md")];

        Assert.Null(await cache.GetOrCreateAsync(catalog, CancellationToken.None));

        monitor.CurrentValue = original;

        weave.BeforeBatchAsync = null;

        Assert.NotNull(await cache.GetOrCreateAsync(catalog, CancellationToken.None));

        Assert.Equal(2, weave.EmbedBatchCallCount);
    }

    [Fact]
    public async Task Concurrent_alternating_catalogs_embed_each_catalog_once()
    {
        FakeWeaveService weave = new() { Delay = TimeSpan.FromMilliseconds(25) };

        using SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new()), NullLogger<SpellWeaveCache>.Instance);

        var requests = Enumerable.Range(0, 16).Select(index => cache.GetOrCreateAsync(
            [new($"Spell{index % 2}", $"description {index % 2}", "/spell/SPELL.md")], CancellationToken.None)).ToArray();

        var results = await Task.WhenAll(requests);

        for (int index = 0; index < results.Length; index++) Assert.True(results[index]!.ContainsKey($"Spell{index % 2}"));

        Assert.Equal(2, weave.EmbedBatchCallCount);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("exception")]
    [InlineData("cancellation")]
    public async Task Failed_refresh_preserves_existing_catalogs_and_does_not_publish_the_failed_catalog(string mode)
    {
        FakeWeaveService weave = new();

        using SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new()), NullLogger<SpellWeaveCache>.Instance);

        SpellMetadata[] first = [new("First", "first", "/first/SPELL.md")];

        SpellMetadata[] second = [new("Second", "second", "/second/SPELL.md")];

        SpellMetadata[] failed = [new("Failed", "failed", "/failed/SPELL.md")];

        var firstSnapshot = await cache.GetOrCreateAsync(first, CancellationToken.None);

        var secondSnapshot = await cache.GetOrCreateAsync(second, CancellationToken.None);

        using CancellationTokenSource caller = new();

        weave.FailBatch = mode == "failure";

        weave.BeforeBatchAsync = ct =>
        {
            if (mode == "exception") throw new InvalidOperationException("controlled failure");

            if (mode == "cancellation")
            {
                caller.Cancel();

                ct.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        };

        if (mode == "cancellation")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateAsync(failed, caller.Token));
        }
        else
        {
            Assert.Null(await cache.GetOrCreateAsync(failed, caller.Token));
        }

        weave.FailBatch = false;

        weave.BeforeBatchAsync = null;

        Assert.Same(firstSnapshot, await cache.GetOrCreateAsync(first, CancellationToken.None));

        Assert.Same(secondSnapshot, await cache.GetOrCreateAsync(second, CancellationToken.None));

        Assert.Equal(3, weave.EmbedBatchCallCount);

        Assert.NotNull(await cache.GetOrCreateAsync(failed, CancellationToken.None));

        Assert.Equal(4, weave.EmbedBatchCallCount);
    }

    private sealed class FakeWeaveService : IWeaveService
    {
        public bool Available { get; set; } = true;

        public bool FailBatch { get; set; }
        public Exception? BatchException { get; init; }

        public TimeSpan Delay { get; set; }

        public Func<CancellationToken, Task>? BeforeBatchAsync { get; set; }

        public int EmbedBatchCallCount { get; private set; }

        public bool IsAvailable => Available;

        public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            EmbedBatchCallCount++;

            if (BeforeBatchAsync is not null) await BeforeBatchAsync(cancellationToken);

            if (BatchException is not null) throw BatchException;

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            if (FailBatch)
            {
                return Result<Embedding<float>[]>.Failure(
                    new Error("private-error-code-canary", "private-error-message-canary"));
            }

            Embedding<float>[] result = new Embedding<float>[texts.Count];

            for (int i = 0; i < texts.Count; i++)
            {
                result[i] = new Embedding<float>(new float[] { i + 1 });
            }

            return Result<Embedding<float>[]>.Success(result);
        }

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SpellWeaveCache.");

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SpellWeaveCache.");
    }
}
