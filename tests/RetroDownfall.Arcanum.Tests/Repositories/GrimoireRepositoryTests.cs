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
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(settings));

    }

}
