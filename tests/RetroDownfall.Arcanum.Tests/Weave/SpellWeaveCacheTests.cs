using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public async Task GetOrCreateAsync_FirstCall_EmbedsTheCatalogOnce()
    {
        FakeWeaveService weave = new();

        SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), NullLogger<SpellWeaveCache>.Instance);

        List<SpellMetadata> spells = [new SpellMetadata("Alpha", "alpha desc", "/a/SPELL.md")];

        ConcurrentDictionary<string, Embedding<float>>? result = await cache.GetOrCreateAsync(spells, CancellationToken.None);

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

        ConcurrentDictionary<string, Embedding<float>>? first = await cache.GetOrCreateAsync(spells, CancellationToken.None);

        ConcurrentDictionary<string, Embedding<float>>? second = await cache.GetOrCreateAsync(spells, CancellationToken.None);

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

        ConcurrentDictionary<string, Embedding<float>>? result = await cache.GetOrCreateAsync(withNewSpell, CancellationToken.None);

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

        ConcurrentDictionary<string, Embedding<float>>? result = await cache.GetOrCreateAsync(withoutBeta, CancellationToken.None);

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
    public async Task GetOrCreateAsync_EmbedBatchReturnsWrongCount_ReturnsNull()
    {

        // A provider returning fewer vectors than requested is a shape mismatch that must be
        // rejected explicitly, rather than allowed to throw IndexOutOfRangeException (opaque to
        // operators) or silently pair the wrong spell with the wrong vector.
        FakeWeaveService weave = new() { ShortBatchResponse = true };

        SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), NullLogger<SpellWeaveCache>.Instance);

        List<SpellMetadata> spells =
        [
            new SpellMetadata("Alpha", "alpha desc", "/a/SPELL.md"),
            new SpellMetadata("Beta", "beta desc", "/b/SPELL.md"),
        ];

        ConcurrentDictionary<string, Embedding<float>>? result = await cache.GetOrCreateAsync(spells, CancellationToken.None);

        Assert.Null(result);

    }

    [Fact]
    public async Task GetOrCreateAsync_WeaveUnavailable_ReturnsNull()
    {
        FakeWeaveService weave = new() { Available = false };

        SpellWeaveCache cache = new(weave, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), NullLogger<SpellWeaveCache>.Instance);

        ConcurrentDictionary<string, Embedding<float>>? result = await cache.GetOrCreateAsync(
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

        ConcurrentDictionary<string, Embedding<float>>? result = await cache.GetOrCreateAsync(
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

        Task<ConcurrentDictionary<string, Embedding<float>>?>[] tasks =
        [
            cache.GetOrCreateAsync(spells, CancellationToken.None),
            cache.GetOrCreateAsync(spells, CancellationToken.None),
            cache.GetOrCreateAsync(spells, CancellationToken.None),
        ];

        ConcurrentDictionary<string, Embedding<float>>?[] results = await Task.WhenAll(tasks);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        Assert.All(results, static r => Assert.NotNull(r));
    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public bool Available { get; set; } = true;

        public bool FailBatch { get; set; }

        public bool ShortBatchResponse { get; set; }

        public TimeSpan Delay { get; set; }

        public int EmbedBatchCallCount { get; private set; }

        public bool IsAvailable => Available;

        public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            EmbedBatchCallCount++;

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            if (FailBatch)
            {
                return Result<Embedding<float>[]>.Failure(
                    new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated batch embedding failure."));
            }

            int returnedCount = ShortBatchResponse ? Math.Max(0, texts.Count - 1) : texts.Count;

            Embedding<float>[] result = new Embedding<float>[returnedCount];

            for (int i = 0; i < returnedCount; i++)
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
