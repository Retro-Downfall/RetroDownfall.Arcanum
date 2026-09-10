using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
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

    private readonly GrimoireConnectionAdmissionGate _admissionGate = OpenGate();

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

        SagaExtractionOutcome outcome = await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(SagaExtractionOutcome.Completed, outcome);

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

        SagaExtractionOutcome outcome = await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(SagaExtractionOutcome.Retry, outcome);

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
            NextText = """{ "memories": [{ "content": "The operator prefers dark mode.", "attachmentId": null }, { "content": "The operator uses xUnit.", "attachmentId": null }] }""",
        };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        SagaExtractionOutcome outcome = await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(SagaExtractionOutcome.Completed, outcome);

        Assert.Equal(1, intelligence.CallCount);

        Assert.Equal(2, await CountMemoriesAsync());

        Assert.Equal(2, weave.EmbedCallCount);

        DateTimeOffset? watermark = await GetWatermarkAsync(sessionId);

        Assert.NotNull(watermark);

        Assert.Equal(latestEntryCreatedAt, watermark!.Value, TimeSpan.FromSeconds(1));

        // Prompt sent to the extraction LLM includes the raw entry content.
        Assert.Contains("I like dark mode.", intelligence.LastStatelessUserContent, StringComparison.Ordinal);

    }

    /// <summary>
    /// The real chokepoint proof: not a direct call to <c>InsertAsync</c>, but the extraction service
    /// itself re-reviewing the entries that produced a memory the operator already retired. Nothing
    /// stands between the two calls to <see cref="SagaExtractionService.ExtractForSessionAsync(IServiceProvider, IGrimoireWorkLease, SagaExtractionRequest, EmbeddingSettings, ArcanumSettings, CancellationToken)"/>
    /// but production code -- the store's own insert transaction is what has to refuse the second write.
    /// </summary>
    [SkippableFact]
    public async Task Extraction_does_not_write_a_memory_the_operator_retired_and_still_advances_the_watermark()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string Conclusion = "The operator prefers dark mode.";

        Guid sessionId = await CreateSessionAsync();

        DateTimeOffset latestEntryCreatedAt = await CreateEntryAsync(sessionId, "I like dark mode.");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = """{ "memories": [{ "content": "The operator prefers dark mode.", "attachmentId": null }] }""",
        };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using (AsyncServiceScope firstScope = scopeFactory.CreateAsyncScope())
        {

            await ExtractWithLeaseAsync(
                service,
                firstScope.ServiceProvider,
                sessionId,
                embeddings,
                settings,
                CancellationToken.None);

        }

        // One call for the first pass, established before the retirement so the later "2" at the end
        // of the test means one call per pass rather than an uneven split across the two.
        Assert.Equal(1, intelligence.CallCount);

        Assert.Equal(1, await CountMemoriesAsync());

        SagaMemoryStore store = CreateStore();

        SagaMemoryDto written = Assert.Single(
            await store.ListAsync(null, sessionId, MemoryScope.Installation, 10, 0, CancellationToken.None));

        SagaCurationOutcome retireOutcome = await store.RetireAsync(
            written.Id,
            AnnalContentDigest.ForSagaMemory(Conclusion),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(SagaCurationOutcomeKind.Applied, retireOutcome.Kind);

        // Roll the watermark back to before the source entry so the second pass reviews the same
        // entries again -- exactly what a re-extraction of that page looks like from the store's side,
        // without seeding a memory row directly.
        await store.SetWatermarkAsync(sessionId, DateTimeOffset.MinValue, CancellationToken.None);

        await using (AsyncServiceScope secondScope = scopeFactory.CreateAsyncScope())
        {

            await ExtractWithLeaseAsync(
                service,
                secondScope.ServiceProvider,
                sessionId,
                embeddings,
                settings,
                CancellationToken.None);

        }

        // The stub was actually invoked a second time over the same entries.
        Assert.Equal(2, intelligence.CallCount);

        // The row did not come back -- asserted on the store's own count, not a helper's return value.
        Assert.Equal(1, await CountMemoriesAsync());

        // A deliberate rejection is not a failure: the watermark still advanced past the suppressed
        // page, to the same place the happy path advances it to -- not merely away from the sentinel
        // this test parked it on to force the replay.
        DateTimeOffset? watermark = await GetWatermarkAsync(sessionId);

        Assert.NotNull(watermark);

        Assert.Equal(latestEntryCreatedAt, watermark!.Value, TimeSpan.FromSeconds(1));

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

        await ExtractWithLeaseAsync(
            service,
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

        await ExtractWithLeaseAsync(
            service,
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

        await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            new SagaExtractionRequest(
                sessionId,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 0,
                ThroughEntrySequence: _seededSequence),
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(0, await CountMemoriesAsync());

        Assert.Equal(0, weave.EmbedCallCount);

        Assert.NotNull(await GetWatermarkAsync(sessionId));

    }

    [SkippableFact]

    public async Task ExtractForSessionAsync_MalformedNonNullAttachmentClaim_RetriesWithoutAdvancingCursor()
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

        SagaExtractionOutcome outcome = await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            new SagaExtractionRequest(
                sessionId,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 0,
                ThroughEntrySequence: _seededSequence),
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(SagaExtractionOutcome.Retry, outcome);

        Assert.Equal(0, await CountMemoriesAsync());

        Assert.Equal(0, weave.EmbedCallCount);

        Assert.Null(await GetExtractionCursorAsync(sessionId));

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

        await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            new SagaExtractionRequest(
                sessionId,
                [provenance],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 0,
                ThroughEntrySequence: _seededSequence),
            embeddings,
            settings,
            CancellationToken.None);

        SagaMemoryDto memory = Assert.Single(
            await CreateStore().ListAsync(
                null,
                sessionId,
                MemoryScope.Installation,
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

        SagaExtractionOutcome outcome = await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(SagaExtractionOutcome.Retry, outcome);

        Assert.Equal(0, await CountMemoriesAsync());

        // A malformed response was never actually reviewed, so — unlike a legitimately empty
        // "{ memories: [] }" response — the watermark must not advance; the next enqueue retries the
        // same entry window (see ExtractForSessionAsync_LlmFailure_DoesNotAdvanceWatermark_RetriesNextTick).
        Assert.Null(await GetWatermarkAsync(sessionId));

    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ \"memories\": null }")]
    [InlineData("{ \"memories\": [42] }")]
    [InlineData("{ \"memories\": [\"legacy string\"] }")]
    [InlineData("{ \"memories\": [{ \"content\": \"missing disposition\" }] }")]
    public async Task ExtractForSessionAsync_SchemaInvalidResponse_DoesNotAdvanceCursor(
        string responseText)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content that still needs a valid extraction disposition");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new() { NextText = responseText };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        SagaExtractionOutcome outcome = await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(SagaExtractionOutcome.Retry, outcome);

        Assert.Equal(0, weave.EmbedCallCount);

        Assert.Equal(0, await CountMemoriesAsync());

        Assert.Null(await GetExtractionCursorAsync(sessionId));

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

        SagaExtractionOutcome outcome = await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(SagaExtractionOutcome.Completed, outcome);

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

        FakeIntelligenceProvider intelligence = new() { NextText = """{ "memories": [{ "content": "a memory that cannot be embedded", "attachmentId": null }] }""" };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        SagaExtractionOutcome outcome = await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(SagaExtractionOutcome.Retry, outcome);

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

        FakeIntelligenceProvider intelligence = new() { NextText = """{ "memories": [{ "content": "should not be extracted", "attachmentId": null }] }""" };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) = BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        SagaExtractionOutcome outcome = await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(SagaExtractionOutcome.Completed, outcome);

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

        SagaExtractionOutcome firstOutcome = await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(SagaExtractionOutcome.Retry, firstOutcome);

        Assert.Equal(0, await CountMemoriesAsync());

        Assert.Null(await GetWatermarkAsync(sessionId));

        Assert.Contains("retry-history-00", intelligence.StatelessUserContents[0], StringComparison.Ordinal);

        // Next tick retries from the same starting point since the watermark never advanced.
        intelligence.NextFailure = null;

        intelligence.NextText = """{ "memories": [{ "content": "recovered memory", "attachmentId": null }] }""";

        SagaExtractionOutcome resumedOutcome = await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

        Assert.Equal(SagaExtractionOutcome.Completed, resumedOutcome);

        Assert.Equal(3, await CountMemoriesAsync());

        Assert.Equal(latestTimestamp, await GetWatermarkAsync(sessionId));

        Assert.Equal(4, intelligence.CallCount);

        Assert.Contains("retry-history-00", intelligence.StatelessUserContents[1], StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task ExtractForSessionAsync_HostCancellation_PropagatesInsteadOfReturningRetry()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "provider call cancelled by host shutdown");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            Entered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            Gate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) =
            BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        using CancellationTokenSource hostStopping = new();

        Task<SagaExtractionOutcome> extraction = ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            hostStopping.Token);

        await intelligence.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await hostStopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await extraction);

        Assert.Equal(1, intelligence.CallCount);

        Assert.Equal(0, weave.EmbedCallCount);

        Assert.Null(await GetWatermarkAsync(sessionId));

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

        FakeIntelligenceProvider intelligence = new() { NextText = """{ "memories": [{ "content": "memory beyond former caps", "attachmentId": null }] }""" };

        SagaExtractionService service = CreateService();

        (IServiceScopeFactory scopeFactory, EmbeddingSettings embeddings, ArcanumSettings settings) =
            BuildScope(weave, intelligence);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await ExtractWithLeaseAsync(
            service,
            scope.ServiceProvider,
            sessionId,
            embeddings,
            settings,
            CancellationToken.None);

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

        FakeIntelligenceProvider intelligence = new() { NextText = """{ "memories": [{ "content": "nope", "attachmentId": null }] }""" };

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
            new TestOptionsSnapshot<ArcanumSettings>(disabledSettings),
            attachmentIndex: null,
            covenantKernel: null,
            FixtureOrdinaryConnectionFactory.For(sp.GetRequiredService<ArcanumDbContext>()),
            FixtureLabeledArtifactGuard.For(sp.GetRequiredService<ArcanumDbContext>())));

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(disabledSettings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        service.EnqueueExtraction(
            new SagaExtractionRequest(
                sessionId,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 0,
                ThroughEntrySequence: _seededSequence));

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(0, intelligence.CallCount);

        Assert.Equal(0, await CountMemoriesAsync());

    }

    [SkippableFact]
    public async Task ExecuteAsync_RefusedWorkLease_PreservesExactRequestAndResumesAfterReopen()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content that must wait for maintenance");

        Guid attachmentId = Guid.NewGuid();

        AttachmentMemoryProvenance provenance = CreateProvenance(sessionId, attachmentId);

        SagaExtractionRequest request = new(
            sessionId,
            [provenance],
            HadUnprovenancedAttachmentContent: true,
            AfterEntrySequenceExclusive: 0,
            ThroughEntrySequence: _seededSequence);

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 2,
        };

        (IServiceScopeFactory innerScopes, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        CountingScopeFactory scopes = new(innerScopes);

        GrimoireConnectionAdmissionGate innerGate = OpenGate();

        RecordingAdmissionGate gate = new(innerGate);

        await using IGrimoireClosingOwner closing = BeginClosing(innerGate, 81);

        SagaExtractionService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            gate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        IGrimoireExclusiveClosedLease? closed = null;

        bool reopened = false;

        try
        {

            service.EnqueueExtraction(request);

            await gate.NextOpenWaitStarted.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, gate.WorkLeaseAttempts);

            Assert.Equal(
                GrimoireWorkKind.SagaExtraction,
                Assert.Single(gate.RequestedWorkKinds));

            Assert.Equal(0, scopes.ScopesCreated);

            Assert.Equal(0, intelligence.CallCount);

            Assert.Equal(0, weave.EmbedCallCount);

            Assert.Null(await GetWatermarkAsync(sessionId));

            long firstThroughSequence = request.ThroughEntrySequence;

            _ = await CreateEntryAsync(
                sessionId,
                "later content that must retain its own attachment authority");

            long laterThroughSequence = _seededSequence;

            Guid laterAttachmentId = Guid.NewGuid();

            AttachmentMemoryProvenance laterProvenance = new(
                sessionId,
                laterAttachmentId,
                "logical-key-after-deferral",
                2,
                "hash-after-deferral",
                DateTimeOffset.UtcNow,
                "note",
                AttachmentSourceAvailability.Available);

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [laterProvenance],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: firstThroughSequence,
                    ThroughEntrySequence: laterThroughSequence));

            Assert.Collection(
                service.PendingSegmentsForTests(sessionId),
                first =>
                {

                    Assert.Equal(0, first.AfterEntrySequenceExclusive);

                    Assert.Equal(firstThroughSequence, first.ThroughEntrySequence);

                    Assert.Equal(attachmentId, Assert.Single(first.MaterializedAttachments).AttachmentId);

                    Assert.True(first.HadUnprovenancedAttachmentContent);

                },
                second =>
                {

                    Assert.Equal(firstThroughSequence, second.AfterEntrySequenceExclusive);

                    Assert.Equal(laterThroughSequence, second.ThroughEntrySequence);

                    Assert.Equal(laterAttachmentId, Assert.Single(second.MaterializedAttachments).AttachmentId);

                    Assert.False(second.HadUnprovenancedAttachmentContent);

                });

            closed = await CloseAsync(innerGate, closing);

            Assert.Equal(0, intelligence.CallCount);

            await ReopenAsync(closed);

            reopened = true;

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            await WaitForWatermarkAsync(sessionId, TimeSpan.FromSeconds(5));

            Assert.Equal(2, intelligence.CallCount);

            Assert.Contains(
                "logical-key-after-deferral",
                intelligence.LastStatelessUserContent,
                StringComparison.Ordinal);

            Assert.Equal(1, gate.NextOpenWaitCalls);

            Assert.Equal(2, gate.WorkLeaseAttempts);

            Assert.Equal(1, service.DirectResignalCountForTests);

            await using IGrimoireClosingOwner laterClosing = BeginClosing(innerGate, 87);

            IGrimoireExclusiveClosedLease laterClosed = await CloseAsync(
                innerGate,
                laterClosing);

            await ReopenAsync(laterClosed);

            await Task.Delay(TimeSpan.FromMilliseconds(100));

            Assert.Equal(1, service.DirectResignalCountForTests);

            Assert.Equal(1, gate.NextOpenWaitCalls);

            Assert.Equal(2, gate.WorkLeaseAttempts);

            Assert.Equal(2, intelligence.CallCount);

        }
        finally
        {

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));

            await hosted.StopAsync(stopTimeout.Token);

            if (!reopened && closed is not null)
            {

                await ReopenAsync(closed);

            }

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_RevocationBeforeFirstPageEffect_DefersWithoutProviderWork()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "the first page must not cross the maintenance frontier");

        SagaExtractionRequest request = new(
            sessionId,
            [CreateProvenance(sessionId, Guid.NewGuid())],
            HadUnprovenancedAttachmentContent: false,
            AfterEntrySequenceExclusive: 0,
            ThroughEntrySequence: _seededSequence);

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new();

        (IServiceScopeFactory innerScopes, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        GrimoireConnectionAdmissionGate innerGate = OpenGate();

        RecordingAdmissionGate gate = new(innerGate);

        IGrimoireClosingOwner? closing = null;

        TaskCompletionSource scopeDisposed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        CountingScopeFactory scopes = new(innerScopes)
        {
            OnScopeCreated = () => closing ??= BeginClosing(innerGate, 82),
            OnScopeDisposed = () =>
            {

                scopeDisposed.TrySetResult();

                return ValueTask.CompletedTask;

            },
        };

        SagaExtractionService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            gate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(request);

            await scopeDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await gate.NextOpenWaitStarted.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, scopes.ScopesCreated);

            Assert.Equal(1, gate.EffectGroupAttempts);

            Assert.Equal(0, gate.EffectGroupsAdmitted);

            Assert.Equal(1, gate.NextOpenWaitCalls);

            Assert.Equal(0, intelligence.CallCount);

            Assert.Equal(0, weave.EmbedCallCount);

            Assert.Equal(0, await CountMemoriesAsync());

            Assert.Null(await GetWatermarkAsync(sessionId));

            Assert.Same(request, Assert.Single(service.PendingRequestsForTests));

        }
        finally
        {

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));

            await hosted.StopAsync(stopTimeout.Token);

            if (closing is not null)
            {

                await closing.DisposeAsync();

            }

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_PageEffectWins_DrainWaitsThroughProviderWritesWatermarkAndScopeDisposal()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "first conclusion on the admitted page");

        DateTimeOffset pageWatermark = await CreateEntryAsync(
            sessionId,
            "second conclusion on the admitted page");

        GrimoireConnectionAdmissionGate innerGate = OpenGate();

        IGrimoireClosingOwner? closing = null;

        Task<Result>? drainTask = null;

        bool drainWaitedDuringProvider = false;

        bool drainWaitedDuringEveryEmbedding = true;

        bool writesWereDurableBeforeEffectDisposal = false;

        bool watermarkWasDurableBeforeEffectDisposal = false;

        bool scopeDisposedWhileLeaseHeld = false;

        bool drainWaitedThroughScopeDisposal = false;

        FakeWeaveService weave = new()
        {
            OnEmbedAsync = _ =>
            {

                drainWaitedDuringEveryEmbedding &= drainTask is { IsCompleted: false };

                return Task.CompletedTask;

            },
        };

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = """{ "memories": [{ "content": "the first durable conclusion", "attachmentId": null }, { "content": "the second durable conclusion", "attachmentId": null }] }""",
            ExpectedCallCount = 1,
            OnCall = _ =>
            {

                closing = BeginClosing(innerGate, 84);

                drainTask = innerGate.DrainRequestAndWorkAsync(
                    closing,
                    CancellationToken.None).AsTask();

                drainWaitedDuringProvider = !drainTask.IsCompleted;

            },
        };

        (IServiceScopeFactory innerScopes, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        TaskCompletionSource scopeDisposed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        RecordingAdmissionGate gate = new(innerGate)
        {
            OnEffectGroupDisposing = async () =>
            {

                writesWereDurableBeforeEffectDisposal = await CountMemoriesAsync() == 2;

                watermarkWasDurableBeforeEffectDisposal =
                    await GetWatermarkAsync(sessionId) == pageWatermark;

            },
        };

        CountingScopeFactory scopes = new(innerScopes)
        {
            OnScopeDisposed = () =>
            {

                scopeDisposedWhileLeaseHeld = gate.WorkLeaseIsHeld;

                drainWaitedThroughScopeDisposal = drainTask is { IsCompleted: false };

                scopeDisposed.TrySetResult();

                return ValueTask.CompletedTask;

            },
        };

        SagaExtractionService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            gate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        IGrimoireExclusiveClosedLease? closed = null;

        bool reopened = false;

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: _seededSequence));

            await scopeDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(drainTask);

            Result drained = await drainTask!.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

            Assert.True(drainWaitedDuringProvider);

            Assert.True(drainWaitedDuringEveryEmbedding);

            Assert.True(writesWereDurableBeforeEffectDisposal);

            Assert.True(watermarkWasDurableBeforeEffectDisposal);

            Assert.True(scopeDisposedWhileLeaseHeld);

            Assert.True(drainWaitedThroughScopeDisposal);

            Assert.Equal(1, gate.WorkLeaseAttempts);

            Assert.Equal(1, gate.EffectGroupAttempts);

            Assert.Equal(1, gate.EffectGroupsAdmitted);

            Assert.Equal(
                GrimoireWorkKind.SagaExtraction,
                Assert.Single(gate.RequestedWorkKinds));

            Assert.Equal(1, intelligence.CallCount);

            Assert.Equal(2, weave.EmbedCallCount);

            Assert.Equal(2, await CountMemoriesAsync());

            Assert.Equal(pageWatermark, await GetWatermarkAsync(sessionId));

            Assert.NotNull(closing);

            closed = await CloseAsync(innerGate, closing!);

            await ReopenAsync(closed);

            reopened = true;

        }
        finally
        {

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));

            await hosted.StopAsync(stopTimeout.Token);

            if (!reopened && closed is not null)
            {

                await ReopenAsync(closed);

            }

            if (closed is null && closing is not null)
            {

                await closing.DisposeAsync();

            }

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_CloseBetweenPages_ResumesFirstUncommittedPageWithoutRebillingCommittedPage()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        DateTimeOffset pageOneWatermark = default;

        DateTimeOffset finalWatermark = default;

        DateTimeOffset firstCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        for (int index = 1; index <= 11; index++)
        {

            DateTimeOffset createdAt = firstCreatedAt.AddSeconds(index);

            DateTimeOffset stored = await CreateEntryAsync(
                sessionId,
                $"page-entry-{index:D2}",
                createdAt);

            if (index == 10)
            {

                pageOneWatermark = stored;

            }

            finalWatermark = stored;

        }

        GrimoireConnectionAdmissionGate innerGate = OpenGate();

        IGrimoireClosingOwner? closing = null;

        Task<Result>? drainTask = null;

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 2,
            TextForCall = callCount => callCount == 1
                ? """{ "memories": [{ "content": "memory from committed page one", "attachmentId": null }] }"""
                : """{ "memories": [{ "content": "memory from resumed page two", "attachmentId": null }] }""",
            OnCall = callCount =>
            {

                if (callCount != 1)
                {

                    return;

                }

                closing = BeginClosing(innerGate, 85);

                drainTask = innerGate.DrainRequestAndWorkAsync(
                    closing,
                    CancellationToken.None).AsTask();

            },
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        RecordingAdmissionGate gate = new(innerGate);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            gate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        IGrimoireExclusiveClosedLease? closed = null;

        bool reopened = false;

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: _seededSequence));

            await gate.NextOpenWaitStarted.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(drainTask);

            Result drained = await drainTask!.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

            Assert.Equal(1, intelligence.CallCount);

            Assert.Equal(1, weave.EmbedCallCount);

            Assert.Equal(1, await CountMemoriesAsync());

            Assert.Equal(pageOneWatermark, await GetWatermarkAsync(sessionId));

            string firstPrompt = Assert.Single(intelligence.StatelessUserContents);

            Assert.Contains("page-entry-01", firstPrompt, StringComparison.Ordinal);

            Assert.Contains("page-entry-10", firstPrompt, StringComparison.Ordinal);

            Assert.DoesNotContain("page-entry-11", firstPrompt, StringComparison.Ordinal);

            Assert.Single(service.PendingRequestsForTests);

            Assert.NotNull(closing);

            closed = await CloseAsync(innerGate, closing!);

            await ReopenAsync(closed);

            reopened = true;

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    completionTimeout.Token);

            }

            Assert.Equal(finalWatermark, await GetWatermarkAsync(sessionId));

            Assert.Equal(2, intelligence.CallCount);

            Assert.Equal(2, weave.EmbedCallCount);

            Assert.Equal(2, await CountMemoriesAsync());

            Assert.Equal(2, intelligence.StatelessUserContents.Count);

            string resumedPrompt = intelligence.StatelessUserContents[1];

            Assert.Contains("page-entry-11", resumedPrompt, StringComparison.Ordinal);

            Assert.DoesNotContain("page-entry-01", resumedPrompt, StringComparison.Ordinal);

            Assert.Equal(2, gate.WorkLeaseAttempts);

            Assert.Equal(3, gate.EffectGroupAttempts);

            Assert.Equal(2, gate.EffectGroupsAdmitted);

            Assert.Equal(1, gate.NextOpenWaitCalls);

        }
        finally
        {

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));

            await hosted.StopAsync(stopTimeout.Token);

            if (!reopened && closed is not null)
            {

                await ReopenAsync(closed);

            }

            if (closed is null && closing is not null)
            {

                await closing.DisposeAsync();

            }

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_RetryDueDuringMaintenance_PreservesPendingRequestAndRetryStateWithoutSpinning()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "a product failure whose retry meets maintenance");

        SagaExtractionRequest request = new(
            sessionId,
            [CreateProvenance(sessionId, Guid.NewGuid())],
            HadUnprovenancedAttachmentContent: true,
            AfterEntrySequenceExclusive: 0,
            ThroughEntrySequence: _seededSequence);

        GrimoireConnectionAdmissionGate innerGate = OpenGate();

        IGrimoireClosingOwner? closing = null;

        Task<Result>? drainTask = null;

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = "not valid json at all",
            ExpectedCallCount = 1,
            OnCall = _ =>
            {

                closing = BeginClosing(innerGate, 86);

                drainTask = innerGate.DrainRequestAndWorkAsync(
                    closing,
                    CancellationToken.None).AsTask();

            },
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        RecordingAdmissionGate gate = new(innerGate);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            gate,
            NullLogger<SagaExtractionService>.Instance)
        {
            RetryBaseDelayForTests = TimeSpan.FromMilliseconds(25),
        };

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(request);

            await gate.NextOpenWaitStarted.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(drainTask);

            Result drained = await drainTask!.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

            Assert.Equal(1, intelligence.CallCount);

            Assert.Equal(2, gate.WorkLeaseAttempts);

            Assert.Equal(1, gate.NextOpenWaitCalls);

            Assert.Equal(1, service.RetryAttemptForTests(sessionId));

            Assert.Same(request, Assert.Single(service.PendingRequestsForTests));

            await Task.Delay(TimeSpan.FromMilliseconds(100));

            Assert.Equal(1, intelligence.CallCount);

            Assert.Equal(2, gate.WorkLeaseAttempts);

            Assert.Equal(1, service.RetryAttemptForTests(sessionId));

            Assert.Same(request, Assert.Single(service.PendingRequestsForTests));

        }
        finally
        {

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));

            await hosted.StopAsync(stopTimeout.Token);

            if (closing is not null)
            {

                await closing.DisposeAsync();

            }

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_RetryRetainsFailedPrefixAheadOfLaterTurnWithoutRebillingCommittedPage()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        DateTimeOffset createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        for (int index = 1; index <= 11; index++)
        {

            _ = await CreateEntryAsync(
                sessionId,
                $"first-turn-entry-{index:D2}",
                createdAt.AddSeconds(index));

        }

        long firstThroughSequence = _seededSequence;

        Guid laterAttachmentId = Guid.NewGuid();

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 4,
            TextForCall = callCount => callCount switch
            {
                1 => """{ "memories": [{ "content": "committed first page", "attachmentId": null }] }""",
                2 => "not valid json",
                _ => $$"""{ "memories": [{ "content": "later attachment claim", "attachmentId": "{{laterAttachmentId}}" }] }""",
            },
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance)
        {
            RetryBaseDelayForTests = TimeSpan.FromMilliseconds(250),
        };

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: firstThroughSequence));

            using CancellationTokenSource retryScheduledTimeout = new(TimeSpan.FromSeconds(5));

            while (intelligence.CallCount < 2 || service.ScheduledRetryCountForTests == 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    retryScheduledTimeout.Token);

            }

            DateTimeOffset laterCreatedAt = await CreateEntryAsync(
                sessionId,
                "second-turn-entry-with-attachment",
                createdAt.AddMinutes(1));

            long laterThroughSequence = _seededSequence;

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [
                        new AttachmentMemoryProvenance(
                            sessionId,
                            laterAttachmentId,
                            "later-attachment",
                            1,
                            "later-hash",
                            laterCreatedAt,
                            "SessionAttachmentRag",
                            AttachmentSourceAvailability.Available),
                    ],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: firstThroughSequence,
                    ThroughEntrySequence: laterThroughSequence));

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    completionTimeout.Token);

            }

            Assert.Equal(4, intelligence.CallCount);

            Assert.Equal(2, weave.EmbedCallCount);

            Assert.Equal(2, await CountMemoriesAsync());

            Assert.DoesNotContain(
                "first-turn-entry-01",
                intelligence.StatelessUserContents[1],
                StringComparison.Ordinal);

            Assert.Contains(
                "first-turn-entry-11",
                intelligence.StatelessUserContents[1],
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                "second-turn-entry-with-attachment",
                intelligence.StatelessUserContents[2],
                StringComparison.Ordinal);

            Assert.Contains(
                "first-turn-entry-11",
                intelligence.StatelessUserContents[2],
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                "first-turn-entry-01",
                intelligence.StatelessUserContents[2],
                StringComparison.Ordinal);

            Assert.Contains(
                "second-turn-entry-with-attachment",
                intelligence.StatelessUserContents[3],
                StringComparison.Ordinal);

            SagaMemoryDto attachmentMemory = Assert.Single(
                await CreateStore().ListAsync(
                    null,
                    sessionId,
                    MemoryScope.Installation,
                    10,
                    0,
                    CancellationToken.None),
                static memory => memory.AttachmentProvenance is not null);

            Assert.Equal(laterAttachmentId, attachmentMemory.AttachmentProvenance!.AttachmentId);

            Assert.Equal(laterThroughSequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

        }
        finally
        {

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_LaterIntervalAlone_ProcessesUnpaidGapWithNoProvenanceAuthority()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        DateTimeOffset oldCreatedAt = await CreateEntryAsync(sessionId, "unpaid older-turn entry");

        long oldThroughSequence = _seededSequence;

        DateTimeOffset laterCreatedAt = await CreateEntryAsync(
            sessionId,
            "later turn entry backed by its attachment",
            oldCreatedAt.AddSeconds(1));

        long laterThroughSequence = _seededSequence;

        Guid laterAttachmentId = Guid.NewGuid();

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 2,
            TextForCall = callCount => callCount == 1
                ? $$"""{ "memories": [{ "content": "ordinary gap conclusion", "attachmentId": null }, { "content": "attachment gap claim", "attachmentId": "{{laterAttachmentId}}" }] }"""
                : $$"""{ "memories": [{ "content": "attachment claim", "attachmentId": "{{laterAttachmentId}}" }] }""",
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [CreateProvenance(sessionId, laterAttachmentId)],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: oldThroughSequence,
                    ThroughEntrySequence: laterThroughSequence));

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    completionTimeout.Token);

            }

            Assert.Equal(2, intelligence.CallCount);

            Assert.Equal(1, weave.EmbedCallCount);

            SagaMemoryDto memory = Assert.Single(
                await CreateStore().ListAsync(
                    null,
                    sessionId,
                    MemoryScope.Installation,
                    10,
                    0,
                    CancellationToken.None));

            Assert.Equal(laterAttachmentId, memory.AttachmentProvenance?.AttachmentId);

            Assert.Contains("unpaid older-turn entry", intelligence.StatelessUserContents[0], StringComparison.Ordinal);

            Assert.DoesNotContain("later turn entry", intelligence.StatelessUserContents[0], StringComparison.Ordinal);

            Assert.Contains("later turn entry", intelligence.StatelessUserContents[1], StringComparison.Ordinal);

            Assert.DoesNotContain("unpaid older-turn entry", intelligence.StatelessUserContents[1], StringComparison.Ordinal);

        }
        finally
        {

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_LaterIntervalMergedDuringFirstPage_DoesNotAuthorizeEarlierRemainder()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        for (int index = 1; index <= 11; index++)
        {

            _ = await CreateEntryAsync(sessionId, $"older-entry-{index:D2}");

        }

        long firstThroughSequence = _seededSequence;

        Guid laterAttachmentId = Guid.NewGuid();

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 3,
            Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            TextForCall = callCount => callCount == 1
                ? """{ "memories": [] }"""
                : $$"""{ "memories": [{ "content": "attachment claim", "attachmentId": "{{laterAttachmentId}}" }] }""",
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: firstThroughSequence));

            await intelligence.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(5));

            DateTimeOffset laterCreatedAt = await CreateEntryAsync(
                sessionId,
                "later-entry-with-attachment");

            long laterThroughSequence = _seededSequence;

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [
                        new AttachmentMemoryProvenance(
                            sessionId,
                            laterAttachmentId,
                            "later-attachment",
                            1,
                            "later-hash",
                            laterCreatedAt,
                            "SessionAttachmentRag",
                            AttachmentSourceAvailability.Available),
                    ],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: firstThroughSequence,
                    ThroughEntrySequence: laterThroughSequence));

            intelligence.Gate!.TrySetResult(true);

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    completionTimeout.Token);

            }

            Assert.Equal(3, intelligence.CallCount);

            Assert.Equal(1, weave.EmbedCallCount);

            SagaMemoryDto memory = Assert.Single(
                await CreateStore().ListAsync(
                    null,
                    sessionId,
                    MemoryScope.Installation,
                    10,
                    0,
                    CancellationToken.None));

            Assert.Equal(laterAttachmentId, memory.AttachmentProvenance?.AttachmentId);

            Assert.Contains("older-entry-11", intelligence.StatelessUserContents[1], StringComparison.Ordinal);

            Assert.DoesNotContain("later-entry-with-attachment", intelligence.StatelessUserContents[1], StringComparison.Ordinal);

            Assert.Contains("later-entry-with-attachment", intelligence.StatelessUserContents[2], StringComparison.Ordinal);

        }
        finally
        {

            intelligence.Gate!.TrySetResult(true);

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_StricterDuplicateArrivingDuringProviderCall_RevokesPersistenceAuthority()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        _ = await CreateEntryAsync(sessionId, "ordinary-looking content from a tainted turn");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 1,
            NextText = """{ "memories": [{ "content": "must be rejected after stricter replay arrives", "attachmentId": null }] }""",
            Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            SagaExtractionRequest original = new(
                sessionId,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 0,
                ThroughEntrySequence: _seededSequence);

            service.EnqueueExtraction(original);

            await intelligence.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(5));

            service.EnqueueExtraction(
                original with { HadUnprovenancedAttachmentContent = true });

            intelligence.Gate!.TrySetResult(true);

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            await WaitForWatermarkAsync(sessionId, TimeSpan.FromSeconds(5));

            Assert.Equal(0, weave.EmbedCallCount);

            Assert.Equal(0, await CountMemoriesAsync());

            Assert.Equal(_seededSequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

        }
        finally
        {

            intelligence.Gate!.TrySetResult(true);

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_StricterDuplicateArrivingDuringEmbedding_RevokesPersistenceAuthority()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        _ = await CreateEntryAsync(sessionId, "ordinary-looking content from a tainted turn");

        TaskCompletionSource<bool> embeddingEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<bool> releaseEmbedding = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        FakeWeaveService weave = new()
        {
            OnEmbedAsync = async _ =>
            {

                embeddingEntered.TrySetResult(true);

                await releaseEmbedding.Task.ConfigureAwait(false);

            },
        };

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 1,
            NextText = """{ "memories": [{ "content": "must be rejected after stricter replay arrives", "attachmentId": null }] }""",
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            SagaExtractionRequest original = new(
                sessionId,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 0,
                ThroughEntrySequence: _seededSequence);

            service.EnqueueExtraction(original);

            await embeddingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            service.EnqueueExtraction(
                original with { HadUnprovenancedAttachmentContent = true });

            releaseEmbedding.TrySetResult(true);

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            await WaitForWatermarkAsync(sessionId, TimeSpan.FromSeconds(5));

            Assert.Equal(1, weave.EmbedCallCount);

            Assert.Equal(0, await CountMemoriesAsync());

            Assert.Equal(_seededSequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

        }
        finally
        {

            releaseEmbedding.TrySetResult(true);

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_StricterDuplicateDuringFirstEmbedding_PreventsLaterCandidateEmbedding()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        _ = await CreateEntryAsync(sessionId, "ordinary-looking content from a tainted turn");

        TaskCompletionSource<bool> firstEmbeddingEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<bool> releaseFirstEmbedding = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        FakeWeaveService weave = new()
        {
            OnEmbedAsync = async callCount =>
            {

                if (callCount != 1)
                {

                    return;

                }

                firstEmbeddingEntered.TrySetResult(true);

                await releaseFirstEmbedding.Task.ConfigureAwait(false);

            },
        };

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 1,
            NextText = """{ "memories": [{ "content": "first candidate", "attachmentId": null }, { "content": "second candidate", "attachmentId": null }] }""",
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            SagaExtractionRequest original = new(
                sessionId,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 0,
                ThroughEntrySequence: _seededSequence);

            service.EnqueueExtraction(original);

            await firstEmbeddingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            service.EnqueueExtraction(
                original with { HadUnprovenancedAttachmentContent = true });

            releaseFirstEmbedding.TrySetResult(true);

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            await WaitForWatermarkAsync(sessionId, TimeSpan.FromSeconds(5));

            Assert.Equal(1, weave.EmbedCallCount);

            Assert.Equal(0, await CountMemoriesAsync());

            Assert.Equal(_seededSequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

        }
        finally
        {

            releaseFirstEmbedding.TrySetResult(true);

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_DurablePageProgress_ResetsRetryLadderForNextPage()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        DateTimeOffset createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        for (int index = 1; index <= 11; index++)
        {

            _ = await CreateEntryAsync(
                sessionId,
                $"retry-reset-entry-{index:D2}",
                createdAt.AddSeconds(index));

        }

        long throughEntrySequence = _seededSequence;

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 7,
            TextForCall = callCount => callCount switch
            {
                <= 4 => "not valid json",
                5 => """{ "memories": [] }""",
                6 => "not valid json",
                _ => """{ "memories": [] }""",
            },
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance)
        {
            RetryBaseDelayForTests = TimeSpan.FromMilliseconds(5),
        };

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: throughEntrySequence));

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    completionTimeout.Token);

            }

            Assert.Equal(7, intelligence.CallCount);

            Assert.Equal(0, service.RetryAttemptForTests(sessionId));

            Assert.Contains("retry-reset-entry-01", intelligence.StatelessUserContents[4], StringComparison.Ordinal);

            Assert.DoesNotContain("retry-reset-entry-11", intelligence.StatelessUserContents[4], StringComparison.Ordinal);

            Assert.Contains("retry-reset-entry-11", intelligence.StatelessUserContents[5], StringComparison.Ordinal);

            Assert.Contains("retry-reset-entry-11", intelligence.StatelessUserContents[6], StringComparison.Ordinal);

            Assert.Equal(throughEntrySequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

        }
        finally
        {

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_UnexpectedFailureOnLaterInterval_UsesThatIntervalsRetryLadder()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        _ = await CreateEntryAsync(sessionId, "first interval commits");

        long firstThroughSequence = _seededSequence;

        _ = await CreateEntryAsync(sessionId, "second interval dependency throws");

        long secondThroughSequence = _seededSequence;

        FakeWeaveService weave = new()
        {
            OnEmbedAsync = _ => throw new InvalidOperationException("Simulated unexpected embedding dependency exception."),
        };

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 6,
            TextForCall = callCount => callCount == 1
                ? """{ "memories": [] }"""
                : """{ "memories": [{ "content": "cannot be embedded", "attachmentId": null }] }""",
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance)
        {
            RetryBaseDelayForTests = TimeSpan.FromMilliseconds(5),
        };

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: firstThroughSequence));

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: firstThroughSequence,
                    ThroughEntrySequence: secondThroughSequence));

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    completionTimeout.Token);

            }

            Assert.Equal(6, intelligence.CallCount);

            Assert.Equal(0, service.RetryAttemptForTests(sessionId));

            Assert.Equal(firstThroughSequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

        }
        finally
        {

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_EarlyUnavailableRetry_PreservesLaterIntervalsFailureOwnership()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        _ = await CreateEntryAsync(sessionId, "first interval commits");

        long firstThroughSequence = _seededSequence;

        _ = await CreateEntryAsync(sessionId, "second interval keeps its bounded retry ownership");

        long secondThroughSequence = _seededSequence;

        FakeWeaveService weave = new()
        {
            AvailabilityForCheck = checkCount => checkCount % 2 == 1,
        };

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 4,
            TextForCall = callCount => callCount == 1
                ? """{ "memories": [] }"""
                : "not valid json",
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance)
        {
            RetryBaseDelayForTests = TimeSpan.FromMilliseconds(5),
        };

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: firstThroughSequence));

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: firstThroughSequence,
                    ThroughEntrySequence: secondThroughSequence));

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    completionTimeout.Token);

            }

            Assert.Equal(4, intelligence.CallCount);

            Assert.Equal(5, weave.AvailabilityCheckCount);

            Assert.Equal(0, service.RetryAttemptForTests(sessionId));

            Assert.Equal(firstThroughSequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

        }
        finally
        {

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_CursorCatchesUpBeforeFinalUnavailableRetry_PreservesLaterMerge()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        DateTimeOffset firstCreatedAt = await CreateEntryAsync(
            sessionId,
            "first turn becomes externally cursor-covered");

        long firstThroughSequence = _seededSequence;

        TaskCompletionSource<bool> fifthAvailabilityCheckEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<bool> releaseFifthAvailabilityCheck = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        int laterTurnEnqueued = 0;

        FakeWeaveService weave = new()
        {
            AvailabilityForCheck = checkCount =>
            {

                if (checkCount <= 4)
                {

                    return false;

                }

                if (Volatile.Read(ref laterTurnEnqueued) != 0)
                {

                    return true;

                }

                fifthAvailabilityCheckEntered.TrySetResult(true);

                releaseFifthAvailabilityCheck.Task.GetAwaiter().GetResult();

                return false;

            },
        };

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 1,
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance)
        {
            RetryBaseDelayForTests = TimeSpan.FromMilliseconds(100),
        };

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: firstThroughSequence));

            using CancellationTokenSource fourthRetryTimeout = new(TimeSpan.FromSeconds(5));

            while (service.RetryAttemptForTests(sessionId) != 4
                || service.ScheduledRetryCountForTests == 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    fourthRetryTimeout.Token);

            }

            await CreateStore().SetExtractionCursorAsync(
                sessionId,
                new SagaExtractionCursor(firstThroughSequence, firstCreatedAt),
                CancellationToken.None);

            using CancellationTokenSource raceTimeout = new(TimeSpan.FromSeconds(5));

            while (!fifthAvailabilityCheckEntered.Task.IsCompleted
                && service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    raceTimeout.Token);

            }

            _ = await CreateEntryAsync(sessionId, "later turn survives the caught-up retry race");

            long laterThroughSequence = _seededSequence;

            Volatile.Write(ref laterTurnEnqueued, 1);

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: firstThroughSequence,
                    ThroughEntrySequence: laterThroughSequence));

            releaseFifthAvailabilityCheck.TrySetResult(true);

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    completionTimeout.Token);

            }

            Assert.Equal(1, intelligence.CallCount);

            Assert.Equal(5, weave.AvailabilityCheckCount);

            Assert.Equal(0, service.RetryAttemptForTests(sessionId));

            Assert.Contains(
                "later turn survives the caught-up retry race",
                intelligence.LastStatelessUserContent,
                StringComparison.Ordinal);

            Assert.Equal(laterThroughSequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

        }
        finally
        {

            releaseFifthAvailabilityCheck.TrySetResult(true);

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [Fact]
    public async Task ExecuteAsync_PreCursorFailure_AbandonsOnePendingFrontierAtATime()
    {

        Guid sessionId = Guid.NewGuid();

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new();

        (_, _, ArcanumSettings settings) = BuildScope(weave, intelligence);

        ServiceCollection incompleteServices = new();

        incompleteServices.AddSingleton<IWeaveService>(weave);

        IServiceScopeFactory scopeFactory = incompleteServices
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        RecordingAdmissionGate gate = new(_admissionGate);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            gate,
            NullLogger<SagaExtractionService>.Instance)
        {
            RetryBaseDelayForTests = TimeSpan.FromMilliseconds(5),
        };

        service.EnqueueExtraction(
            new SagaExtractionRequest(
                sessionId,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 0,
                ThroughEntrySequence: 1));

        service.EnqueueExtraction(
            new SagaExtractionRequest(
                sessionId,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 1,
                ThroughEntrySequence: 2));

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    completionTimeout.Token);

            }

            Assert.Equal(10, gate.WorkLeaseAttempts);

            Assert.Equal(0, service.RetryAttemptForTests(sessionId));

        }
        finally
        {

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_LaterTurnMergedDuringFinalFailure_StartsFreshLadderInsteadOfBeingDropped()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        _ = await CreateEntryAsync(sessionId, "first turn unpaid entry");

        long firstThroughSequence = _seededSequence;

        TaskCompletionSource<bool> fifthCallEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<bool> releaseFifthCall = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 7,
            TextForCall = callCount => callCount <= 5
                ? "not valid json"
                : """{ "memories": [] }""",
            BeforeResultAsync = async (callCount, cancellationToken) =>
            {

                if (callCount != 5)
                {

                    return;

                }

                fifthCallEntered.TrySetResult(true);

                await releaseFifthCall.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            },
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance)
        {
            RetryBaseDelayForTests = TimeSpan.FromMilliseconds(5),
        };

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: firstThroughSequence));

            await fifthCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            _ = await CreateEntryAsync(sessionId, "later turn must survive abandonment");

            long laterThroughSequence = _seededSequence;

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: firstThroughSequence,
                    ThroughEntrySequence: laterThroughSequence));

            releaseFifthCall.TrySetResult(true);

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    completionTimeout.Token);

            }

            Assert.Equal(7, intelligence.CallCount);

            Assert.Equal(0, service.RetryAttemptForTests(sessionId));

            Assert.Contains("first turn unpaid entry", intelligence.StatelessUserContents[5], StringComparison.Ordinal);

            Assert.DoesNotContain("later turn", intelligence.StatelessUserContents[5], StringComparison.Ordinal);

            Assert.Contains("later turn must survive abandonment", intelligence.StatelessUserContents[6], StringComparison.Ordinal);

            Assert.Equal(laterThroughSequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

        }
        finally
        {

            releaseFifthCall.TrySetResult(true);

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_LaterTurnMergedBeforeFinalRetry_StartsFreshLadderInsteadOfBeingDropped()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        _ = await CreateEntryAsync(sessionId, "first turn unpaid entry");

        long firstThroughSequence = _seededSequence;

        TaskCompletionSource<bool> fourthCallEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 7,
            TextForCall = callCount => callCount <= 5
                ? "not valid json"
                : """{ "memories": [] }""",
            OnCall = callCount =>
            {

                if (callCount == 4)
                {

                    fourthCallEntered.TrySetResult(true);

                }

            },
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance)
        {
            RetryBaseDelayForTests = TimeSpan.FromMilliseconds(100),
        };

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: firstThroughSequence));

            await fourthCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource backoffTimeout = new(TimeSpan.FromSeconds(5));

            while (service.RetryAttemptForTests(sessionId) != 4
                || service.ScheduledRetryCountForTests == 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    backoffTimeout.Token);

            }

            _ = await CreateEntryAsync(sessionId, "later turn must survive the final retry backoff");

            long laterThroughSequence = _seededSequence;

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: firstThroughSequence,
                    ThroughEntrySequence: laterThroughSequence));

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(10));

            using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    completionTimeout.Token);

            }

            Assert.Equal(7, intelligence.CallCount);

            Assert.Equal(0, service.RetryAttemptForTests(sessionId));

            Assert.Contains("first turn unpaid entry", intelligence.StatelessUserContents[5], StringComparison.Ordinal);

            Assert.DoesNotContain("later turn", intelligence.StatelessUserContents[5], StringComparison.Ordinal);

            Assert.Contains("later turn must survive the final retry backoff", intelligence.StatelessUserContents[6], StringComparison.Ordinal);

            Assert.Equal(laterThroughSequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

        }
        finally
        {

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task StopAsync_WhileDeferredGateStaysClosed_CancelsWaitAndClearsPendingState()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content parked behind a keep-closed transition");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new();

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        GrimoireConnectionAdmissionGate innerGate = OpenGate();

        RecordingAdmissionGate gate = new(innerGate);

        await using IGrimoireClosingOwner closing = BeginClosing(innerGate, 83);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            gate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        bool stopped = false;

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: _seededSequence));

            await gate.NextOpenWaitStarted.WaitAsync(TimeSpan.FromSeconds(5));

            IGrimoireExclusiveClosedLease closed = await CloseAsync(innerGate, closing);

            Result keptClosed = await closed.CompleteAsync(
                CovenantExclusiveLeaseDisposition.KeepClosed,
                CancellationToken.None);

            Assert.True(
                keptClosed.IsSuccess,
                keptClosed.IsFailure ? keptClosed.Error.Message : null);

            await closed.DisposeAsync();

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));

            await hosted.StopAsync(stopTimeout.Token);

            stopped = true;

            Assert.Equal(1, gate.NextOpenWaitCalls);

            Assert.Equal(0, intelligence.CallCount);

            Assert.Equal(0, weave.EmbedCallCount);

            Assert.Empty(service.PendingRequestsForTests);

            await Task.Delay(TimeSpan.FromMilliseconds(100));

            Assert.Equal(0, intelligence.CallCount);

        }
        finally
        {

            if (!stopped)
            {

                using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));

                await hosted.StopAsync(stopTimeout.Token);

            }

        }

    }

    [SkippableFact]
    public async Task StopAsync_ObservesAndCancelsScheduledRetryBeforeReturning()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content whose extraction schedules a long retry");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            NextText = "not valid json at all",
            ExpectedCallCount = 1,
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance)
        {

            RetryBaseDelayForTests = TimeSpan.FromMinutes(10),

        };

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        bool stopped = false;

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: _seededSequence));

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource retryScheduledTimeout = new(TimeSpan.FromSeconds(5));

            while (service.ScheduledRetryCountForTests == 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    retryScheduledTimeout.Token);

            }

            Assert.Equal(1, service.ScheduledRetryCountForTests);

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));

            await hosted.StopAsync(stopTimeout.Token);

            stopped = true;

            Assert.Equal(0, service.ScheduledRetryCountForTests);

            Assert.Empty(service.PendingRequestsForTests);

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: _seededSequence));

            Assert.Empty(service.PendingRequestsForTests);

        }
        finally
        {

            if (!stopped)
            {

                using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));

                await hosted.StopAsync(stopTimeout.Token);

            }

        }

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
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance)
        {

            RetryBaseDelayForTests = TimeSpan.FromMilliseconds(5),

        };

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: _seededSequence));

            using CancellationTokenSource firstLadderTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    firstLadderTimeout.Token);

            }

            // A permanent failure must not become an endless ladder of billable LLM round-trips.
            Assert.Equal(5, intelligence.CallCount);

            Assert.Equal(0, service.RetryAttemptForTests(sessionId));

            // Abandoning the request is not permanent poisoning: the session's next successful turn
            // enqueues it again and it gets a fresh bounded ladder.
            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: _seededSequence));

            using CancellationTokenSource secondLadderTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    secondLadderTimeout.Token);

            }

            Assert.Equal(10, intelligence.CallCount);

            Assert.Equal(0, service.RetryAttemptForTests(sessionId));

        }
        finally
        {

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_RetryBackoffForOneSession_DoesNotBlockAnotherSessionsFirstAttempt()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid firstSessionId = await CreateSessionAsync();

        await CreateEntryAsync(firstSessionId, "content this extraction model can never parse");

        long firstThroughSequence = _seededSequence;

        Guid secondSessionId = await CreateSessionAsync();

        await CreateEntryAsync(secondSessionId, "content this extraction model can never parse");

        long secondThroughSequence = _seededSequence;

        FakeWeaveService weave = new();

        // Every attempt fails identically, forever, for both sessions - the only thing under test is
        // whether the second session's first attempt has to wait behind the first session's retry
        // backoff, not whether either one succeeds. ExpectedCallCount/WaitForExpectedCallsAsync
        // waits deterministically for the second call instead of polling within a wall-clock margin
        // against a comparable-duration backoff, which a loaded machine could miss.
        FakeIntelligenceProvider intelligence = new()
        {
            NextText = "not valid json at all",
            ExpectedCallCount = 2,
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(weave, intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance)
        {

            // Clamps to MaximumAutomaticRetryDelay (5 minutes) - comfortably longer than the wait
            // below, so the first session's own retry cannot possibly fire during this test and a
            // second call is unambiguously the second session's first attempt.
            RetryBaseDelayForTests = TimeSpan.FromMinutes(10),

        };

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    firstSessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: firstThroughSequence));

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    secondSessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: secondThroughSequence));

            // A single-reader loop that awaits its own retry backoff inline starves every other
            // queued session for the whole delay. Reaching two calls at all inside this bound - let
            // alone the five seconds allowed - proves the second session's first attempt did not
            // queue behind the first session's five-minute-plus wait.
            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, intelligence.CallCount);

        }
        finally
        {

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task EnqueueExtraction_ConflictingDuplicateInterval_IntersectsAuthorityWithoutDuplicating()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content worth extracting");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(weave, intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            Guid firstAttachmentId = Guid.NewGuid();

            Guid secondAttachmentId = Guid.NewGuid();

            AttachmentMemoryProvenance firstProvenance = new(
                sessionId,
                firstAttachmentId,
                "logical-key-one",
                1,
                "hash-one",
                DateTimeOffset.UtcNow,
                "note",
                AttachmentSourceAvailability.Available);

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [firstProvenance],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: _seededSequence));

            await intelligence.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The first attempt is now blocked mid-flight on Gate. A second arrival for the same
            // session must merge into the still-reserved dedup key instead of racing a duplicate
            // channel write for the same session: dequeuing used to remove the key immediately,
            // so a concurrent enqueue found nothing to merge into and queued a second, separate entry.
            AttachmentMemoryProvenance secondProvenance = new(
                sessionId,
                secondAttachmentId,
                "logical-key-two",
                1,
                "hash-two",
                DateTimeOffset.UtcNow,
                "note",
                AttachmentSourceAvailability.Available);

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [secondProvenance],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: _seededSequence));

            SagaExtractionRequest pending = Assert.Single(service.PendingRequestsForTests);

            Assert.Equal(sessionId, pending.SessionId);

            Assert.Empty(pending.MaterializedAttachments);

            Assert.Equal(0, pending.AfterEntrySequenceExclusive);

            Assert.Equal(_seededSequence, pending.ThroughEntrySequence);

        }
        finally
        {

            // Unconditionally, even if an assertion above threw: the fake is parked on Gate with no
            // cancellation wiring of its own, and StopAsync(CancellationToken.None) waits for
            // ExecuteAsync to finish, so a still-held gate would hang the test forever instead of
            // reporting the assertion failure.
            intelligence.Gate!.TrySetResult(true);

            await hosted.StopAsync(CancellationToken.None);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_SharedTimestampAcrossFrontiers_ProcessesEachEntryExactlyOnce()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        DateTimeOffset sharedCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        await CreateEntryAsync(sessionId, "first-turn-user", sharedCreatedAt);

        await CreateEntryAsync(sessionId, "first-turn-assistant", sharedCreatedAt);

        long firstThroughSequence = _seededSequence;

        await CreateEntryAsync(sessionId, "second-turn-user", sharedCreatedAt);

        await CreateEntryAsync(sessionId, "second-turn-assistant", sharedCreatedAt);

        long secondThroughSequence = _seededSequence;

        Guid firstAttachmentId = Guid.NewGuid();

        Guid secondAttachmentId = Guid.NewGuid();

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 2,
            TextForCall = callCount => callCount == 1
                ? $$"""{ "memories": [{ "content": "memory from the first attachment", "attachmentId": "{{firstAttachmentId}}" }] }"""
                : $$"""{ "memories": [{ "content": "memory from the second attachment", "attachmentId": "{{secondAttachmentId}}" }] }""",
        };

        (IServiceScopeFactory scopes, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            AttachmentMemoryProvenance firstProvenance = CreateProvenance(
                sessionId,
                firstAttachmentId);

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [firstProvenance],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: firstThroughSequence));

            using CancellationTokenSource firstCompletionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    firstCompletionTimeout.Token);

            }

            Assert.Equal(1, intelligence.CallCount);

            Assert.Equal(1, weave.EmbedCallCount);

            Assert.Equal(1, await CountMemoriesAsync());

            Assert.Equal(sharedCreatedAt, await GetWatermarkAsync(sessionId));

            Assert.Equal(firstThroughSequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

            AttachmentMemoryProvenance secondProvenance = CreateProvenance(
                sessionId,
                secondAttachmentId);

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [secondProvenance],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: firstThroughSequence,
                    ThroughEntrySequence: secondThroughSequence));

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource secondCompletionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    secondCompletionTimeout.Token);

            }

            Assert.Equal(2, intelligence.CallCount);

            Assert.Equal(2, weave.EmbedCallCount);

            Assert.Equal(2, await CountMemoriesAsync());

            Assert.Equal(sharedCreatedAt, await GetWatermarkAsync(sessionId));

            Assert.Equal(secondThroughSequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

            Assert.Collection(
                intelligence.StatelessUserContents,
                firstPrompt =>
                {

                    Assert.Contains("first-turn-user", firstPrompt, StringComparison.Ordinal);

                    Assert.Contains("first-turn-assistant", firstPrompt, StringComparison.Ordinal);

                    Assert.DoesNotContain("second-turn-user", firstPrompt, StringComparison.Ordinal);

                    Assert.DoesNotContain("second-turn-assistant", firstPrompt, StringComparison.Ordinal);

                },
                secondPrompt =>
                {

                    Assert.DoesNotContain("first-turn-user", secondPrompt, StringComparison.Ordinal);

                    Assert.DoesNotContain("first-turn-assistant", secondPrompt, StringComparison.Ordinal);

                    Assert.Contains("second-turn-user", secondPrompt, StringComparison.Ordinal);

                    Assert.Contains("second-turn-assistant", secondPrompt, StringComparison.Ordinal);

                });

            HashSet<Guid> storedAttachmentIds =
            [
                .. (await CreateStore().ListAsync(
                    null,
                    sessionId,
                    MemoryScope.Installation,
                    10,
                    0,
                    CancellationToken.None))
                    .Select(memory => memory.AttachmentProvenance!.AttachmentId),
            ];

            Assert.Equal(
                new HashSet<Guid> { firstAttachmentId, secondAttachmentId },
                storedAttachmentIds);

        }
        finally
        {

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));

            await hosted.StopAsync(stopTimeout.Token);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_PreStartMergedFrontiers_KeepEachTurnsProvenanceIsolated()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        DateTimeOffset firstCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        for (int index = 1; index <= 10; index++)
        {

            _ = await CreateEntryAsync(
                sessionId,
                $"older-turn-entry-{index:D2}",
                firstCreatedAt.AddSeconds(index));

        }

        long firstThroughSequence = _seededSequence;

        DateTimeOffset laterCreatedAt = await CreateEntryAsync(
            sessionId,
            "later-turn-entry-with-attachment",
            firstCreatedAt.AddMinutes(1));

        long secondThroughSequence = _seededSequence;

        Guid laterAttachmentId = Guid.NewGuid();

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 2,
            TextForCall = _ =>
                $$"""{ "memories": [{ "content": "claimed attachment memory", "attachmentId": "{{laterAttachmentId}}" }] }""",
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

        service.EnqueueExtraction(
            new SagaExtractionRequest(
                sessionId,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 0,
                ThroughEntrySequence: firstThroughSequence));

        service.EnqueueExtraction(
            new SagaExtractionRequest(
                sessionId,
                [
                    new AttachmentMemoryProvenance(
                        sessionId,
                        laterAttachmentId,
                        "later-attachment",
                        1,
                        "later-attachment-hash",
                        laterCreatedAt,
                        "SessionAttachmentRag",
                        AttachmentSourceAvailability.Available),
                ],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: firstThroughSequence,
                ThroughEntrySequence: secondThroughSequence));

        Assert.Collection(
            service.PendingSegmentsForTests(sessionId),
            first =>
            {

                Assert.Equal(firstThroughSequence, first.ThroughEntrySequence);

                Assert.Empty(first.MaterializedAttachments);

            },
            second =>
            {

                Assert.Equal(secondThroughSequence, second.ThroughEntrySequence);

                Assert.Equal(laterAttachmentId, Assert.Single(second.MaterializedAttachments).AttachmentId);

            });

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource completionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    completionTimeout.Token);

            }

            Assert.Equal(2, intelligence.CallCount);

            Assert.Equal(1, weave.EmbedCallCount);

            Assert.Collection(
                intelligence.StatelessUserContents,
                firstPrompt =>
                {

                    Assert.Contains("older-turn-entry-01", firstPrompt, StringComparison.Ordinal);

                    Assert.DoesNotContain("later-turn-entry-with-attachment", firstPrompt, StringComparison.Ordinal);

                    Assert.DoesNotContain(laterAttachmentId.ToString(), firstPrompt, StringComparison.OrdinalIgnoreCase);

                },
                secondPrompt =>
                {

                    Assert.DoesNotContain("older-turn-entry-01", secondPrompt, StringComparison.Ordinal);

                    Assert.Contains("later-turn-entry-with-attachment", secondPrompt, StringComparison.Ordinal);

                    Assert.Contains(laterAttachmentId.ToString(), secondPrompt, StringComparison.OrdinalIgnoreCase);

                });

            SagaMemoryDto memory = Assert.Single(
                await CreateStore().ListAsync(
                    null,
                    sessionId,
                    MemoryScope.Installation,
                    10,
                    0,
                    CancellationToken.None));

            Assert.Equal(laterAttachmentId, memory.AttachmentProvenance?.AttachmentId);

            Assert.Equal(secondThroughSequence, (await GetExtractionCursorAsync(sessionId))!.EntrySequence);

        }
        finally
        {

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));

            await hosted.StopAsync(stopTimeout.Token);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_EntryCommittedBeforeLaterEnqueue_WaitsForItsProvenanceFrontier()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        DateTimeOffset firstCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        DateTimeOffset initialPageWatermark = default;

        for (int index = 1; index <= 10; index++)
        {

            initialPageWatermark = await CreateEntryAsync(
                sessionId,
                $"initial-page-entry-{index:D2}",
                firstCreatedAt.AddSeconds(index));

        }

        long initialThroughSequence = _seededSequence;

        Guid laterAttachmentId = Guid.NewGuid();

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            ExpectedCallCount = 2,
            Entered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            Gate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            TextForCall = callCount => callCount == 1
                ? """{ "memories": [] }"""
                : $$"""{ "memories": [{ "content": "memory from the later attachment", "attachmentId": "{{laterAttachmentId}}" }] }""",
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(
            weave,
            intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        try
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: initialThroughSequence));

            await intelligence.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(5));

            DateTimeOffset laterCreatedAt = await CreateEntryAsync(
                sessionId,
                "entry backed by the later attachment",
                initialPageWatermark);

            long laterThroughSequence = _seededSequence;

            Assert.Equal(initialThroughSequence + 1L, laterThroughSequence);

            intelligence.Gate!.TrySetResult(true);

            using CancellationTokenSource initialCompletionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    initialCompletionTimeout.Token);

            }

            Assert.Equal(1, intelligence.CallCount);

            Assert.Equal(0, weave.EmbedCallCount);

            Assert.Equal(initialPageWatermark, await GetWatermarkAsync(sessionId));

            AttachmentMemoryProvenance laterProvenance = new(
                sessionId,
                laterAttachmentId,
                "later-attachment",
                3,
                "later-attachment-hash",
                laterCreatedAt,
                "SessionAttachmentRag",
                AttachmentSourceAvailability.Available);

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [laterProvenance],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: initialThroughSequence,
                    ThroughEntrySequence: laterThroughSequence));

            await intelligence.WaitForExpectedCallsAsync(TimeSpan.FromSeconds(5));

            using CancellationTokenSource finalCompletionTimeout = new(TimeSpan.FromSeconds(5));

            while (service.PendingRequestsForTests.Count != 0)
            {

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    finalCompletionTimeout.Token);

            }

            Assert.Equal(2, intelligence.CallCount);

            Assert.Equal(1, weave.EmbedCallCount);

            Assert.Contains(
                "later-attachment",
                intelligence.StatelessUserContents[1],
                StringComparison.Ordinal);

            Assert.Equal(laterCreatedAt, await GetWatermarkAsync(sessionId));

            SagaMemoryDto memory = Assert.Single(
                await CreateStore().ListAsync(
                    null,
                    sessionId,
                    MemoryScope.Installation,
                    10,
                    0,
                    CancellationToken.None));

            Assert.NotNull(memory.AttachmentProvenance);

            Assert.Equal(
                laterAttachmentId,
                memory.AttachmentProvenance.AttachmentId);

        }
        finally
        {

            intelligence.Gate!.TrySetResult(true);

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));

            await hosted.StopAsync(stopTimeout.Token);

        }

    }

    /// <summary>
    /// The dedup key survives dequeue (peeked, not removed), but
    /// the shutdown-cancellation return path did not release it, unlike the disabled-skip branch a few
    /// lines above it - a session mid-attempt when the host stops would stay in <c>_pending</c> forever
    /// with no channel entry left to ever read it back out.
    /// </summary>
    [SkippableFact]
    public async Task StopAsync_duringAnInFlightAttempt_releasesTheDedupKey()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content worth extracting");

        FakeWeaveService weave = new();

        FakeIntelligenceProvider intelligence = new()
        {
            Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        (IServiceScopeFactory scopeFactory, _, ArcanumSettings settings) = BuildScope(weave, intelligence);

        SagaExtractionService service = new(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        service.EnqueueExtraction(
            new SagaExtractionRequest(
                sessionId,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 0,
                ThroughEntrySequence: _seededSequence));

        await intelligence.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The attempt is now blocked mid-flight, awaiting Gate on the service's own stoppingToken.
        // StopAsync cancels that token immediately, so the gated call throws OperationCanceledException
        // instead of ever completing - exercising ExecuteAsync's shutdown-cancellation return path
        // while the dedup key is still held.
        await hosted.StopAsync(CancellationToken.None);

        Assert.Empty(service.PendingRequestsForTests);

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
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

        foreach (Guid sessionId in sessionIds)
        {

            service.EnqueueExtraction(
                new SagaExtractionRequest(
                    sessionId,
                    [],
                    HadUnprovenancedAttachmentContent: false,
                    AfterEntrySequenceExclusive: 0,
                    ThroughEntrySequence: 1));

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
    public void EnqueueExtraction_DuplicateIntervalWithConflictingAttachmentIdentity_FailsClosed()
    {

        SagaExtractionService service = CreateService();

        Guid sessionId = Guid.NewGuid();

        AttachmentMemoryProvenance provenance = CreateProvenance(
            sessionId,
            Guid.NewGuid());

        SagaExtractionRequest request = new(
            sessionId,
            [provenance],
            HadUnprovenancedAttachmentContent: false,
            AfterEntrySequenceExclusive: 2,
            ThroughEntrySequence: 4);

        service.EnqueueExtraction(request);

        service.EnqueueExtraction(
            request with
            {
                MaterializedAttachments =
                [
                    provenance with { ContentHash = "conflicting-content-hash" },
                ],
            });

        SagaExtractionRequest pending = Assert.Single(
            service.PendingSegmentsForTests(sessionId));

        Assert.Empty(pending.MaterializedAttachments);

        Assert.Equal(2, pending.AfterEntrySequenceExclusive);

        Assert.Equal(4, pending.ThroughEntrySequence);

    }

    [Fact]

    public void EnqueueExtraction_DeduplicatesPendingSessionAndKeepsIntervalProvenanceSeparate()
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
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 0,
                ThroughEntrySequence: 4));

        service.EnqueueExtraction(
            new SagaExtractionRequest(
                sessionId,
                [CreateProvenance(sessionId, secondAttachment)],
                HadUnprovenancedAttachmentContent: true,
                AfterEntrySequenceExclusive: 4,
                ThroughEntrySequence: 8));

        Assert.Collection(
            service.PendingSegmentsForTests(sessionId),
            first =>
            {

                Assert.Equal(0, first.AfterEntrySequenceExclusive);

                Assert.Equal(4, first.ThroughEntrySequence);

                Assert.Single(first.MaterializedAttachments);

                Assert.Equal(firstAttachment, first.MaterializedAttachments[0].AttachmentId);

                Assert.False(first.HadUnprovenancedAttachmentContent);

            },
            second =>
            {

                Assert.Equal(4, second.AfterEntrySequenceExclusive);

                Assert.Equal(8, second.ThroughEntrySequence);

                Assert.Single(second.MaterializedAttachments);

                Assert.Equal(secondAttachment, second.MaterializedAttachments[0].AttachmentId);

                Assert.True(second.HadUnprovenancedAttachmentContent);

            });

    }

    private SagaExtractionService CreateService() =>
        new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            _admissionGate,
            NullLogger<SagaExtractionService>.Instance);

    private async Task<SagaExtractionOutcome> ExtractWithLeaseAsync(
        SagaExtractionService service,
        IServiceProvider services,
        Guid sessionId,
        EmbeddingSettings embeddings,
        ArcanumSettings settings,
        CancellationToken cancellationToken)
    {

        Assert.True(_admissionGate.TryAcquireWorkLease(
            GrimoireWorkKind.SagaExtraction,
            out IGrimoireWorkLease? workLease));

        await using IGrimoireWorkLease lease = workLease!;

        long throughEntrySequence = await _db!.Entries
            .Where(entry => entry.SessionId == sessionId)
            .MaxAsync(entry => (long?)entry.Sequence, cancellationToken)
            .ConfigureAwait(false)
        ?? 0L;

        return await service.ExtractForSessionAsync(
            services,
            lease,
            new SagaExtractionRequest(
                sessionId,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: 0,
                ThroughEntrySequence: throughEntrySequence),
            embeddings,
            settings,
            cancellationToken);

    }

    private async Task<SagaExtractionOutcome> ExtractWithLeaseAsync(
        SagaExtractionService service,
        IServiceProvider services,
        SagaExtractionRequest request,
        EmbeddingSettings embeddings,
        ArcanumSettings settings,
        CancellationToken cancellationToken)
    {

        Assert.True(_admissionGate.TryAcquireWorkLease(
            GrimoireWorkKind.SagaExtraction,
            out IGrimoireWorkLease? workLease));

        await using IGrimoireWorkLease lease = workLease!;

        return await service.ExtractForSessionAsync(
            services,
            lease,
            request,
            embeddings,
            settings,
            cancellationToken);

    }

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
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            attachmentIndex: null,
            covenantKernel: null,
            FixtureOrdinaryConnectionFactory.For(_db!),
            FixtureLabeledArtifactGuard.For(_db!));

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

    private async Task<SagaExtractionCursor?> GetExtractionCursorAsync(Guid sessionId)
    {

        SagaMemoryStore store = CreateStore();

        return await store.GetExtractionCursorAsync(sessionId, CancellationToken.None);

    }

    private async Task WaitForWatermarkAsync(Guid sessionId, TimeSpan timeout)
    {

        using CancellationTokenSource timeoutCancellation = new(timeout);

        while (await GetWatermarkAsync(sessionId) is null)
        {

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeoutCancellation.Token);

        }

    }

    private static GrimoireConnectionAdmissionGate OpenGate() => new(TimeProvider.System);

    private static CovenantExclusiveRecoveryOwner Owner(byte seed) =>
        new(
            Guid.Parse($"00000000-0000-0000-0000-{seed:D12}"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat(seed, 32).ToArray()));

    private static IGrimoireClosingOwner BeginClosing(
        GrimoireConnectionAdmissionGate gate,
        byte seed)
    {

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(Owner(seed));

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        return begun.Value;

    }

    private static async Task<IGrimoireExclusiveClosedLease> CloseAsync(
        GrimoireConnectionAdmissionGate gate,
        IGrimoireClosingOwner closing)
    {

        Result drained = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(
            closing,
            CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        return closed.Value;

    }

    private static async Task ReopenAsync(IGrimoireExclusiveClosedLease closed)
    {

        Result completed = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None);

        Assert.True(completed.IsSuccess, completed.IsFailure ? completed.Error.Message : null);

        await closed.DisposeAsync();

    }

    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {

        private int _scopesCreated;

        internal int ScopesCreated => Volatile.Read(ref _scopesCreated);

        internal Action? OnScopeCreated { get; init; }

        internal Func<ValueTask>? OnScopeDisposed { get; init; }

        public IServiceScope CreateScope()
        {

            _ = Interlocked.Increment(ref _scopesCreated);

            IServiceScope scope = inner.CreateScope();

            OnScopeCreated?.Invoke();

            return new ObservingScope(scope, OnScopeDisposed);

        }

    }

    private sealed class ObservingScope(
        IServiceScope inner,
        Func<ValueTask>? onDisposed) : IServiceScope, IAsyncDisposable
    {

        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public void Dispose() => inner.Dispose();

        public async ValueTask DisposeAsync()
        {

            if (inner is IAsyncDisposable asyncInner)
            {

                await asyncInner.DisposeAsync();

            }
            else
            {

                inner.Dispose();

            }

            if (onDisposed is not null)
            {

                await onDisposed();

            }

        }

    }

    private sealed class RecordingAdmissionGate(
        IGrimoireConnectionAdmissionGate inner) : IGrimoireConnectionAdmissionGate
    {

        private readonly TaskCompletionSource _nextOpenWaitStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _workLeaseAttempts;

        private int _effectGroupAttempts;

        private int _effectGroupsAdmitted;

        private int _nextOpenWaitCalls;

        internal int WorkLeaseAttempts => Volatile.Read(ref _workLeaseAttempts);

        internal int EffectGroupAttempts => Volatile.Read(ref _effectGroupAttempts);

        internal int EffectGroupsAdmitted => Volatile.Read(ref _effectGroupsAdmitted);

        internal int NextOpenWaitCalls => Volatile.Read(ref _nextOpenWaitCalls);

        internal bool WorkLeaseIsHeld => _workLease is { IsHeld: true };

        internal List<GrimoireWorkKind> RequestedWorkKinds { get; } = [];

        internal Task NextOpenWaitStarted => _nextOpenWaitStarted.Task;

        internal Func<ValueTask>? OnEffectGroupDisposing { get; set; }

        private RecordingWorkLease? _workLease;

        public long CurrentGeneration => inner.CurrentGeneration;

        public bool TryAcquireRequestLease(
            GrimoireRequestKind kind,
            out IGrimoireRequestLease? lease) =>
            inner.TryAcquireRequestLease(kind, out lease);

        public bool TryAcquireWorkLease(
            GrimoireWorkKind kind,
            out IGrimoireWorkLease? lease)
        {

            _ = Interlocked.Increment(ref _workLeaseAttempts);

            RequestedWorkKinds.Add(kind);

            if (!inner.TryAcquireWorkLease(kind, out IGrimoireWorkLease? admitted))
            {

                lease = null;

                return false;

            }

            _workLease = new RecordingWorkLease(
                admitted!,
                () => _ = Interlocked.Increment(ref _effectGroupAttempts),
                () => _ = Interlocked.Increment(ref _effectGroupsAdmitted),
                OnEffectGroupDisposing ?? (static () => ValueTask.CompletedTask));

            lease = _workLease;

            return true;

        }

        public IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection) =>
            inner.AcquireOrdinaryOpen(connection);

        public Result<IGrimoireClosingOwner> BeginOrResumeExclusive(
            CovenantExclusiveRecoveryOwner owner,
            IGrimoireRequestLease? initiatingRequest = null,
            DbConnection? scopedConnection = null) =>
            inner.BeginOrResumeExclusive(owner, initiatingRequest, scopedConnection);

        public ValueTask<Result> DrainRequestAndWorkAsync(
            IGrimoireClosingOwner closingOwner,
            CancellationToken cancellationToken) =>
            inner.DrainRequestAndWorkAsync(closingOwner, cancellationToken);

        public ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(
            IGrimoireClosingOwner closingOwner,
            CancellationToken cancellationToken) =>
            inner.CloseConnectionAdmissionAsync(closingOwner, cancellationToken);

        public ValueTask<Result> AbortClosingAsync(
            IGrimoireClosingOwner closingOwner,
            Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync,
            CancellationToken cancellationToken) =>
            inner.AbortClosingAsync(
                closingOwner,
                proveNoDestructiveEffectAsync,
                cancellationToken);

        public Task<long> WaitForNextOpenGenerationAsync(
            long observedGeneration,
            CancellationToken cancellationToken)
        {

            _ = Interlocked.Increment(ref _nextOpenWaitCalls);

            _nextOpenWaitStarted.TrySetResult();

            return inner.WaitForNextOpenGenerationAsync(
                observedGeneration,
                cancellationToken);

        }

        public ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>>
            AcquireExpiredLeaseAdoptionInterlockAsync(
                CovenantExclusiveRecoveryOwner candidateOwner,
                Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>>
                    revalidateDurableOwnerAsync,
                CancellationToken cancellationToken) =>
            inner.AcquireExpiredLeaseAdoptionInterlockAsync(
                candidateOwner,
                revalidateDurableOwnerAsync,
                cancellationToken);

    }

    private sealed class RecordingWorkLease(
        IGrimoireWorkLease inner,
        Action onEffectGroupAttempt,
        Action onEffectGroupAdmitted,
        Func<ValueTask> onEffectGroupDisposing) : IGrimoireWorkLease
    {

        private int _disposed;

        internal bool IsHeld => Volatile.Read(ref _disposed) == 0;

        public GrimoireWorkKind Kind => inner.Kind;

        public long Generation => inner.Generation;

        public CancellationToken MaintenanceRevocation => inner.MaintenanceRevocation;

        public bool TryBeginExternalEffectGroup(
            out IGrimoireExternalEffectGroup? effectGroup)
        {

            onEffectGroupAttempt();

            if (!inner.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? admitted))
            {

                effectGroup = null;

                return false;

            }

            onEffectGroupAdmitted();

            effectGroup = new ObservingExternalEffectGroup(
                admitted!,
                onEffectGroupDisposing);

            return true;

        }

        public async ValueTask DisposeAsync()
        {

            await inner.DisposeAsync();

            _ = Interlocked.Exchange(ref _disposed, 1);

        }

    }

    private sealed class ObservingExternalEffectGroup(
        IGrimoireExternalEffectGroup inner,
        Func<ValueTask> onDisposing) : IGrimoireExternalEffectGroup
    {

        public async ValueTask DisposeAsync()
        {

            try
            {

                await onDisposing();

            }
            finally
            {

                await inner.DisposeAsync();

            }

        }

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        private int _embedCallCount;

        private int _availabilityCheckCount;

        public bool Available { get; set; } = true;

        public bool EmbedShouldFail { get; set; }

        public int EmbedCallCount => Volatile.Read(ref _embedCallCount);

        public int AvailabilityCheckCount => Volatile.Read(ref _availabilityCheckCount);

        internal Func<int, Task>? OnEmbedAsync { get; init; }

        internal Func<int, bool>? AvailabilityForCheck { get; init; }

        public bool IsAvailable
        {

            get
            {

                int checkCount = Interlocked.Increment(ref _availabilityCheckCount);

                return AvailabilityForCheck?.Invoke(checkCount) ?? Available;

            }

        }

        public async Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {

            int callCount = Interlocked.Increment(ref _embedCallCount);

            if (OnEmbedAsync is not null)
            {

                await OnEmbedAsync(callCount);

            }

            if (EmbedShouldFail)
            {

                return Result<Embedding<float>>.Failure(new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated embedding failure."));

            }

            return Result<Embedding<float>>.Success(new Embedding<float>(Vec(1f)));

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

        internal Func<int, string>? TextForCall { get; init; }

        public Error? NextFailure { get; set; }

        public int ExpectedCallCount { get; init; }

        public int CallCount => Volatile.Read(ref _callCount);

        public string LastStatelessUserContent { get; private set; } = string.Empty;

        public List<string> StatelessUserContents { get; } = [];

        internal Action<int>? OnCall { get; init; }

        internal Func<int, CancellationToken, Task>? BeforeResultAsync { get; init; }

        /// <summary>Signaled the moment a call enters <see cref="ExecutePromptAsync"/>, before <see cref="Gate"/> is awaited. Null unless a test needs to observe a call as in-flight.</summary>
        public TaskCompletionSource<bool>? Entered { get; set; }

        /// <summary>When set, held calls block here until the test completes it, so a test can inspect state while an extraction is in flight.</summary>
        public TaskCompletionSource<bool>? Gate { get; set; }

        public async Task<Result<PromptTurnResult>> ExecutePromptAsync(PingRequest request, ArcanumInvocationContext invocationContext, CancellationToken cancellationToken, InferenceAuditContext? auditContext = null)
        {

            int callCount = Interlocked.Increment(ref _callCount);

            LastStatelessUserContent = request.StatelessMessages?
                .LastOrDefault(static m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?
                .Content ?? string.Empty;

            StatelessUserContents.Add(LastStatelessUserContent);

            OnCall?.Invoke(callCount);

            if (ExpectedCallCount > 0 && callCount >= ExpectedCallCount)
            {

                _expectedCallsReached.TrySetResult(true);

            }

            Entered?.TrySetResult(true);

            if (Gate is { } gate)
            {

                // Honors the caller's token: a test can hold a call open and prove that cancelling
                // stoppingToken (e.g. via StopAsync) unblocks it with OperationCanceledException,
                // rather than needing to release Gate itself.
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            }

            if (BeforeResultAsync is not null)
            {

                await BeforeResultAsync(callCount, cancellationToken).ConfigureAwait(false);

            }

            if (NextFailure is { } failure)
            {

                return Result<PromptTurnResult>.Failure(failure);

            }

            string text = TextForCall?.Invoke(callCount) ?? NextText;

            return Result<PromptTurnResult>.Success(new PromptTurnResult(text, null));

        }

        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(PingRequest request, ArcanumInvocationContext invocationContext, CancellationToken cancellationToken, InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException("SagaExtractionService only calls ExecutePromptAsync.");

        public Task WaitForExpectedCallsAsync(TimeSpan timeout) =>
            _expectedCallsReached.Task.WaitAsync(timeout);

    }

}
