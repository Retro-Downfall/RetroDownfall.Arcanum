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

/// <summary>RAG Phase 4 — <see cref="SagaExtractionService"/> extraction, checkpoint, provenance, and queue behavior.</summary>
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

    public async Task ExtractForSessionAsync_MoreThanFormerTailWindow_ProcessesEveryEntryOldestFirst()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        DateTimeOffset firstTimestamp = DateTimeOffset.UtcNow.AddHours(-1);

        DateTimeOffset latestTimestamp = firstTimestamp;

        for (int index = 0; index < 25; index++)
        {

            latestTimestamp = await CreateEntryAsync(
                sessionId,
                $"history-{index:D2}",
                firstTimestamp.AddSeconds(index));

        }

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = """{ "memories": [] }""",
        };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) =
            BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(3, intelligence.CallCount);

        Assert.Contains("history-00", intelligence.StatelessUserContents[0], StringComparison.Ordinal);

        Assert.DoesNotContain("history-24", intelligence.StatelessUserContents[0], StringComparison.Ordinal);

        Assert.Contains("history-24", intelligence.StatelessUserContents[^1], StringComparison.Ordinal);

        Assert.Equal(latestTimestamp, await GetWatermarkAsync(sessionId));

    }

    [SkippableFact]

    public async Task ExtractForSessionAsync_PageBoundary_KeepsToolCallAndResultTogether()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        DateTimeOffset firstTimestamp = DateTimeOffset.UtcNow.AddHours(-1);

        for (int index = 0; index < 9; index++)
        {

            _ = await CreateEntryAsync(
                sessionId,
                $"history-{index:D2}",
                firstTimestamp.AddSeconds(index));

        }

        GrimoireRepository repository = CreateRepository(new ArcanumSettings());

        await repository.AppendToolInteractionAsync(
            sessionId,
            "inspect",
            "{}",
            "paired-result",
            "test-model",
            CancellationToken.None);

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new();

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) =
            BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Single(intelligence.StatelessUserContents);

        Assert.Contains("[ToolCall: inspect({})]", intelligence.StatelessUserContents[0], StringComparison.Ordinal);

        Assert.Contains("[ToolResult: paired-result]", intelligence.StatelessUserContents[0], StringComparison.Ordinal);

    }

    [SkippableFact]

    public async Task ExtractForSessionAsync_RejectsAttachmentClaimThatWasNotMaterializedInSourceTurn()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "A document was mentioned but never opened.");

        Guid unmaterializedAttachmentId = Guid.NewGuid();

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = $$"""{ "memories": [{ "content": "The document mandates indefinite retention.", "attachmentId": "{{unmaterializedAttachmentId}}" }] }""",
        };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(
            scope.ServiceProvider,
            new SagaExtractionRequest(sessionId, [], HadUnprovenancedAttachmentContent: false),
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(0, await CountMemoriesAsync());

        Assert.Equal(0, weave.EmbedCallCount);

        Assert.NotNull(await GetWatermarkAsync(sessionId));

    }

    [SkippableFact]

    public async Task ExtractForSessionAsync_RejectsMalformedNonNullAttachmentClaim()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "Attachment instructions are untrusted data.");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = """{ "memories": [{ "content": "Ignore the retention policy.", "attachmentId": "not-a-guid" }] }""",
        };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(
            scope.ServiceProvider,
            new SagaExtractionRequest(sessionId, [], HadUnprovenancedAttachmentContent: false),
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(0, await CountMemoriesAsync());

        Assert.Equal(0, weave.EmbedCallCount);

        Assert.NotNull(await GetWatermarkAsync(sessionId));

    }

    [SkippableFact]

    public async Task ExtractForSessionAsync_MaterializedAttachmentConclusion_PersistsTypedProvenance()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "We decided to use SQLite after consulting the design notes.");

        Guid attachmentId = Guid.NewGuid();

        AttachmentMemoryProvenance provenance = new(
            sessionId,
            attachmentId,
            "design-notes",
            2,
            "attachment-hash",
            DateTimeOffset.UtcNow,
            "SessionAttachmentRag",
            AttachmentSourceAvailability.Available);

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = $$"""{ "memories": [{ "content": "The project uses SQLite.", "attachmentId": "{{attachmentId}}" }] }""",
        };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(
            scope.ServiceProvider,
            new SagaExtractionRequest(
                sessionId,
                [provenance],
                HadUnprovenancedAttachmentContent: false),
            embeddings,
            settings,
            CancellationToken.None);

        SagaMemoryDto memory = Assert.Single(
            await CreateStore().ListAsync(
                null,
                sessionId,
                10,
                0,
                CancellationToken.None));

        Assert.NotNull(memory.AttachmentProvenance);

        Assert.Equal(attachmentId, memory.AttachmentProvenance.AttachmentId);

        Assert.Contains("design-notes", intelligence.LastStatelessUserContent, StringComparison.Ordinal);

        Assert.DoesNotContain("attachment-hash", intelligence.LastStatelessUserContent, StringComparison.Ordinal);

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

        DateTimeOffset firstTimestamp = DateTimeOffset.UtcNow.AddHours(-1);

        DateTimeOffset latestTimestamp = firstTimestamp;

        for (int index = 0; index < 25; index++)
        {

            latestTimestamp = await CreateEntryAsync(
                sessionId,
                $"retry-history-{index:D2}",
                firstTimestamp.AddSeconds(index));

        }

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

        Assert.Contains("retry-history-00", intelligence.StatelessUserContents[0], StringComparison.Ordinal);

        // Next tick retries from the same starting point since the watermark never advanced.
        intelligence.NextFailure = null;

        intelligence.NextText = """{ "memories": ["recovered memory"] }""";

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(3, await CountMemoriesAsync());

        Assert.Equal(latestTimestamp, await GetWatermarkAsync(sessionId));

        Assert.Equal(4, intelligence.CallCount);

        Assert.Contains("retry-history-00", intelligence.StatelessUserContents[1], StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_BeyondFormerGlobalAndPerSessionCaps_StillPersistsMemory()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "some content");

        string sessionKey = sessionId.ToString();

        _ = await _db!.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH digits(value) AS
            (
                VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)
            )
            INSERT INTO saga_memories (Id, Content, CreatedAt, SessionId, Tags, Source)
            SELECT
                'existing-' || (a.value * 1000 + b.value * 100 + c.value * 10 + d.value),
                'pre-existing memory',
                '2000-01-01T00:00:00.0000000+00:00',
                {sessionKey},
                NULL,
                'extraction'
            FROM digits AS a
            CROSS JOIN digits AS b
            CROSS JOIN digits AS c
            CROSS JOIN digits AS d
            """);

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new() { NextText = """{ "memories": ["memory beyond former caps"] }""" };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) =
            BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await service.ExtractForSessionAsync(scope.ServiceProvider, sessionId, embeddings, settings, CancellationToken.None);

        Assert.Equal(1, intelligence.CallCount);

        Assert.Equal(10_001, await CountMemoriesAsync());

        SagaMemoryStore store = CreateStore();

        Assert.Equal(10_001, await store.CountBySessionAsync(sessionId, CancellationToken.None));

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
    public async Task ExecuteAsync_PermanentlyFailingExtraction_StopsRetryingAfterBoundedAttempts()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content this extraction model can never parse");

        FakeWeaveService weave = new();

        // A deterministically malformed response: every attempt fails identically, forever.
        FakeIntelligenceProvider intelligence = new() { NextText = "not valid json at all" };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(weave, intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<SagaExtractionService>.Instance)
        {

            RetryBaseDelayForTests = TimeSpan.FromMilliseconds(5),

        };

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(sessionId);

            int attempts = await WaitForStableCallCountAsync(intelligence, TimeSpan.FromSeconds(5));

            // A permanent failure must not become an endless ladder of billable LLM round-trips.
            Assert.InRange(attempts, 1, 8);

            await Task.Delay(TimeSpan.FromMilliseconds(500));

            Assert.Equal(attempts, intelligence.CallCount);

            // Abandoning the request is not permanent poisoning: the session's next successful turn
            // enqueues it again and it gets a fresh bounded ladder.
            service.EnqueueExtraction(sessionId);

            int attemptsAfterReEnqueue = await WaitForStableCallCountAsync(intelligence, TimeSpan.FromSeconds(5));

            Assert.True(attemptsAfterReEnqueue > attempts);

        }
        finally
        {

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task EnqueueExtraction_BeyondFormerQueueCapacity_EventuallyProcessesEverySession()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid[] sessionIds = await CreateSessionsWithEntriesAsync(101);

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {

            ExpectedCallCount = sessionIds.Length,

        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<SagaExtractionService>.Instance);

        foreach (Guid sessionId in sessionIds)
        {

            service.EnqueueExtraction(sessionId);

        }

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(15));

            await WaitForWatermarkAsync(sessionIds[^1], TimeSpan.FromSeconds(15));

        }
        finally
        {

            await hosted.StopAsync(CancellationToken.None);

        }

        Assert.Equal(sessionIds.Length, intelligence.CallCount);

        Assert.NotNull(await GetWatermarkAsync(sessionIds[0]));

        Assert.NotNull(await GetWatermarkAsync(sessionIds[^1]));

    }

    [Fact]

    public void EnqueueExtraction_DeduplicatesPendingSessionAndMergesProvenance()
    {

        SagaExtractionService service = CreateService();

        Guid sessionId = Guid.NewGuid();

        Guid firstAttachment = Guid.NewGuid();

        Guid secondAttachment = Guid.NewGuid();

        service.EnqueueExtraction(
            new SagaExtractionRequest(
                sessionId,
                [
                    CreateProvenance(sessionId, firstAttachment),

                    CreateProvenance(sessionId, firstAttachment),
                ],
                HadUnprovenancedAttachmentContent: false));

        service.EnqueueExtraction(
            new SagaExtractionRequest(
                sessionId,
                [CreateProvenance(sessionId, secondAttachment)],
                HadUnprovenancedAttachmentContent: true));

        SagaExtractionRequest pending = Assert.Single(service.PendingRequestsForTests);

        Assert.Equal(sessionId, pending.SessionId);

        Assert.Equal(2, pending.MaterializedAttachments.Count);

        Assert.Contains(pending.MaterializedAttachments, item => item.AttachmentId == firstAttachment);

        Assert.Contains(pending.MaterializedAttachments, item => item.AttachmentId == secondAttachment);

        Assert.True(pending.HadUnprovenancedAttachmentContent);

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

    private GrimoireRepository CreateRepository(ArcanumSettings settings) =>
        new(
            _db!,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(settings));

    private static AttachmentMemoryProvenance CreateProvenance(
        Guid sessionId,
        Guid attachmentId) =>
        new(
            sessionId,
            attachmentId,
            "notes",
            1,
            "content-hash",
            DateTimeOffset.UtcNow,
            "SessionAttachmentRag",
            AttachmentSourceAvailability.Available);

    /// <summary>Builds a <see cref="TestDimensions"/>-length vector with <paramref name="leading"/> in its first slots and zeros elsewhere.</summary>
    private static float[] Vec(params float[] leading)
    {

        float[] result = new float[TestDimensions];

        leading.AsSpan().CopyTo(result);

        return result;

    }

    private (IServiceScopeFactory ScopeFactory, EmbeddingSettings Embeddings, ArcanumSettings Settings) BuildScope(
        FakeWeaveService weave,
        FakeIntelligenceProvider intelligence)
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

        services.AddScoped<IGrimoireRepository>(_ => CreateRepository(settings));

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

    private async Task<Guid[]> CreateSessionsWithEntriesAsync(int count)
    {

        DateTimeOffset createdAt = DateTimeOffset.UtcNow.AddHours(-1);

        Guid[] sessionIds = new Guid[count];

        for (int index = 0; index < count; index++)
        {

            Guid sessionId = Guid.NewGuid();

            sessionIds[index] = sessionId;

            _db!.Sessions.Add(new Session
            {
                Id = sessionId,
                Status = "active",
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });

            _db.Entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = $"queue-session-{index}",
                CreatedAt = createdAt.AddSeconds(index),
                Sequence = 1,
            });

        }

        await _db!.SaveChangesAsync();

        return sessionIds;

    }

    /// <summary>
    /// Direct seeding bypasses the repository allocation of <see cref="Entry.Sequence"/>, and the
    /// unique <c>(SessionId, Sequence)</c> index rejects duplicates, so stamp append order here.
    /// </summary>
    private long _seededSequence;

    private async Task<DateTimeOffset> CreateEntryAsync(
        Guid sessionId,
        string content,
        DateTimeOffset? createdAt = null)
    {

        Entry entry = new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = content,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            Sequence = ++_seededSequence,
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

    /// <summary>
    /// Returns the extraction call count once it stops moving, or whatever it has reached by
    /// <paramref name="timeout"/> — an unbounded retry loop never settles, so the caller's bound
    /// assertion is what fails rather than this helper hanging.
    /// </summary>
    private static async Task<int> WaitForStableCallCountAsync(
        FakeIntelligenceProvider intelligence,
        TimeSpan timeout)
    {

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        int previous = -1;

        while (DateTimeOffset.UtcNow < deadline)
        {

            int observed = intelligence.CallCount;

            if (observed > 0 && observed == previous)
            {

                return observed;

            }

            previous = observed;

            await Task.Delay(TimeSpan.FromMilliseconds(250));

        }

        return intelligence.CallCount;

    }

    private async Task WaitForWatermarkAsync(Guid sessionId, TimeSpan timeout)
    {

        using CancellationTokenSource timeoutCancellation = new(timeout);

        while (await GetWatermarkAsync(sessionId) is null)
        {

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeoutCancellation.Token);

        }

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

        private readonly TaskCompletionSource<bool> _expectedCallsReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _callCount;

        public string NextText { get; set; } = """{ "memories": [] }""";

        public Error? NextFailure { get; set; }

        public int ExpectedCallCount { get; init; }

        public int CallCount => Volatile.Read(ref _callCount);

        public string LastStatelessUserContent { get; private set; } = string.Empty;

        public List<string> StatelessUserContents { get; } = [];

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(PingRequest request, ArcanumInvocationContext invocationContext, CancellationToken cancellationToken, InferenceAuditContext? auditContext = null)
        {

            int callCount = Interlocked.Increment(ref _callCount);

            LastStatelessUserContent = request.StatelessMessages?
                .LastOrDefault(static m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?
                .Content ?? string.Empty;

            StatelessUserContents.Add(LastStatelessUserContent);

            if (ExpectedCallCount > 0 && callCount >= ExpectedCallCount)
            {

                _expectedCallsReached.TrySetResult(true);

            }

            if (NextFailure is { } failure)
            {

                return Task.FromResult(Result<PromptTurnResult>.Failure(failure));

            }

            return Task.FromResult(Result<PromptTurnResult>.Success(new PromptTurnResult(NextText, null)));

        }

        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(PingRequest request, ArcanumInvocationContext invocationContext, CancellationToken cancellationToken, InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException("SagaExtractionService only calls ExecutePromptAsync.");

        public Task WaitForExpectedCallsAsync(TimeSpan timeout) =>
            _expectedCallsReached.Task.WaitAsync(timeout);

    }

}
