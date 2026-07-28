using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>RAG Phase 4 — <see cref="SagaExtractionService"/> extraction logic, caps, watermark tracking, and queue behavior.</summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SagaExtractionServiceTests : IAsyncLifetime
{

    /// <summary>
    /// Matches <see cref="ArcanumSettingClamps.EmbeddingsDimensions"/>'s 64-dimension floor and
    /// <see cref="FakeWeaveService.EmbedAsync"/>'s vector length, so SagaMemoryStore's
    /// dimension-validation guard (see InsertAsync) does not reject test inserts.
    /// </summary>
    private const int TestDimensions = 64;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public SagaExtractionServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_NoEntries_SkipsWithoutCallingIntelligenceOrWeave()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new();

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(0, intelligence.CallCount);

        Assert.Equal(0, weave.EmbedCallCount);

        Assert.Null(await GetWatermarkAsync(sessionId));

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_WeaveUnavailable_Skips()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "hello there");

        FakeWeaveService weave = new() { Available = false };

        FakeIntelligenceProvider intelligence = new();

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(0, intelligence.CallCount);

        Assert.Equal(0, await CountMemoriesAsync());

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_ValidMemories_InsertsAndAdvancesWatermark()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "I like dark mode.");

        DateTimeOffset latestEntryCreatedAt = await CreateEntryAsync(sessionId, "I use xUnit for tests.");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = """{ "memories": ["The operator prefers dark mode.", "The operator uses xUnit."] }""",
        };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(1, intelligence.CallCount);

        Assert.Equal(2, await CountMemoriesAsync());

        Assert.Equal(2, weave.EmbedCallCount);

        DateTimeOffset? watermark = await GetWatermarkAsync(sessionId);

        Assert.NotNull(watermark);

        Assert.Equal(latestEntryCreatedAt, watermark!.Value, TimeSpan.FromSeconds(1));

        // Prompt sent to the extraction LLM includes the raw entry content.
        Assert.Contains("I like dark mode.", intelligence.LastStatelessUserContent, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_MalformedJsonResponse_DoesNotAdvanceWatermark_RetriesNextTick()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "some content");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new() { NextText = "not valid json at all" };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(0, await CountMemoriesAsync());

        // A malformed response was never actually reviewed, so — unlike a legitimately empty
        // "{ memories: [] }" response — the watermark must not advance; the next enqueue retries the
        // same entry window (see ExtractForSessionAsync_LlmFailure_DoesNotAdvanceWatermark_RetriesNextTick).
        Assert.Null(await GetWatermarkAsync(sessionId));

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_EmptyMemoriesArray_NoInserts_StillAdvancesWatermark()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "nothing worth remembering here");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new() { NextText = """{ "memories": [] }""" };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(0, await CountMemoriesAsync());

        Assert.NotNull(await GetWatermarkAsync(sessionId));

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_AllEmbedsFail_DoesNotAdvanceWatermark_RetriesNextTick()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "some content");

        FakeWeaveService weave = new() { EmbedShouldFail = true };

        FakeIntelligenceProvider intelligence = new() { NextText = """{ "memories": ["a memory that cannot be embedded"] }""" };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(0, await CountMemoriesAsync());

        // The LLM parsed a real memory, but embedding it failed for every candidate (e.g. embedding
        // provider outage) — like a parse failure, nothing was actually persisted, so the watermark
        // must not advance and the next enqueue retries the same entry window.
        Assert.Null(await GetWatermarkAsync(sessionId));

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_NoNewEntriesBeyondWatermark_Skips()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        DateTimeOffset entryCreatedAt = await CreateEntryAsync(sessionId, "already covered content");

        SagaMemoryStore store = CreateStore();

        await store.SetWatermarkAsync(sessionId, entryCreatedAt, CancellationToken.None);

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new() { NextText = """{ "memories": ["should not be extracted"] }""" };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(0, intelligence.CallCount);

        Assert.Equal(0, await CountMemoriesAsync());

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_LlmFailure_DoesNotAdvanceWatermark_RetriesNextTick()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "some content");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            NextFailure = new Error(ErrorCodes.Hub.Error, "Simulated LLM failure."),
        };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(0, await CountMemoriesAsync());

        Assert.Null(await GetWatermarkAsync(sessionId));

        // Next tick retries from the same starting point since the watermark never advanced.
        intelligence.NextFailure = null;

        intelligence.NextText = """{ "memories": ["recovered memory"] }""";

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(1, await CountMemoriesAsync());

        Assert.NotNull(await GetWatermarkAsync(sessionId));

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_TotalMemoryCapReached_SkipsExtraction()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "some content");

        // ArcanumSettingClamps.EmbeddingsSagaMaxMemoriesTotal clamps to a 100-1,000,000 floor, so the
        // cap under test (100, the minimum valid value) requires exactly that many pre-existing rows.
        const int cap = 100;

        SagaMemoryStore store = CreateStore();

        for (int i = 0; i < cap; i++)
        {

            await store.InsertAsync($"existing-{i}", $"pre-existing memory {i}", DateTimeOffset.UtcNow, null, null, "extraction", Vec(1f), CancellationToken.None);

        }

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new() { NextText = """{ "memories": ["should not be extracted"] }""" };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence,
            maxMemoriesTotal: cap);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(0, intelligence.CallCount);

        Assert.Equal(cap, await CountMemoriesAsync());

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_PerSessionMemoryCapReached_SkipsExtraction()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "some content");

        SagaMemoryStore store = CreateStore();

        await store.InsertAsync("existing-1", "pre-existing memory", DateTimeOffset.UtcNow, sessionId, null, "extraction", Vec(1f), CancellationToken.None);

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new() { NextText = """{ "memories": ["should not be extracted"] }""" };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence,
            maxMemoriesPerSession: 1);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(0, intelligence.CallCount);

        Assert.Equal(1, await CountMemoriesAsync());

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_PerSessionCapReachedMidBatch_StopsInsertingWithoutExceedingCap()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "some content");

        SagaMemoryStore store = CreateStore();

        // One pre-existing memory, cap of 2: only one more may be inserted, even though the LLM
        // response below parses three candidate memories in a single extraction call.
        await store.InsertAsync("existing-1", "pre-existing memory", DateTimeOffset.UtcNow, sessionId, null, "extraction", Vec(1f), CancellationToken.None);

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = """{ "memories": ["first new memory", "second new memory", "third new memory"] }""",
        };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence,
            maxMemoriesPerSession: 2);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        // The cap (2) is re-checked before every insert in the batch, not just once before the LLM
        // call, so the per-session cap is never exceeded even when a single extraction call parses
        // more candidate memories than remaining headroom.
        Assert.Equal(2, await CountMemoriesAsync());

    }

    [SkippableFact]
    public async Task ExecuteAsync_IdlesWhenDisabled_NeverCallsIntelligence()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "should not be extracted while disabled");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new() { NextText = """{ "memories": ["nope"] }""" };

        ArcanumSettings disabledSettings = new()
        {
            Features = new FeatureSettings { Embeddings = false, Saga = false },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Dimensions = TestDimensions,
                },
            },
        };

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        services.AddSingleton<IWeaveService>(weave);

        services.AddSingleton<IArcanumIntelligenceProvider>(intelligence);

        services.AddSingleton<ISagaMemoryStore, SagaMemoryStore>();

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(disabledSettings));

        services.AddSingleton(new WeaveIndexAvailability());

        services.AddScoped<IGrimoireRepository>(sp => new GrimoireRepository(
            sp.GetRequiredService<ArcanumDbContext>(),
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(disabledSettings)));

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(disabledSettings),
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        service.EnqueueExtraction(sessionId);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(0, intelligence.CallCount);

        Assert.Equal(0, await CountMemoriesAsync());

    }

    [SkippableFact]
    public async Task EnqueueExtraction_ManyRapidCalls_NeverThrows_DropsOldestWhenFull()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SagaExtractionService service = CreateService();

        // No consumer is running (ExecuteAsync was never started), so the bounded channel (capacity
        // 100, DropOldest) fills up quickly; EnqueueExtraction must never throw regardless.
        for (int i = 0; i < 250; i++)
        {

            service.EnqueueExtraction(Guid.NewGuid());

        }

    }

    private SagaExtractionService CreateService() =>
        new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            NullLogger<SagaExtractionService>.Instance);

    private SagaMemoryStore CreateStore() =>
        new(
            _db!,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings
                        {
                            Dimensions = TestDimensions,
                        },
                    },
                }));

    /// <summary>Builds a <see cref="TestDimensions"/>-length vector with <paramref name="leading"/> in its first slots and zeros elsewhere.</summary>
    private static float[] Vec(params float[] leading)
    {

        float[] result = new float[TestDimensions];

        leading.AsSpan().CopyTo(result);

        return result;

    }

    private (IServiceScopeFactory ScopeFactory, EmbeddingSettings Embeddings, ArcanumSettings Settings) BuildScope(
        FakeWeaveService weave,
        FakeIntelligenceProvider intelligence,
        int maxMemoriesTotal = 10_000,
        int maxMemoriesPerSession = 50)
    {

        EmbeddingSettings embeddings = ArcanumRuntimeDefaults.Embeddings with
        {
            Enabled = true,
            SagaEnabled = true,
            Provider = "test",
            Model = "test-embed",
            Dimensions = TestDimensions,
            Saga = ArcanumRuntimeDefaults.Embeddings.Saga with
            {
                ExtractionEnabled = true,
                MaxMemoriesTotal = maxMemoriesTotal,
                MaxMemoriesPerSession = maxMemoriesPerSession,
                ExtractionWindowEntries = 10,
            },
        };

        ArcanumSettings settings = new()
        {
            FastModel = "fast-test-model",
            Features = new FeatureSettings
            {
                Embeddings = true,
                Saga = true,
                SagaExtraction = true,
            },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "test",
                    Model = "test-embed",
                    Dimensions = TestDimensions,
                },
            },
        };

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        services.AddSingleton<IWeaveService>(weave);

        services.AddSingleton<IArcanumIntelligenceProvider>(intelligence);

        services.AddSingleton<ISagaMemoryStore, SagaMemoryStore>();

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(settings));

        services.AddSingleton(new WeaveIndexAvailability());

        services.AddScoped<IGrimoireRepository>(sp => new GrimoireRepository(
            sp.GetRequiredService<ArcanumDbContext>(),
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(settings)));

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return (scopeFactory, embeddings, settings);

    }

    private async Task<Guid> CreateSessionAsync()
    {

        Session session = new()
        {
            Id = Guid.NewGuid(),
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db!.Sessions.Add(session);

        await _db.SaveChangesAsync();

        return session.Id;

    }

    private async Task<DateTimeOffset> CreateEntryAsync(Guid sessionId, string content)
    {

        Entry entry = new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db!.Entries.Add(entry);

        await _db.SaveChangesAsync();

        return entry.CreatedAt;

    }

    private async Task<int> CountMemoriesAsync()
    {

        SagaMemoryStore store = CreateStore();

        return await store.CountAsync(CancellationToken.None);

    }

    private async Task<DateTimeOffset?> GetWatermarkAsync(Guid sessionId)
    {

        SagaMemoryStore store = CreateStore();

        return await store.GetWatermarkAsync(sessionId, CancellationToken.None);

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public bool Available { get; set; } = true;

        public bool EmbedShouldFail { get; set; }

        public int EmbedCallCount { get; private set; }

        public bool IsAvailable => Available;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {

            EmbedCallCount++;

            if (EmbedShouldFail)
            {

                return Task.FromResult(Result<Embedding<float>>.Failure(new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated embedding failure.")));

            }

            return Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(Vec(1f))));

        }

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("SagaExtractionService only calls EmbedAsync per memory.");

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SagaExtractionService.");

    }

    private sealed class FakeIntelligenceProvider : IArcanumIntelligenceProvider
    {

        public string NextText { get; set; } = """{ "memories": [] }""";

        public Error? NextFailure { get; set; }

        public int CallCount { get; private set; }

        public string LastStatelessUserContent { get; private set; } = string.Empty;

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(PingRequest request, CancellationToken cancellationToken = default, InferenceAuditContext? auditContext = null)
        {

            CallCount++;

            LastStatelessUserContent = request.StatelessMessages?
                .LastOrDefault(static m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?
                .Content ?? string.Empty;

            if (NextFailure is { } failure)
            {

                return Task.FromResult(Result<PromptTurnResult>.Failure(failure));

            }

            return Task.FromResult(Result<PromptTurnResult>.Success(new PromptTurnResult(NextText, null)));

        }

        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(PingRequest request, CancellationToken cancellationToken = default, InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException("SagaExtractionService only calls ExecutePromptAsync.");

    }

}
