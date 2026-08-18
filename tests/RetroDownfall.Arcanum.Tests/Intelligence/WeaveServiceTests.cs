using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class WeaveServiceTests
{

    [Fact]
    public void IsAvailable_Disabled_ReturnsFalse()
    {

        WeaveService service = CreateService(new ArcanumSettings
        {
            Features = new FeatureSettings { Embeddings = false },
        });

        Assert.False(service.IsAvailable);

    }

    [Fact]
    public void IsAvailable_EnabledWithoutProvider_ReturnsFalse()
    {

        WeaveService service = CreateService(new ArcanumSettings
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings { Model = "nomic-embed-text" },
            },
        });

        Assert.False(service.IsAvailable);

    }

    [Fact]
    public void IsAvailable_EnabledWithoutModel_ReturnsFalse()
    {

        WeaveService service = CreateService(new ArcanumSettings
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings { Provider = "local" },
            },
        });

        Assert.False(service.IsAvailable);

    }

    [Fact]
    public void IsAvailable_EnabledWithProviderAndModel_ReturnsTrue()
    {

        WeaveService service = CreateService(new ArcanumSettings
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "local",
                    Model = "nomic-embed-text",
                },
            },
        });

        Assert.True(service.IsAvailable);

    }

    [Fact]
    public async Task EmbedAsync_Disabled_ReturnsFeatureDisabled_WithoutResolvingGenerator()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        WeaveService service = CreateService(
            new ArcanumSettings { Features = new FeatureSettings { Embeddings = false } },
            factory);

        Result<Embedding<float>> result = await service.EmbedAsync("hello", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Embeddings.FeatureDisabled, result.Error.Code);

        Assert.Equal(0, factory.ResolveCount);

    }

    [Fact]
    public async Task EmbedBatchAsync_Disabled_ReturnsFeatureDisabled_WithoutResolvingGenerator()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        WeaveService service = CreateService(
            new ArcanumSettings { Features = new FeatureSettings { Embeddings = false } },
            factory);

        Result<Embedding<float>[]> result = await service.EmbedBatchAsync(["a", "b"], CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Embeddings.FeatureDisabled, result.Error.Code);

        Assert.Equal(0, factory.ResolveCount);

    }

    [Fact]
    public async Task EmbedBatchAsync_Empty_ReturnsEmptySuccess()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        WeaveService service = CreateService(EnabledSettings(), factory);

        Result<Embedding<float>[]> result = await service.EmbedBatchAsync([], CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Empty(result.Value);

        Assert.Equal(0, factory.ResolveCount);

    }

    [Fact]
    public async Task EmbedBatchAsync_SplitsAtCodeOwnedBatchSize()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        ArcanumSettings settings = EnabledSettings();
        int batchSize = ArcanumSettingClamps.EmbeddingsBatchSize(
            ArcanumRuntimeDefaults.Embeddings.BatchSize);
        string[] inputs = Enumerable
            .Range(0, (batchSize * 2) + 1)
            .Select(static index => $"input-{index}")
            .ToArray();

        WeaveService service = CreateService(settings, factory);

        Result<Embedding<float>[]> result = await service.EmbedBatchAsync(
            inputs,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(inputs.Length, result.Value.Length);

        Assert.Equal(3, factory.ResolveCount);

        Assert.Equal([batchSize, batchSize, 1], factory.Generator.CallSizes);

    }

    [Fact]
    public async Task EmbedBatchAsync_ReservesSanitizedInputBeforeProviderAndLedgersEachBatch()
    {
        FakeEmbeddingGeneratorFactory factory = new();
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new();
        ArcanumSettings settings = EnabledSettings() with
        {
            Cost = new CostSettings
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry { InputPer1M = 1_000_000m },
                },
            },
        };
        factory.Generator.BeforeGenerate = () => Assert.NotNull(reservations.LastRequest);
        WeaveService service = CreateService(settings, factory, writer, reservations);
        int batchSize = ArcanumSettingClamps.EmbeddingsBatchSize(
            ArcanumRuntimeDefaults.Embeddings.BatchSize);
        int chunkSize = ArcanumSettingClamps.EmbeddingsChunkSizeChars(
            ArcanumRuntimeDefaults.Embeddings.ChunkSizeChars);
        string[] inputs =
        [
            new string('a', chunkSize + 100),
            .. Enumerable.Repeat("b", batchSize),
        ];

        Result<Embedding<float>[]> result = await service.EmbedBatchAsync(
            inputs,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, writer.Operations.Count);
        decimal expectedSpend = writer.Operations.Sum(static operation => operation.InputTokens);
        Assert.Equal(expectedSpend, reservations.LastRequest?.ReservedUsd);
        Assert.Equal(1, reservations.ReconcileCount);
        Assert.Equal(expectedSpend, reservations.ReconciledUsd);
    }

    [Fact]
    public async Task EmbedBatchAsync_LaterProviderFailureRetainsEarlierBatchSpend()
    {
        FakeEmbeddingGeneratorFactory factory = new();
        factory.Generator.ThrowOnCallNumber = 2;
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new();
        ArcanumSettings settings = EnabledSettings() with
        {
            Cost = new CostSettings
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry { InputPer1M = 1_000_000m },
                },
            },
        };
        WeaveService service = CreateService(settings, factory, writer, reservations);
        int batchSize = ArcanumSettingClamps.EmbeddingsBatchSize(
            ArcanumRuntimeDefaults.Embeddings.BatchSize);
        string[] inputs = Enumerable.Repeat("a", batchSize + 1).ToArray();

        Result<Embedding<float>[]> result = await service.EmbedBatchAsync(
            inputs,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        BillableOperationRecord operation = Assert.Single(writer.Operations);
        Assert.Equal(batchSize, operation.InputTokens);
        Assert.Equal(1, reservations.ReconcileCount);
        Assert.Equal(operation.ActualCostUsd, reservations.ReconciledUsd);
    }

    /// <summary>
    /// An OpenAI-compatible backend can answer 200 with an empty <c>data</c> array (model still
    /// loading, input silently dropped). The single-text overload must degrade to a
    /// <see cref="Result{T}"/> failure like every other provider fault instead of throwing out of an
    /// API documented as never throwing.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_ProviderReturnsNoVectors_ReturnsProviderUnavailable_NeverThrows()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        factory.Generator.ReturnNoVectors = true;

        WeaveService service = CreateService(EnabledSettings(), factory);

        Result<Embedding<float>> result = await service.EmbedAsync("hello", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Embeddings.ProviderUnavailable, result.Error.Code);

    }

    [Fact]
    public async Task EmbedAsync_ProviderThrows_ReturnsProviderUnavailable_NeverThrows()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        factory.Generator.ThrowOnGenerate = new InvalidOperationException("boom");

        WeaveService service = CreateService(EnabledSettings(), factory);

        Result<Embedding<float>> result = await service.EmbedAsync("hello", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Embeddings.ProviderUnavailable, result.Error.Code);

        // Sanitized: the internal exception message never leaks into the returned error.
        Assert.DoesNotContain("boom", result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task EmbedAsync_CallerCancellation_PropagatesAsOperationCanceled()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        factory.Generator.DelayOnGenerate = TimeSpan.FromSeconds(30);

        WeaveService service = CreateService(EnabledSettings(), factory);

        using CancellationTokenSource cts = new();

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TaskCanceledException>(() => service.EmbedAsync("hello", cts.Token));

    }

    [Fact]
    public async Task EmbedAsync_WithoutCallerCancellation_DoesNotCreateInternalDeadline()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        WeaveService service = CreateService(EnabledSettings(), factory);

        Result<Embedding<float>> result = await service.EmbedAsync("hello", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.False(factory.ResolveCancellationToken.CanBeCanceled);

        Assert.False(factory.Generator.GenerateCancellationToken.CanBeCanceled);

    }

    [Fact]
    public async Task EmbedAsync_Success_ReturnsGeneratedEmbedding()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        WeaveService service = CreateService(EnabledSettings(), factory);

        Result<Embedding<float>> result = await service.EmbedAsync("hello", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(3, result.Value.Vector.Length);

    }

    [Fact]
    public async Task ChunkAsync_EmptyText_ReturnsEmpty()
    {

        WeaveService service = CreateService(new ArcanumSettings());

        Result<(string Chunk, int Offset)[]> result = await service.ChunkAsync(string.Empty, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Empty(result.Value);

    }

    [Fact]
    public async Task ChunkAsync_WorksRegardlessOfIsAvailable()
    {

        // Embeddings disabled entirely — ChunkAsync is pure CPU and must still succeed.
        WeaveService service = CreateService(new ArcanumSettings
        {
            Features = new FeatureSettings { Embeddings = false },
        });

        Assert.False(service.IsAvailable);

        Result<(string Chunk, int Offset)[]> result = await service.ChunkAsync(
            new string('x', 250),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.NotEmpty(result.Value);

    }

    [Fact]
    public async Task ChunkAsync_ProducesOverlappingWindows()
    {
        WeaveService service = CreateService(new ArcanumSettings());
        int chunkSize = ArcanumSettingClamps.EmbeddingsChunkSizeChars(
            ArcanumRuntimeDefaults.Embeddings.ChunkSizeChars);
        int overlap = ArcanumSettingClamps.EmbeddingsChunkOverlapChars(
            ArcanumRuntimeDefaults.Embeddings.ChunkOverlapChars);
        int step = chunkSize - overlap;
        string text = new('a', (chunkSize * 2) + 100);

        Result<(string Chunk, int Offset)[]> result = await service.ChunkAsync(text, CancellationToken.None);

        Assert.True(result.IsSuccess);

        (string Chunk, int Offset)[] chunks = result.Value;

        Assert.Equal(
            [0, step, step * 2],
            chunks.Select(static chunk => chunk.Offset).ToArray());

        Assert.All(chunks, chunk => Assert.True(chunk.Chunk.Length <= chunkSize));

        // Every character of the source text is covered by the final chunk's reach.
        (string Chunk, int Offset) last = chunks[^1];

        Assert.Equal(text.Length, last.Offset + last.Chunk.Length);

    }

    [Fact]
    public async Task ChunkAsync_NeverSplitsASurrogatePairAtEitherEndOfAWindow()
    {

        WeaveService service = CreateService(new ArcanumSettings());

        int chunkSize = ArcanumSettingClamps.EmbeddingsChunkSizeChars(
            ArcanumRuntimeDefaults.Embeddings.ChunkSizeChars);

        int overlap = ArcanumSettingClamps.EmbeddingsChunkOverlapChars(
            ArcanumRuntimeDefaults.Embeddings.ChunkOverlapChars);

        int step = chunkSize - overlap;

        // An astral character straddling the second window's start index. The tail guard alone leaves
        // that window opening on the orphaned low surrogate, which the embedding provider serializes as
        // U+FFFD, so the chunk is no longer the exact source slice.
        string text = new string('a', step) + "\U0001F600" + new string('b', chunkSize);

        Result<(string Chunk, int Offset)[]> result = await service.ChunkAsync(text, CancellationToken.None);

        Assert.True(result.IsSuccess);

        foreach ((string chunk, int offset) in result.Value)
        {

            Assert.False(
                chunk.Length > 0 && char.IsLowSurrogate(chunk[0]),
                $"chunk at offset {offset} begins with an unpaired low surrogate.");

            Assert.False(
                chunk.Length > 0 && char.IsHighSurrogate(chunk[^1]) && offset + chunk.Length < text.Length,
                $"chunk at offset {offset} ends with an unpaired high surrogate.");

        }

        // The emoji must survive intact in whichever chunk claims it.
        Assert.Contains(result.Value, static entry => entry.Chunk.Contains("\U0001F600", StringComparison.Ordinal));

    }

    /// <summary>
    /// The two clamps are independent — <c>EmbeddingsChunkSizeChars</c> admits 128..8,192 and
    /// <c>EmbeddingsChunkOverlapChars</c> admits 0..1,024 — so an overlap at or above the resolved chunk
    /// size is individually legal. Unbounded, the sliding window then advances a single character per
    /// iteration and a document emits one near-duplicate chunk per character.
    /// </summary>
    [Theory]
    [InlineData(128, 128)]
    [InlineData(128, 1_024)]
    [InlineData(1_000, 1_024)]
    [InlineData(8_192, 8_192)]
    public void ResolveChunkStep_OverlapAtOrAboveChunkSize_AdvancesByHalfAWindow(
        int chunkSizeChars,
        int chunkOverlapChars)
    {

        int step = WeaveService.ResolveChunkStep(chunkSizeChars, chunkOverlapChars);

        Assert.Equal(chunkSizeChars - (chunkSizeChars / 2), step);

    }

    /// <summary>
    /// The bounded step is what keeps the emitted chunk count within a constant factor of the minimum;
    /// a one-character step turns a 200 KB source into ~200,000 retained chunks and the batched
    /// embedding spend that goes with them.
    /// </summary>
    [Fact]
    public void ResolveChunkStep_OverlapAtOrAboveChunkSize_KeepsTheEmittedChunkCountBounded()
    {

        const int chunkSizeChars = 128;

        const int documentChars = 200_000;

        int step = WeaveService.ResolveChunkStep(chunkSizeChars, 1_024);

        int emitted = ((documentChars - 1) / step) + 1;

        int minimum = ((documentChars - 1) / chunkSizeChars) + 1;

        Assert.True(emitted <= minimum * 2, $"{emitted} chunks emitted for a {minimum}-chunk document.");

    }

    /// <summary>
    /// An overlap the window can actually carry is honoured exactly — the relative bound must not
    /// silently shrink an ordinary configuration.
    /// </summary>
    [Theory]
    [InlineData(1_000, 100, 900)]
    [InlineData(128, 0, 128)]
    [InlineData(8_192, 1_024, 7_168)]
    [InlineData(2_048, 1_024, 1_024)]
    public void ResolveChunkStep_OverlapWithinHalfTheChunkSize_IsHonouredExactly(
        int chunkSizeChars,
        int chunkOverlapChars,
        int expectedStep)
    {

        Assert.Equal(expectedStep, WeaveService.ResolveChunkStep(chunkSizeChars, chunkOverlapChars));

    }

    private static ArcanumSettings EnabledSettings() =>
        new()
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "local",
                    Model = "nomic-embed-text",
                },
            },
        };

    private static WeaveService CreateService(
        ArcanumSettings settings,
        FakeEmbeddingGeneratorFactory? factory = null,
        ITurnRunWriter? writer = null,
        IBudgetReservationService? reservations = null)
    {
        ServiceCollection collection = new();

        if (writer is not null)
        {
            collection.AddSingleton(writer);
        }

        if (reservations is not null)
        {
            collection.AddSingleton(reservations);
        }

        ServiceProvider services = collection.BuildServiceProvider();

        return new(
            factory ?? new FakeEmbeddingGeneratorFactory(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WeaveService>.Instance);
    }

    private sealed class RecordingTurnRunWriter : ITurnRunWriter
    {
        public List<BillableOperationRecord> Operations { get; } = [];

        public Task<Guid> StartRunAsync(
            InferenceRunStart start,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());

        public Task CompleteRunAsync(
            Guid runId,
            InferenceRunStatus status,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Guid> RecordBillableOperationAsync(
            BillableOperationRecord operation,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class RecordingBudgetReservationService : IBudgetReservationService
    {
        public BudgetReservationRequest? LastRequest { get; private set; }

        public decimal? ReconciledUsd { get; private set; }

        public int ReconcileCount { get; private set; }

        public Task<Result<BudgetReservation>> ReserveAsync(
            BudgetReservationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Result<BudgetReservation>.Success(new BudgetReservation(
                Guid.NewGuid(),
                request.RunId,
                request.BudgetPeriod,
                request.ReservedUsd,
                0m,
                BudgetReservationStatus.Reserved,
                request.ExpiresAt,
                DateTimeOffset.UtcNow)));
        }

        public Task ReconcileAsync(
            Guid reservationId,
            decimal actualCostUsd,
            CancellationToken cancellationToken = default)
        {
            ReconciledUsd = actualCostUsd;
            ReconcileCount++;
            return Task.CompletedTask;
        }

        public Task<Result> AdjustAsync(
            Guid reservationId,
            decimal reservedUsd,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<int> SweepExpiredAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class FakeEmbeddingGeneratorFactory : IEmbeddingGeneratorFactory
    {

        public FakeEmbeddingGenerator Generator { get; } = new();

        public int ResolveCount { get; private set; }

        public CancellationToken ResolveCancellationToken { get; private set; }

        public Task<EmbeddingGeneratorLease> ResolveGeneratorAsync(CancellationToken cancellationToken)
        {

            ResolveCount++;

            ResolveCancellationToken = cancellationToken;

            return Task.FromResult(new EmbeddingGeneratorLease(Generator, ownsGenerator: false));

        }

    }

    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {

        public List<int> CallSizes { get; } = [];

        public Exception? ThrowOnGenerate { get; set; }

        public TimeSpan? DelayOnGenerate { get; set; }

        public Action? BeforeGenerate { get; set; }

        public int? ThrowOnCallNumber { get; set; }

        public bool ReturnNoVectors { get; set; }

        public CancellationToken GenerateCancellationToken { get; private set; }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {

            GenerateCancellationToken = cancellationToken;

            List<string> list = [.. values];

            CallSizes.Add(list.Count);

            BeforeGenerate?.Invoke();

            if (DelayOnGenerate is { } delay)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            }

            if (ThrowOnGenerate is not null || ThrowOnCallNumber == CallSizes.Count)
            {
                throw ThrowOnGenerate ?? new InvalidOperationException("scripted embedding failure");

            }

            if (ReturnNoVectors)
            {
                return new GeneratedEmbeddings<Embedding<float>>();

            }

            GeneratedEmbeddings<Embedding<float>> result = new(list.Count);

            foreach (string _ in list)
            {
                result.Add(new Embedding<float>(new float[] { 1f, 0f, 0f }));

            }

            return result;

        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

    }

}
