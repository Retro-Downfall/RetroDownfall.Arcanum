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
            Embeddings = new EmbeddingSettings { Enabled = false },
        });

        Assert.False(service.IsAvailable);

    }

    [Fact]
    public void IsAvailable_EnabledWithoutProvider_ReturnsFalse()
    {

        WeaveService service = CreateService(new ArcanumSettings
        {
            Embeddings = new EmbeddingSettings { Enabled = true, Model = "nomic-embed-text" },
        });

        Assert.False(service.IsAvailable);

    }

    [Fact]
    public void IsAvailable_EnabledWithoutModel_ReturnsFalse()
    {

        WeaveService service = CreateService(new ArcanumSettings
        {
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "local" },
        });

        Assert.False(service.IsAvailable);

    }

    [Fact]
    public void IsAvailable_EnabledWithProviderAndModel_ReturnsTrue()
    {

        WeaveService service = CreateService(new ArcanumSettings
        {
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "local", Model = "nomic-embed-text" },
        });

        Assert.True(service.IsAvailable);

    }

    [Fact]
    public async Task EmbedAsync_Disabled_ReturnsFeatureDisabled_WithoutResolvingGenerator()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        WeaveService service = CreateService(
            new ArcanumSettings { Embeddings = new EmbeddingSettings { Enabled = false } },
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
            new ArcanumSettings { Embeddings = new EmbeddingSettings { Enabled = false } },
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
    public async Task EmbedBatchAsync_SplitsIntoConfiguredBatchSize()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        ArcanumSettings settings = EnabledSettings(batchSize: 2);

        WeaveService service = CreateService(settings, factory);

        Result<Embedding<float>[]> result = await service.EmbedBatchAsync(
            ["a", "b", "c", "d", "e"],
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(5, result.Value.Length);

        // 5 texts at batch size 2 -> batches of [2, 2, 1] -> 3 generator resolutions/calls.
        Assert.Equal(3, factory.ResolveCount);

        Assert.Equal([2, 2, 1], factory.Generator.CallSizes);

    }

    [Fact]
    public async Task EmbedBatchAsync_ReservesSanitizedInputBeforeProviderAndLedgersEachBatch()
    {
        FakeEmbeddingGeneratorFactory factory = new();
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new();
        ArcanumSettings settings = EnabledSettings(batchSize: 2) with
        {
            Embeddings = EnabledSettings(batchSize: 2).Embeddings! with { ChunkSizeChars = 128 },
            Pricing = new PricingSettings
            {
                DefaultPricing = new ModelPricingEntry { InputPer1M = 1_000_000m },
            },
        };
        factory.Generator.BeforeGenerate = () => Assert.NotNull(reservations.LastRequest);
        WeaveService service = CreateService(settings, factory, writer, reservations);

        Result<Embedding<float>[]> result = await service.EmbedBatchAsync(
            [new string('a', 300), "b", "c"],
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(34m, reservations.LastRequest?.ReservedUsd);
        Assert.Collection(
            writer.Operations,
            first => Assert.Equal(33L, first.InputTokens),
            second => Assert.Equal(1L, second.InputTokens));
        Assert.Equal(1, reservations.ReconcileCount);
        Assert.Equal(34m, reservations.ReconciledUsd);
    }

    [Fact]
    public async Task EmbedBatchAsync_LaterProviderFailureRetainsEarlierBatchSpend()
    {
        FakeEmbeddingGeneratorFactory factory = new();
        factory.Generator.ThrowOnCallNumber = 2;
        RecordingTurnRunWriter writer = new();
        RecordingBudgetReservationService reservations = new();
        ArcanumSettings settings = EnabledSettings(batchSize: 2) with
        {
            Pricing = new PricingSettings
            {
                DefaultPricing = new ModelPricingEntry { InputPer1M = 1_000_000m },
            },
        };
        WeaveService service = CreateService(settings, factory, writer, reservations);

        Result<Embedding<float>[]> result = await service.EmbedBatchAsync(
            ["a", "b", "c"],
            CancellationToken.None);

        Assert.True(result.IsFailure);
        BillableOperationRecord operation = Assert.Single(writer.Operations);
        Assert.Equal(2L, operation.InputTokens);
        Assert.Equal(1, reservations.ReconcileCount);
        Assert.Equal(operation.ActualCostUsd, reservations.ReconciledUsd);
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
    public async Task EmbedAsync_ProviderTimesOut_ReturnsProviderUnavailable_NeverThrows()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        // Generously longer than the clamped-minimum 5s request timeout below, so the internal
        // timeout deterministically fires well before this fake delay could complete.
        factory.Generator.DelayOnGenerate = TimeSpan.FromSeconds(30);

        ArcanumSettings settings = EnabledSettings(requestTimeoutSeconds: 5);

        WeaveService service = CreateService(settings, factory);

        Result<Embedding<float>> result = await service.EmbedAsync("hello", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Embeddings.ProviderUnavailable, result.Error.Code);

    }

    [Fact]
    public async Task EmbedAsync_CallerCancellation_PropagatesAsOperationCanceled()
    {

        FakeEmbeddingGeneratorFactory factory = new();

        factory.Generator.DelayOnGenerate = TimeSpan.FromSeconds(30);

        WeaveService service = CreateService(EnabledSettings(requestTimeoutSeconds: 300), factory);

        using CancellationTokenSource cts = new();

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TaskCanceledException>(() => service.EmbedAsync("hello", cts.Token));

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
            Embeddings = new EmbeddingSettings { Enabled = false },
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

        // Values respect ArcanumSettingClamps.EmbeddingsChunkSizeChars (128-8192) /
        // EmbeddingsChunkOverlapChars (0-1024) — values below the clamp floor would silently
        // clamp up and change the expected offsets.
        WeaveService service = CreateService(new ArcanumSettings
        {
            Embeddings = new EmbeddingSettings { ChunkSizeChars = 200, ChunkOverlapChars = 40 },
        });

        string text = new('a', 500);

        Result<(string Chunk, int Offset)[]> result = await service.ChunkAsync(text, CancellationToken.None);

        Assert.True(result.IsSuccess);

        (string Chunk, int Offset)[] chunks = result.Value;

        // step = 200 - 40 = 160; offsets 0, 160, 320 (last chunk shorter, covers to the end).
        Assert.Equal([0, 160, 320], chunks.Select(c => c.Offset).ToArray());

        Assert.All(chunks, c => Assert.True(c.Chunk.Length <= 200));

        // Every character of the source text is covered by the final chunk's reach.
        (string Chunk, int Offset) last = chunks[^1];

        Assert.Equal(text.Length, last.Offset + last.Chunk.Length);

    }

    private static ArcanumSettings EnabledSettings(int? batchSize = null, int? requestTimeoutSeconds = null) =>
        new()
        {
            Embeddings = new EmbeddingSettings
            {
                Enabled = true,
                Provider = "local",
                Model = "nomic-embed-text",
                BatchSize = batchSize ?? new EmbeddingSettings().BatchSize,
                RequestTimeoutSeconds = requestTimeoutSeconds ?? new EmbeddingSettings().RequestTimeoutSeconds,
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

        public Task<EmbeddingGeneratorLease> ResolveGeneratorAsync(CancellationToken cancellationToken)
        {

            ResolveCount++;

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

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {

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
