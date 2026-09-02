using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

[Collection("Grimoire")]
public sealed class GrimoireRepositoryTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public GrimoireRepositoryTests(GrimoireFixture fixture)
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
    public async Task CommitTurnAsync_retains_read_write_admission_through_its_transaction()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FixtureOrdinaryConnectionFactory connections = new();

        GrimoireRepository repository = CreateRepository(connections: connections);

        (Guid sessionId, Guid assistantEntryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "What is the ward sigil?",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        TurnCommitRequest request = new(
            assistantEntryId,
            sessionId,
            AssistantFinalizationOutcome.Committed,
            "The sigil is cobalt.",
            CovenantTask6Fixture.D(31),
            ContentSensitivity.None,
            GenerationProvenance.CreateExact([]));

        {

            using ScopedConsumerPause pause = new("GrimoireRepository.CommitWithinImmediateTransactionAsync");

            Task<Result<TurnCommitReceipt>> committing = repository.CommitTurnAsync(
                request,
                CancellationToken.None);

            try
            {

                await pause.WaitUntilEnteredAsync();

                Assert.Equal(GrimoireScopedConsumerFinalUseKind.TransactionCommitted, pause.FinalUse.Kind);

                Assert.Equal((int)AssistantFinalizationOutcome.Committed, pause.FinalUse.Observation);

                Assert.Equal(1, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadWrite));

                await using ArcanumDbContext observer = _fixture.CreateContext(_dbPath);

                string persisted = await observer.Entries
                    .AsNoTracking()
                    .Where(entry => entry.Id == assistantEntryId)
                    .Select(static entry => entry.Content)
                    .SingleAsync(CancellationToken.None);

                Assert.Equal("The sigil is cobalt.", persisted);

            }
            finally
            {

                pause.Release();

                _ = await committing.WaitAsync(TimeSpan.FromSeconds(10));

            }

            Result<TurnCommitReceipt> committed = await committing;

            Assert.True(committed.IsSuccess, committed.Error.Message);

            Assert.Equal(0, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadWrite));

        }

        using ScopedConsumerPause replayPause = new("GrimoireRepository.CommitWithinImmediateTransactionAsync");

        Task<Result<TurnCommitReceipt>> replaying = repository.CommitTurnAsync(
            request,
            CancellationToken.None);

        try
        {

            await replayPause.WaitUntilEnteredAsync();

            Assert.Equal(GrimoireScopedConsumerFinalUseKind.TransactionRolledBack, replayPause.FinalUse.Kind);

            Assert.Equal((int)AssistantFinalizationOutcome.Committed, replayPause.FinalUse.Observation);

            Assert.Equal(1, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadWrite));

        }
        finally
        {

            replayPause.Release();

            _ = await replaying.WaitAsync(TimeSpan.FromSeconds(10));

        }

        Result<TurnCommitReceipt> replayed = await replaying;

        Assert.True(replayed.IsSuccess, replayed.Error.Message);

        Assert.True(replayed.Value.Replayed);

        Assert.Equal(0, connections.LiveLeaseCountFor(CovenantSqliteConnectionMode.ReadWrite));

    }

    [SkippableFact]
    public async Task BeginAssistantReplyAsync_and_FinalizeAssistantEntryAsync_persist_exchange()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, Guid assistantEntryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "What is the ward sigil?",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await repository.FinalizeAssistantEntryAsync(
            assistantEntryId,
            "The sigil is cobalt.",
            CancellationToken.None);

        GrimoireEntryDto? assistantEntry = await repository.GetEntryByIdAsync(
            sessionId,
            assistantEntryId,
            CancellationToken.None);

        Assert.NotNull(assistantEntry);

        Assert.Equal("The sigil is cobalt.", assistantEntry!.Content);

        Assert.True(await repository.SessionExistsAsync(sessionId, CancellationToken.None));

    }

    [SkippableFact]
    public async Task AppendToolInteractionAsync_persists_an_exact_recallable_pair()
    {
        const string toolName = "execute_command";
        const string arguments = """{"command":"dotnet --version"}""";
        const string result = "10.0.0";
        const string model = "test-model";

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, Guid assistantEntryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "recall the command result",
            model,
            CancellationToken.None);

        await repository.FinalizeAssistantEntryAsync(
            assistantEntryId,
            "I will run the command.",
            CancellationToken.None);

        await repository.AppendToolInteractionAsync(
            sessionId,
            toolName,
            arguments,
            result,
            model,
            CancellationToken.None);

        List<Entry> entries = await _db!.Entries
            .AsNoTracking()
            .Where(entry => entry.SessionId == sessionId)
            .OrderBy(entry => entry.Sequence)
            .ToListAsync(CancellationToken.None);

        Entry toolCall = entries[^2];
        Entry toolResult = entries[^1];

        Assert.Equal(toolCall.Sequence + 1, toolResult.Sequence);
        Assert.Equal(MessageRole.Assistant, toolCall.Role);
        Assert.Equal("""[ToolCall: execute_command({"command":"dotnet --version"})]""", toolCall.Content);
        Assert.Equal(toolName, toolCall.ToolName);
        Assert.Equal(arguments, toolCall.ToolArguments);
        Assert.Equal(model, toolCall.ModelUsed);
        Assert.Equal(MessageRole.System, toolResult.Role);
        Assert.Equal("[ToolResult: 10.0.0]", toolResult.Content);
        Assert.Null(toolResult.ToolCallId);
        Assert.Null(toolResult.ToolName);
        Assert.Null(toolResult.ToolArguments);
        Assert.Equal(model, toolResult.ModelUsed);

    }

    [SkippableFact]
    public async Task DiscardAssistantEntryAsync_removes_empty_placeholder_without_user_row()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, Guid assistantEntryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "Interrupted turn",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await repository.DiscardAssistantEntryAsync(assistantEntryId, CancellationToken.None);

        GrimoireEntryDto? assistantEntry = await repository.GetEntryByIdAsync(
            sessionId,
            assistantEntryId,
            CancellationToken.None);

        Assert.Null(assistantEntry);

        List<GrimoireEntryDto>? entries = await repository.GetSessionEntriesAsync(sessionId, CancellationToken.None);

        Assert.NotNull(entries);

        Assert.Single(entries!);

        Assert.Equal(MessageRole.User, entries![0].Role);

    }

    [SkippableFact]
    public async Task ScribeLoreAsync_ReadLoreAsync_and_DeleteLoreAsync_manage_lore()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        LoreDto written = await repository.ScribeLoreAsync("ward.color", "cobalt", CancellationToken.None);

        Assert.Equal("cobalt", written.Value);

        string? read = await repository.ReadLoreAsync("ward.color", CancellationToken.None);

        Assert.Equal("cobalt", read);

        LoreDto? fetched = await repository.GetLoreAsync("ward.color", CancellationToken.None);

        Assert.NotNull(fetched);

        bool deleted = await repository.DeleteLoreAsync("ward.color", CancellationToken.None);

        Assert.True(deleted);

        Assert.Null(await repository.GetLoreAsync("ward.color", CancellationToken.None));

    }

    [SkippableFact]
    public async Task GetLoreAsync_and_ListLoreAsync_return_UpdatedAtUtc_marked_as_Utc()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        LoreDto written = await repository.ScribeLoreAsync("ward.kind", "cobalt", CancellationToken.None);

        Assert.Equal(DateTimeKind.Utc, written.UpdatedAtUtc.Kind);

        LoreDto? fetched = await repository.GetLoreAsync("ward.kind", CancellationToken.None);

        Assert.NotNull(fetched);

        Assert.Equal(DateTimeKind.Utc, fetched!.UpdatedAtUtc.Kind);

        ListPageResult<LoreDto> page = await repository.ListLoreAsync(cancellationToken: CancellationToken.None);

        LoreDto listed = page.Items.Single(item => item.Key == "ward.kind");

        Assert.Equal(DateTimeKind.Utc, listed.UpdatedAtUtc.Kind);

        string json = JsonSerializer.Serialize(fetched, ArcanumJsonContext.Default.LoreDto);

        Assert.Contains("Z\"", json, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task SaveCompletedExchangeAsync_and_PurgeSessionAsync_round_trip()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        await repository.SaveCompletedExchangeAsync(
            "Find the moonstone archive",
            "The archive is beneath the observatory.",
            "test-model",
            CancellationToken.None);

        Session? session = await _db!.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Title == "Find the moonstone archive", CancellationToken.None);

        Assert.NotNull(session);

        Assert.True(await repository.SessionExistsAsync(session!.Id, CancellationToken.None));

        int removed = await repository.PurgeSessionAsync(session.Id, CancellationToken.None);

        Assert.Equal(1, removed);

        Assert.False(await repository.SessionExistsAsync(session.Id, CancellationToken.None));

    }

    [SkippableFact]
    public async Task PurgeSessionAsync_removes_only_the_sessions_entry_embeddings()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid purgedSessionId, Guid purgedEntryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "purge embedded entry",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        (Guid retainedSessionId, Guid retainedEntryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "retain embedded entry",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await EnsureEntryEmbeddingTablesAsync();

        await InsertEntryEmbeddingAsync(purgedEntryId);

        await InsertEntryEmbeddingAsync(retainedEntryId);

        Assert.Equal(1, await CountEntryEmbeddingAsync("entry_embeddings", purgedEntryId));

        Assert.Equal(1, await CountEntryEmbeddingAsync("entry_embeddings_vec", purgedEntryId));

        Assert.Equal(1, await repository.PurgeSessionAsync(purgedSessionId, CancellationToken.None));

        Assert.Equal(0, await CountEntryEmbeddingAsync("entry_embeddings", purgedEntryId));

        Assert.Equal(0, await CountEntryEmbeddingAsync("entry_embeddings_vec", purgedEntryId));

        Assert.Equal(1, await CountEntryEmbeddingAsync("entry_embeddings", retainedEntryId));

        Assert.Equal(1, await CountEntryEmbeddingAsync("entry_embeddings_vec", retainedEntryId));

        Assert.True(await repository.SessionExistsAsync(retainedSessionId, CancellationToken.None));

    }

    [SkippableFact]
    public async Task IncrementSessionTokensAsync_updates_total()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, _) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "token count",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await repository.IncrementSessionTokensAsync(sessionId, 42, CancellationToken.None);

        Session? session = await _db!.Sessions.AsNoTracking().FirstAsync(s => s.Id == sessionId, CancellationToken.None);

        Assert.Equal(42, session.TotalTokensUsed);

    }

    [SkippableFact]
    public async Task IncrementSessionTokensAndCostAsync_updates_total_and_cost()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, _) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "token and cost",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await repository.IncrementSessionTokensAndCostAsync(sessionId, 42, 1.23m, CancellationToken.None);

        Session? session = await _db!.Sessions.AsNoTracking().FirstAsync(s => s.Id == sessionId, CancellationToken.None);

        Assert.Equal(42, session.TotalTokensUsed);

        Assert.Equal(1.23m, session.TotalCostUsd);

    }

    [SkippableFact]
    public async Task GetTodaySpendAsync_sums_sessions_created_today()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId1, _) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "session 1",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        (Guid sessionId2, _) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "session 2",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await repository.IncrementSessionTokensAndCostAsync(sessionId1, 10, 2.00m, CancellationToken.None);

        await repository.IncrementSessionTokensAndCostAsync(sessionId2, 20, 3.50m, CancellationToken.None);

        decimal todaySpend = await repository.GetTodaySpendAsync(CancellationToken.None);

        Assert.Equal(5.50m, todaySpend);

    }

    /// <summary>
    /// The new-session branch committed the Session and its two Entries, then bumped the counter in a
    /// second, untransacted statement. A crash between the two left the session durable with
    /// UnsummarizedEntryCount = 0, so GetSessionsNeedingSummarizationAsync — which selects purely on
    /// that counter — never picked it up and the Campaign Logger silently never summarized it.
    /// Seeding the counter on the inserted row puts both in one SQLite transaction, which the change
    /// tracker (never refreshed from the database) observes; ExecuteUpdateAsync bypasses it.
    /// </summary>
    [SkippableFact]
    public async Task New_session_reply_writes_the_unsummarized_counter_in_the_insert_transaction()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, _) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "atomic counter test",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        Session tracked = _db!.ChangeTracker
            .Entries<Session>()
            .Select(entry => entry.Entity)
            .Single(session => session.Id == sessionId);

        Assert.Equal(2, tracked.UnsummarizedEntryCount);

        Session persisted = await _db.Sessions
            .AsNoTracking()
            .FirstAsync(session => session.Id == sessionId, CancellationToken.None);

        Assert.Equal(2, persisted.UnsummarizedEntryCount);

    }

    [SkippableFact]
    public async Task UnsummarizedEntryCount_increments_on_begin_and_resets_on_rollup()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, Guid assistantEntryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "perf counter test",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await repository.FinalizeAssistantEntryAsync(assistantEntryId, "done", CancellationToken.None);

        Session? afterBegin = await _db!.Sessions.AsNoTracking().FirstAsync(s => s.Id == sessionId, CancellationToken.None);

        Assert.Equal(2, afterBegin!.UnsummarizedEntryCount);

        await repository.AppendToolInteractionAsync(
            sessionId,
            "tool",
            "{}",
            "ok",
            "test-model",
            CancellationToken.None);

        Session? afterTool = await _db.Sessions.AsNoTracking().FirstAsync(s => s.Id == sessionId, CancellationToken.None);

        Assert.Equal(4, afterTool!.UnsummarizedEntryCount);

        Entry lastEntry = (await _db.Entries
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .ToListAsync(CancellationToken.None))
            .OrderByDescending(e => e.CreatedAt)
            .First();

        await repository.UpdateSessionCampaignRollupAsync(
            sessionId,
            "summary",
            lastEntry.CreatedAt.UtcDateTime,
            CancellationToken.None);

        Session? afterRollup = await _db.Sessions.AsNoTracking().FirstAsync(s => s.Id == sessionId, CancellationToken.None);

        Assert.Equal(0, afterRollup!.UnsummarizedEntryCount);

    }

    [SkippableFact]
    public async Task GetSessionsNeedingSummarizationAsync_returns_all_candidates_in_updated_order()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const int candidateCount = 101;

        DateTimeOffset baseline = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        Guid[] expected = new Guid[candidateCount];

        for (int i = candidateCount - 1; i >= 0; i--)
        {

            Guid id = Guid.NewGuid();

            expected[i] = id;

            DateTimeOffset timestamp = baseline.AddMinutes(i);

            _db!.Sessions.Add(new Session
            {
                Id = id,
                Title = $"candidate-{i}",
                Status = "active",
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                UnsummarizedEntryCount = 26,
            });

        }

        await _db!.SaveChangesAsync(CancellationToken.None);

        GrimoireRepository repository = CreateRepository();

        List<Guid> result = await repository.GetSessionsNeedingSummarizationAsync(
            threshold: 25,
            idleCutoff: baseline.AddDays(-1).UtcDateTime,
            CancellationToken.None);

        Assert.Equal(candidateCount, result.Count);

        Assert.Equal(expected, result);

    }

    [SkippableFact]
    public async Task GetUnsummarizedEntriesAsync_pages_a_long_session_window()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTime watermark = new(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);

        Guid sessionId = Guid.NewGuid();

        _db!.Sessions.Add(new Session
        {
            Id = sessionId,
            Title = "wide unsummarized window",
            Status = "active",
            CreatedAt = new DateTimeOffset(watermark, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(watermark.AddMinutes(60), TimeSpan.Zero),
            LastSummarizedMessageAt = watermark,
            UnsummarizedEntryCount = 60,
        });

        for (int i = 1; i <= 60; i++)
        {

            _db.Entries.Add(new Entry
            {
                Id = EntryId(i),
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = $"entry-{i}",
                ModelUsed = "test-model",
                CreatedAt = new DateTimeOffset(watermark.AddMinutes(i), TimeSpan.Zero),
                Sequence = i,
            });

        }

        await _db.SaveChangesAsync(CancellationToken.None);

        GrimoireRepository repository = CreateRepository();

        List<Entry> result = await repository.GetUnsummarizedEntriesAsync(
            sessionId,
            watermark,
            batchSize: 50,
            CancellationToken.None);

        Assert.Equal(50, result.Count);

        Assert.Equal(
            Enumerable.Range(1, 50).Select(EntryId),
            result.Select(entry => entry.Id));

    }

    [SkippableFact]
    public async Task GetUnsummarizedEntriesAsync_keeps_tied_tool_pair_and_rollup_count_correct()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTime watermark = new(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc);

        Guid sessionId = Guid.NewGuid();

        _db!.Sessions.Add(new Session
        {
            Id = sessionId,
            Title = "tied tool pair",
            Status = "active",
            CreatedAt = new DateTimeOffset(watermark, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(watermark.AddMinutes(25), TimeSpan.Zero),
            LastSummarizedMessageAt = watermark,
            UnsummarizedEntryCount = 26,
        });

        for (int i = 1; i <= 24; i++)
        {

            _db.Entries.Add(new Entry
            {
                Id = EntryId(i),
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = $"entry-{i}",
                ModelUsed = "test-model",
                CreatedAt = new DateTimeOffset(watermark.AddMinutes(i), TimeSpan.Zero),
                Sequence = i,
            });

        }

        DateTimeOffset tiedTimestamp =
            new(watermark.AddMinutes(25), TimeSpan.Zero);

        Guid toolCallId = EntryId(25);

        Guid toolResultId = EntryId(26);

        _db.Entries.Add(new Entry
        {
            Id = toolCallId,
            SessionId = sessionId,
            Role = MessageRole.Assistant,
            Content = "[ToolCall: inspect({})]",
            ModelUsed = "test-model",
            CreatedAt = tiedTimestamp,
            Sequence = 25,
            ToolCallId = "call-25",
            ToolName = "inspect",
            ToolArguments = "{}",
        });

        _db.Entries.Add(new Entry
        {
            Id = toolResultId,
            SessionId = sessionId,
            Role = MessageRole.Tool,
            Content = "[ToolResult: ok]",
            ModelUsed = "test-model",
            CreatedAt = tiedTimestamp,
            Sequence = 26,
            ToolCallId = "call-25",
        });

        await _db.SaveChangesAsync(CancellationToken.None);

        GrimoireRepository repository = CreateRepository();

        List<Entry> result = await repository.GetUnsummarizedEntriesAsync(
            sessionId,
            watermark,
            batchSize: 25,
            CancellationToken.None);

        Assert.Equal(26, result.Count);

        Assert.Equal(toolCallId, result[^2].Id);

        Assert.Equal(toolResultId, result[^1].Id);

        await repository.UpdateSessionCampaignRollupAsync(
            sessionId,
            "summary through complete tool pair",
            tiedTimestamp.UtcDateTime,
            CancellationToken.None);

        Session updated = await _db.Sessions
            .AsNoTracking()
            .SingleAsync(session => session.Id == sessionId, CancellationToken.None);

        Assert.Equal(0, updated.UnsummarizedEntryCount);

        Assert.Equal(tiedTimestamp.UtcDateTime, updated.LastSummarizedMessageAt);

    }

    [SkippableFact]
    public async Task GetUnsummarizedEntriesAsync_returns_one_checkpoint_page()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const int entryCeiling = 100;

        DateTime watermark = new(2026, 7, 4, 0, 0, 0, DateTimeKind.Utc);

        Guid sessionId = await SeedUnsummarizedWindowAsync(
            "exact entry ceiling",
            watermark,
            entryCeiling);

        GrimoireRepository repository = CreateRepository();

        List<Entry> result = await repository.GetUnsummarizedEntriesAsync(
            sessionId,
            watermark,
            batchSize: 25,
            CancellationToken.None);

        Assert.Equal(25, result.Count);

        Assert.Equal(
            Enumerable.Range(1, 25).Select(EntryId),
            result.Select(entry => entry.Id));

    }

    [SkippableFact]
    public async Task GetUnsummarizedEntriesAsync_more_than_former_ceiling_is_checkpointed()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const int entryCeiling = 100;

        DateTime watermark = new(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc);

        Guid sessionId = await SeedUnsummarizedWindowAsync(
            "legacy overflow",
            watermark,
            entryCeiling + 1);

        GrimoireRepository repository = CreateRepository();

        List<Entry> result = await repository.GetUnsummarizedEntriesAsync(
            sessionId,
            watermark,
            batchSize: 25,
            CancellationToken.None);

        Assert.Equal(25, result.Count);

        Session unchanged = await _db!.Sessions
            .AsNoTracking()
            .SingleAsync(session => session.Id == sessionId, CancellationToken.None);

        Assert.Equal(watermark, unchanged.LastSummarizedMessageAt);

        Assert.Equal(entryCeiling + 1, unchanged.UnsummarizedEntryCount);

    }

    [SkippableFact]
    public async Task GetSessionsNeedingSummarizationAsync_legacy_backfill_recomputes_under_session_write_lock()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTime watermark = new(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);

        Guid sessionId = await SeedUnsummarizedWindowAsync(
            "legacy counter backfill",
            watermark,
            entryCount: 3,
            unsummarizedEntryCount: -1);

        GrimoireRepository repository = CreateRepository();

        bool lockWasHeld = false;

        repository.AfterLegacyBackfillCountedForTesting = (observedSessionId, _) =>
        {
            if (observedSessionId == sessionId)
            {
                lockWasHeld = SessionWriteLock.IsHeldForTesting(sessionId);
            }

            return ValueTask.CompletedTask;
        };

        List<Guid> candidates = await repository.GetSessionsNeedingSummarizationAsync(
            threshold: 2,
            idleCutoff: watermark.AddDays(-1),
            CancellationToken.None);

        Session updated = await _db!.Sessions
            .AsNoTracking()
            .SingleAsync(session => session.Id == sessionId, CancellationToken.None);

        Assert.True(lockWasHeld);
        Assert.Equal(3, updated.UnsummarizedEntryCount);
        Assert.Contains(sessionId, candidates);

    }

    [SkippableFact]
    public async Task UpdateSessionCampaignRollupAsync_serializes_concurrent_append_and_preserves_remaining_count()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTime watermark = new(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc);

        Guid sessionId = await SeedUnsummarizedWindowAsync(
            "concurrent rollup append",
            watermark,
            entryCount: 1);

        GrimoireRepository rollupRepository = CreateRepository();

        await using ArcanumDbContext appendDb = _fixture.CreateContext(_dbPath);

        GrimoireRepository appendRepository = CreateRepository(appendDb);

        TaskCompletionSource rollupCounted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseRollup = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool lockWasHeld = false;

        rollupRepository.AfterRollupRemainingCountedForTesting = async (observedSessionId, _) =>
        {
            if (observedSessionId != sessionId)
            {
                return;
            }

            lockWasHeld = SessionWriteLock.IsHeldForTesting(sessionId);

            rollupCounted.SetResult();

            await releaseRollup.Task.ConfigureAwait(false);
        };

        Task rollupTask = rollupRepository.UpdateSessionCampaignRollupAsync(
            sessionId,
            "summary through initial entry",
            watermark.AddMinutes(1),
            CancellationToken.None);

        await rollupCounted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        TaskCompletionSource appendAttemptedLock =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        SessionWriteLock.BeforeAcquireForTesting = observedSessionId =>
        {
            if (observedSessionId == sessionId)
            {
                appendAttemptedLock.SetResult();
            }
        };

        try
        {
            Task appendTask = appendRepository.AppendToolInteractionAsync(
                sessionId,
                "inspect",
                "{}",
                "ok",
                "test-model",
                CancellationToken.None);

            await appendAttemptedLock.Task.WaitAsync(TimeSpan.FromSeconds(10));

            releaseRollup.SetResult();

            await Task.WhenAll(rollupTask, appendTask);
        }
        finally
        {
            SessionWriteLock.BeforeAcquireForTesting = null;

            releaseRollup.TrySetResult();
        }

        await using ArcanumDbContext verificationDb = _fixture.CreateContext(_dbPath);

        Session updated = await verificationDb.Sessions
            .AsNoTracking()
            .SingleAsync(session => session.Id == sessionId, CancellationToken.None);

        int actualRemaining = await EntryTemporalQueries
            .CountAfter(
                verificationDb,
                sessionId,
                new DateTimeOffset(watermark.AddMinutes(1), TimeSpan.Zero))
            .FirstAsync(CancellationToken.None);

        Assert.True(lockWasHeld);
        Assert.Equal(2, actualRemaining);
        Assert.Equal(actualRemaining, updated.UnsummarizedEntryCount);

    }

    [SkippableFact]
    public async Task GetSessionHeaderAsync_returns_session_without_entries()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, _) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "header only",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        Session? header = await repository.GetSessionHeaderAsync(sessionId, CancellationToken.None);

        Assert.NotNull(header);

        Assert.Empty(header!.Entries);

        Session? full = await repository.GetSessionAsync(sessionId, CancellationToken.None);

        Assert.NotNull(full);

        Assert.NotEmpty(full!.Entries);

    }

    [SkippableFact]
    public async Task GetSessionAsync_loads_every_post_watermark_entry_even_beyond_max_messages()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        int maxMessages = ArcanumSettingClamps.MaxMessagesPerConversationLoad(50);

        DateTime watermark = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Guid sessionId = Guid.NewGuid();

        _db!.Sessions.Add(new Session
        {
            Id = sessionId,
            CreatedAt = new DateTimeOffset(watermark.AddMinutes(-30), TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(watermark.AddMinutes(maxMessages + 10), TimeSpan.Zero),
            Status = "active",
            Title = "watermark anchor",
            Summary = "Prior turns up to the watermark were summarized.",
            LastSummarizedMessageAt = watermark,
        });

        for (int i = 1; i <= 3; i++)
        {

            _db.Entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = $"before-watermark-{i}",
                ModelUsed = "test-model",
                CreatedAt = new DateTimeOffset(watermark.AddMinutes(-i), TimeSpan.Zero),
                // Seeded newest-first, so invert the loop index to keep sequence chronological.
                Sequence = 4 - i,
            });

        }

        int postWatermarkCount = maxMessages + 10;

        List<Guid> postWatermarkIds = new();

        for (int i = 1; i <= postWatermarkCount; i++)
        {

            Guid entryId = Guid.NewGuid();

            postWatermarkIds.Add(entryId);

            _db.Entries.Add(new Entry
            {
                Id = entryId,
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = $"after-watermark-{i}",
                ModelUsed = "test-model",
                CreatedAt = new DateTimeOffset(watermark.AddMinutes(i), TimeSpan.Zero),
                Sequence = 3 + i,
            });

        }

        await _db.SaveChangesAsync(CancellationToken.None);

        Session? session = await repository.GetSessionAsync(sessionId, CancellationToken.None);

        Assert.NotNull(session);

        HashSet<Guid> loadedIds = session!.Entries.Select(e => e.Id).ToHashSet();

        Guid[] dropped = postWatermarkIds.Where(id => !loadedIds.Contains(id)).ToArray();

        Assert.True(
            dropped.Length == 0,
            $"Expected all {postWatermarkCount} post-watermark entries to be loaded, but {dropped.Length} were dropped.");

    }

    // W3.4 Group D #8: SearchArchivesAsync builds a raw DbCommand over the EF connection but
    // never opens it. EF Core closes its connection after each SaveChanges, so a search issued
    // without a prior open query must still work. The sibling ResolveFtsSessionIdsAsync opens
    // the connection first; SearchArchivesAsync must do the same. Without the fix, the raw
    // ExecuteReaderAsync on a closed connection throws.
    [SkippableFact]
    public async Task SearchArchivesAsync_runs_on_a_cold_closed_connection()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, _) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "the cobalt sigil is glowing brightly",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        // SaveChanges above opens then closes the EF connection, so the connection is closed
        // here. SearchArchivesAsync must open it itself before ExecuteReaderAsync.
        string result = await repository.SearchArchivesAsync("cobalt", maxResults: 10, CancellationToken.None);

        Assert.Contains("cobalt", result, StringComparison.OrdinalIgnoreCase);

    }

    // A hundred maximum-size entry bodies concatenated into one string is hundreds of megabytes of
    // transient allocation that the caller's output cap then rejects wholesale. The repository has
    // to stop building at the cap rather than build first and be refused afterwards.
    [SkippableFact]
    public async Task SearchArchivesAsync_stops_building_at_the_tool_output_cap()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, _) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "zephyrine seed",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        string body = string.Concat(Enumerable.Repeat("zephyrine ", 20_000));

        for (int index = 0; index < 10; index++)
        {

            _ = _db!.Entries.Add(
                new Entry
                {

                    Id = Guid.NewGuid(),

                    SessionId = sessionId,

                    Role = MessageRole.Assistant,

                    Content = body,

                    ModelUsed = "test-model",

                    CreatedAt = DateTimeOffset.UtcNow.AddSeconds(index),

                    Sequence = 100 + index,

                    IsPinned = false,

                });

        }

        _ = await _db!.SaveChangesAsync(CancellationToken.None);

        string result = await repository.SearchArchivesAsync(
            "zephyrine",
            maxResults: 100,
            CancellationToken.None);

        long cap = ArcanumSettingClamps.ToolOutputCapBytes(
            new ArcanumSettings().ResolveIntelligence().ToolOutputCapBytes);

        Assert.Contains("[TRUNCATED:", result, StringComparison.Ordinal);

        Assert.True(
            result.Length <= cap + 4096,
            $"Archive search returned {result.Length} characters against a {cap}-byte cap.");

    }

    [SkippableFact]
    public async Task DeleteEntryAsync_removes_existing_entry()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, Guid assistantEntryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "delete me",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await repository.FinalizeAssistantEntryAsync(assistantEntryId, "delete me reply", CancellationToken.None);

        bool deleted = await repository.DeleteEntryAsync(sessionId, assistantEntryId, CancellationToken.None);

        Assert.True(deleted);

        Assert.Null(await repository.GetEntryByIdAsync(sessionId, assistantEntryId, CancellationToken.None));

    }

    [SkippableFact]
    public async Task DeleteEntryAsync_returns_false_when_entry_missing()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, _) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "session only",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        bool deleted = await repository.DeleteEntryAsync(sessionId, Guid.NewGuid(), CancellationToken.None);

        Assert.False(deleted);

    }

    /// <summary>
    /// W15-1: a compensating rollback must run on <see cref="CancellationToken.None"/>, not the
    /// caller's token. When the caller's token is already cancelled by the time the catch block's
    /// rollback runs, rolling back on that token throws a fresh <see cref="OperationCanceledException"/>
    /// before <c>throw;</c> can re-raise the original failure, so the caller never learns why the write
    /// actually failed.
    /// </summary>
    [SkippableFact]
    public async Task DeleteEntryAsync_surfaces_the_original_failure_when_the_token_cancels_before_rollback()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using CancellationTokenSource cts = new();

        InvalidOperationException synthetic = new("synthetic clear failure for the RED test");

        NoOpSessionAttachmentStore attachments = new(
            clearEntryIds: (_, _, _) =>
            {

                // Cancels exactly where BeginTransactionAsync has already succeeded and the write
                // has not yet committed, matching the finding's own interleaving.
                cts.Cancel();

                return Task.FromException(synthetic);

            });

        GrimoireRepository repository = CreateRepository(attachments: attachments);

        (Guid sessionId, Guid assistantEntryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "delete me under cancellation",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await repository.FinalizeAssistantEntryAsync(
            assistantEntryId,
            "delete me under cancellation reply",
            CancellationToken.None);

        InvalidOperationException observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.DeleteEntryAsync(sessionId, assistantEntryId, cts.Token));

        Assert.Same(synthetic, observed);

        // The rollback actually ran (rather than being skipped by a cancelled token), so the entry
        // is still there and the connection is left with no open transaction to trip a later call.
        Assert.NotNull(
            await repository.GetEntryByIdAsync(sessionId, assistantEntryId, CancellationToken.None));

    }

    [SkippableFact]
    public async Task SetEntryPinnedAsync_toggles_pinned_flag()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, Guid assistantEntryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "pin me",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await repository.FinalizeAssistantEntryAsync(assistantEntryId, "pin me reply", CancellationToken.None);

        GrimoireEntryDto? before = await repository.GetEntryByIdAsync(sessionId, assistantEntryId, CancellationToken.None);

        Assert.NotNull(before);

        Assert.False(before!.IsPinned);

        bool pinned = await repository.SetEntryPinnedAsync(sessionId, assistantEntryId, true, CancellationToken.None);

        Assert.True(pinned);

        GrimoireEntryDto? after = await repository.GetEntryByIdAsync(sessionId, assistantEntryId, CancellationToken.None);

        Assert.NotNull(after);

        Assert.True(after!.IsPinned);

        bool unpinned = await repository.SetEntryPinnedAsync(sessionId, assistantEntryId, false, CancellationToken.None);

        Assert.True(unpinned);

        GrimoireEntryDto? final = await repository.GetEntryByIdAsync(sessionId, assistantEntryId, CancellationToken.None);

        Assert.NotNull(final);

        Assert.False(final!.IsPinned);

    }

    [SkippableFact]
    public async Task SetEntryPinnedAsync_returns_false_when_entry_missing()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, _) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "session only",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        bool pinned = await repository.SetEntryPinnedAsync(sessionId, Guid.NewGuid(), true, CancellationToken.None);

        Assert.False(pinned);

    }

    [SkippableFact]
    public async Task GetPinnedEntryCountAsync_counts_only_pinned_entries()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, Guid entryId1) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "pin one",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await repository.FinalizeAssistantEntryAsync(entryId1, "reply one", CancellationToken.None);

        (Guid _, Guid entryId2) = await repository.BeginAssistantReplyAsync(
            sessionId,
            prompt: "pin two",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await repository.FinalizeAssistantEntryAsync(entryId2, "reply two", CancellationToken.None);

        Assert.Equal(0, await repository.GetPinnedEntryCountAsync(sessionId, CancellationToken.None));

        await repository.SetEntryPinnedAsync(sessionId, entryId1, true, CancellationToken.None);

        Assert.Equal(1, await repository.GetPinnedEntryCountAsync(sessionId, CancellationToken.None));

        await repository.SetEntryPinnedAsync(sessionId, entryId2, true, CancellationToken.None);

        Assert.Equal(2, await repository.GetPinnedEntryCountAsync(sessionId, CancellationToken.None));

    }

    [SkippableFact]
    public async Task GetSessionEntriesAsync_includes_is_pinned_projection()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        (Guid sessionId, Guid entryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "project pin",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        await repository.FinalizeAssistantEntryAsync(entryId, "project pin reply", CancellationToken.None);

        await repository.SetEntryPinnedAsync(sessionId, entryId, true, CancellationToken.None);

        List<GrimoireEntryDto>? entries = await repository.GetSessionEntriesAsync(sessionId, CancellationToken.None);

        Assert.NotNull(entries);

        Assert.Contains(entries!, e => e.Id == entryId && e.IsPinned);

    }

    [SkippableFact]
    public async Task RecordWorkspaceContextAsync_and_GetLatestWorkspaceContextAsync_round_trip_without_offset_orderby_error()
    {
        // Regression guard for the first-message chat crash: ChronosyncEngine.AnalyzeAndSyncAsync
        // calls these two methods on every chat turn. The EF Core SQLite provider cannot
        // translate DateTimeOffset in ORDER BY, so the repository must materialize and sort
        // client-side. Without that, the first chat message throws
        // "SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses".
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = CreateRepository();

        string workspacePath = "/tmp/arcanum-regression/" + Guid.NewGuid().ToString("N");

        WorkspaceContext first = new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero),
            WorkspacePath = workspacePath,
            SerializedSnapshot = "{\"domain\":\"SoftwareEngineering\"}",
        };

        await repository.RecordWorkspaceContextAsync(first, CancellationToken.None);

        WorkspaceContext second = new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = new DateTimeOffset(2026, 7, 18, 12, 5, 0, TimeSpan.Zero),
            WorkspacePath = workspacePath,
            SerializedSnapshot = "{\"domain\":\"Research\"}",
        };

        await repository.RecordWorkspaceContextAsync(second, CancellationToken.None);

        WorkspaceContext? latest = await repository.GetLatestWorkspaceContextAsync(workspacePath, CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(second.Id, latest!.Id);
        Assert.Equal(second.CreatedAt, latest.CreatedAt);
    }

    /// <summary>
    /// Creating a Session through the production turn-begin path writes a binding row that agrees with
    /// the Session it names, so the Session can actually be created and the binding can be read back.
    /// </summary>
    /// <remarks>
    /// <b>This is the first test that drives the real <c>CreateBoundSessionAsync</c> against a database.</b>
    /// Every other reference to it in the suite is a fake, which is why the defect below survived: the
    /// object-relational writer stores <c>"Sessions"."Id"</c> as uppercase dashed text, the binding row was
    /// written with a bare <c>ToString()</c>, and <c>session_campaign_bindings.SessionId</c> is declared
    /// <c>REFERENCES "Sessions"("Id")</c> with foreign keys both set and verified on every connection this
    /// context opens. Parent and child therefore disagreed and the insert failed the foreign key, rolling
    /// the whole transaction back and returning <c>Grimoire.WriteFailed</c> - so no Session could be
    /// created at all through the path a turn naming no Session takes.
    ///
    /// <para>Both arms matter and they fail for the same reason. A Campaign-bound Session and a
    /// global-only one both write a binding row; only the <c>CampaignId</c> differs, and that column is
    /// not what the foreign key is about.</para>
    ///
    /// <para>The assertion is that the two columns <i>agree</i>, read back out of the rows rather than
    /// compared against a rendering this test chose. An assertion that the binding is uppercase would
    /// pass vacuously on an all-digit identity, and one that it equals a spelling the test picked would
    /// be describing the fix rather than the requirement: what the foreign key demands is that the child
    /// holds exactly what the parent holds.</para>
    /// </remarks>
    [SkippableTheory]

    [InlineData(true)]

    [InlineData(false)]

    public async Task CreateBoundSessionAsync_writes_a_binding_that_agrees_with_the_Session_it_names(
        bool campaignBound)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CanonicalCampaignContext campaign = campaignBound
            ? await SeedCampaignContextAsync()
            : CanonicalCampaignContext.GlobalOnly;

        // The repository turns any failure here into one opaque message, so the captured exception is
        // carried into the assertion. Without it a red says only "The session could not be created.",
        // which is the same sentence for a refused authorization scope, a foreign key, and a bug nobody
        // has written yet - and a mutation check that cannot tell those apart proves very little.
        TestCapturingLogger<GrimoireRepository> logger = new();

        GrimoireRepository repository = CreateRepository(logger: logger);

        Result<Guid> created = await repository.CreateBoundSessionAsync(
            campaign,
            "a new conversation",
            CancellationToken.None);

        Assert.True(
            created.IsSuccess,
            created.IsFailure
                ? created.Error.Message
                    + System.Environment.NewLine
                    + string.Join(
                        System.Environment.NewLine,
                        logger.Entries.Select(static entry => entry.Exception?.ToString()))
                : string.Empty);

        (string session, string binding) = await ReadSessionAndBindingAsync(created.Value);

        Assert.Equal(session, binding);

    }

    /// <summary>
    /// A Campaign the resolver would hand the turn-begin store, written the way the object-relational
    /// writer writes one.
    /// </summary>
    private async Task<CanonicalCampaignContext> SeedCampaignContextAsync()
    {

        Guid campaignId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = _db!.Campaigns.Add(new Campaign
        {

            Id = campaignId,

            Name = "binding-" + campaignId.ToString("N"),

            NameLower = "binding-" + campaignId.ToString("N"),

            Path = Path.Combine(Path.GetTempPath(), "binding-" + campaignId.ToString("N")),

            Type = WorkspaceType.Campaign,

            CreatedAt = now,

            UpdatedAt = now,

        });

        _ = await _db.SaveChangesAsync(CancellationToken.None);

        return CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest: null);

    }

    /// <summary>
    /// The two stored spellings, read as text so the comparison is the one the foreign key makes.
    /// </summary>
    private async Task<(string Session, string Binding)> ReadSessionAndBindingAsync(Guid sessionId)
    {

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(CancellationToken.None);

        }

        await using System.Data.Common.DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT session."Id", binding.SessionId
            FROM "Sessions" AS session
            JOIN session_campaign_bindings AS binding
              ON lower(replace(binding.SessionId, '-', '')) = lower(replace(session."Id", '-', ''))
            WHERE lower(replace(session."Id", '-', '')) = @id;
            """;

        System.Data.Common.DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "@id";

        parameter.Value = sessionId.ToString("N");

        command.Parameters.Add(parameter);

        await using System.Data.Common.DbDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None), "No Session and binding pair was written.");

        return (reader.GetString(0), reader.GetString(1));

    }

    /// <summary>
    /// A labelled Entry cannot be deleted through the repository the product actually composes.
    /// </summary>
    /// <remarks>
    /// Resolved from a real composition root rather than constructed here, because the defect this
    /// pins was never in the repository: the guard was a constructor parameter defaulting to null and
    /// both factory registrations simply stopped short of it, so every test that built the subject by
    /// hand passed the argument production forgot and watched a refusal production could not reach.
    /// The refusal surfaces as a throw rather than a failed <c>Result</c> because
    /// <c>DeleteEntryAsync</c> returns <c>bool</c>: there is no failure channel in its signature, which
    /// is why the guard raises instead of returning one (§10.20.2).
    /// </remarks>
    [SkippableTheory]
    [InlineData(GrimoireComposition.NonPooledCli)]
    [InlineData(GrimoireComposition.PooledHost)]
    public async Task Deleting_a_labelled_entry_through_the_composed_repository_is_refused(
        GrimoireComposition composition)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ServiceProvider provider = _fixture.CreateComposedProvider(composition);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        IGrimoireRepository repository = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

        (Guid sessionId, Guid assistantEntryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "Which artifacts carry a sensitivity label?",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        ArcanumDbContext composed = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await LabelAssistantEntryAsync(composed, assistantEntryId, CancellationToken.None);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.DeleteEntryAsync(sessionId, assistantEntryId, CancellationToken.None));

        Assert.Contains("purge boundary", refused.Message, StringComparison.Ordinal);

        // The refusal is only worth anything if it stopped the delete. A guard that raised after the
        // row was gone would satisfy the assertion above and lose the artifact anyway.
        Assert.True(
            await composed.Entries
                .AsNoTracking()
                .AnyAsync(entry => entry.Id == assistantEntryId, CancellationToken.None),
            "The refused delete removed the labelled Entry anyway.");

    }

    /// <summary>
    /// Puts a live sensitivity label on an assistant Entry, by the same raw insert the label suite uses.
    /// </summary>
    /// <remarks>
    /// Raw rather than through the ledger's own write path: <c>artifact_sensitivity</c> is declared in
    /// the schema tree rather than the compiled EF model, and the label is this test's precondition
    /// rather than the thing it asserts.
    /// </remarks>
    private static async Task LabelAssistantEntryAsync(
        ArcanumDbContext db,
        Guid entryId,
        CancellationToken cancellationToken)
    {

        System.Data.Common.DbConnection connection = db.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken);

        }

        await using System.Data.Common.DbCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId,
                ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES ($label, $kind, $artifact, 1, 1, $generations, NULL, NULL, NULL, NULL,
                    1, zeroblob(32), zeroblob(32), NULL, NULL, NULL, zeroblob(32), $now);
            """;

        AddParameter(command, "$label", Guid.NewGuid().ToString("D").ToUpperInvariant());

        AddParameter(command, "$kind", (int)SensitiveArtifactKind.AssistantEntry);

        AddParameter(command, "$artifact", entryId.ToString("D").ToUpperInvariant());

        AddParameter(command, "$generations", Enumerable.Repeat((byte)7, 16).ToArray());

        AddParameter(command, "$now", "2026-01-01T00:00:00.0000000Z");

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {

        System.Data.Common.DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        command.Parameters.Add(parameter);

    }

    private GrimoireRepository CreateRepository(
        ArcanumDbContext? db = null,
        ILogger<GrimoireRepository>? logger = null,
        FixtureOrdinaryConnectionFactory? connections = null,
        ISessionAttachmentStore? attachments = null)
    {
        ArcanumDbContext context = db ?? _db!;

        return new GrimoireRepository(
            context,
            attachments ?? new NoOpSessionAttachmentStore(),
            logger ?? NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            attachmentIndex: null,
            covenantKernel: null,
            connections ?? FixtureOrdinaryConnectionFactory.For(context),
            FixtureLabeledArtifactGuard.For(context));

    }

    private async Task EnsureEntryEmbeddingTablesAsync()
    {

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(CancellationToken.None);

        }

        await using System.Data.Common.DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS entry_embeddings (
                EntryId TEXT PRIMARY KEY,
                Embedding BLOB NOT NULL,
                Dim INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS entry_embeddings_vec (
                EntryId TEXT PRIMARY KEY,
                Embedding BLOB NOT NULL
            );
            """;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task InsertEntryEmbeddingAsync(Guid entryId)
    {

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        await using System.Data.Common.DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO entry_embeddings (EntryId, Embedding, Dim)
            VALUES (@entryId, @embedding, 64);

            INSERT INTO entry_embeddings_vec (EntryId, Embedding)
            VALUES (@entryId, @embedding);
            """;

        System.Data.Common.DbParameter entryIdParameter = command.CreateParameter();

        entryIdParameter.ParameterName = "@entryId";

        // The weaving service copies whatever spelling Entries."Id" holds, which the value binder
        // renders uppercase; a bare ToString() seeded an embedding its own Entry's join would miss.
        entryIdParameter.Value = entryId.ToString("D").ToUpperInvariant();

        command.Parameters.Add(entryIdParameter);

        System.Data.Common.DbParameter embeddingParameter = command.CreateParameter();

        embeddingParameter.ParameterName = "@embedding";

        embeddingParameter.Value = new byte[64 * sizeof(float)];

        command.Parameters.Add(embeddingParameter);

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task<long> CountEntryEmbeddingAsync(string table, Guid entryId)
    {

        System.Data.Common.DbConnection connection = _db!.Database.GetDbConnection();

        await using System.Data.Common.DbCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE EntryId = @entryId";

        System.Data.Common.DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "@entryId";

        // The read has to bind the spelling the column holds, which is the canonical one the
        // weaving service copies out of Entries."Id" and the guard now enforces.
        parameter.Value = entryId.ToString("D").ToUpperInvariant();

        command.Parameters.Add(parameter);

        object? result = await command.ExecuteScalarAsync(CancellationToken.None);

        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);

    }

    private async Task<Guid> SeedUnsummarizedWindowAsync(
        string title,
        DateTime watermark,
        int entryCount,
        int? unsummarizedEntryCount = null)
    {

        Guid sessionId = Guid.NewGuid();

        _db!.Sessions.Add(new Session
        {
            Id = sessionId,
            Title = title,
            Status = "active",
            CreatedAt = new DateTimeOffset(watermark, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(watermark.AddMinutes(entryCount), TimeSpan.Zero),
            LastSummarizedMessageAt = watermark,
            UnsummarizedEntryCount = unsummarizedEntryCount ?? entryCount,
        });

        for (int i = 1; i <= entryCount; i++)
        {
            _db.Entries.Add(new Entry
            {
                Id = EntryId(i),
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = $"entry-{i}",
                ModelUsed = "test-model",
                CreatedAt = new DateTimeOffset(watermark.AddMinutes(i), TimeSpan.Zero),
                Sequence = i,
            });
        }

        await _db.SaveChangesAsync(CancellationToken.None);

        return sessionId;

    }

    private static Guid EntryId(int ordinal) =>
        Guid.Parse($"00000000-0000-0000-0000-{ordinal:000000000000}");

}
