using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
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

    private GrimoireRepository CreateRepository()
    {

        ArcanumSettings settings = new()
        {
            Grimoire = new GrimoireSettings
            {
                MaxMessagesPerConversationLoad = 50,
                WorkspaceContextRetentionCount = 5,
            },
            Intelligence = new IntelligenceSettings
            {
                ArchiveSearchMaxQueryLength = 256,
            },
        };

        return new GrimoireRepository(
            _db!,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(settings));

    }

}
