using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

[Collection("Grimoire")]
public sealed class SessionRepositoryTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public SessionRepositoryTests(GrimoireFixture fixture)
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
    public async Task CreateAsync_persists_and_returns_active_session()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, _fixture.CreateOptionsMonitor());

        Session created = await repository.CreateAsync(campaignId: null, title: "  Alpha thread  ", CancellationToken.None);

        Session? loaded = await repository.GetByIdAsync(created.Id, CancellationToken.None);

        Assert.NotNull(loaded);

        Assert.Equal("Alpha thread", loaded!.Title);

        Assert.Equal("active", loaded.Status);

    }

    [SkippableFact]
    public async Task AddEntryAsync_sets_title_from_first_user_message()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, _fixture.CreateOptionsMonitor());

        Session session = await repository.CreateAsync(campaignId: null, title: null, CancellationToken.None);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Entry entry = new()
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = "Summarize the northern ward",
            ModelUsed = "test-model",
            CreatedAt = now,
        };

        _ = await repository.AddEntryAsync(session.Id, entry, CancellationToken.None);

        Session? reloaded = await repository.GetByIdAsync(session.Id, CancellationToken.None);

        Assert.NotNull(reloaded);

        Assert.Equal("Summarize the northern ward", reloaded!.Title);

        Assert.Equal(1, await repository.GetEntryCountAsync(session.Id, CancellationToken.None));

        Entry? loadedEntry = await repository.GetEntryAsync(session.Id, entry.Id, CancellationToken.None);

        Assert.NotNull(loadedEntry);

        Assert.Equal(entry.Content, loadedEntry!.Content);

    }

    [SkippableFact]
    public async Task ArchiveAsync_marks_session_archived()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, _fixture.CreateOptionsMonitor());

        Session session = await repository.CreateAsync(campaignId: null, title: "Archive me", CancellationToken.None);

        await repository.ArchiveAsync(session.Id, CancellationToken.None);

        Session? archived = await repository.GetByIdAsync(session.Id, CancellationToken.None);

        Assert.NotNull(archived);

        Assert.Equal("archived", archived!.Status);

    }

    [SkippableFact]
    public async Task GetAnalyticsAsync_counts_sessions_and_entries()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, _fixture.CreateOptionsMonitor());

        SessionAnalytics before = await repository.GetAnalyticsAsync(CancellationToken.None);

        Session session = await repository.CreateAsync(campaignId: null, title: "Stats", CancellationToken.None);

        _ = await repository.AddEntryAsync(
            session.Id,
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = "ping",
                ModelUsed = "stats-model",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        SessionAnalytics after = await repository.GetAnalyticsAsync(CancellationToken.None);

        Assert.Equal(before.TotalSessions + 1, after.TotalSessions);

        Assert.Equal(before.TotalEntries + 1, after.TotalEntries);

        Assert.Equal(before.UserEntries + 1, after.UserEntries);

    }

    [SkippableFact]
    public async Task AddEntryAsync_TooManyEntries_Throws()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ArcanumSettings settings = new()
        {
            Sessions = new SessionSettings
            {
                MaxEntriesPerSession = 100,
                MaxEntryContentBytes = 1024,
            },
        };

        SessionRepository repository = new(_db!, new TestOptionsMonitor<ArcanumSettings>(settings));

        Session session = await repository.CreateAsync(campaignId: null, title: "Limited", CancellationToken.None);

        for (int i = 0; i < 100; i++)
        {

            _ = await repository.AddEntryAsync(
                session.Id,
                new Entry
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = $"entry-{i}",
                    ModelUsed = "test-model",
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None);

        }

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AddEntryAsync(
                session.Id,
                new Entry
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = "second",
                    ModelUsed = "test-model",
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None));

        Assert.StartsWith("Session.TooManyEntries:", ex.Message, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task AddEntryAsync_EntryTooLarge_Throws()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ArcanumSettings settings = new()
        {
            Sessions = new SessionSettings
            {
                MaxEntriesPerSession = 100,
                MaxEntryContentBytes = 1024,
            },
        };

        SessionRepository repository = new(_db!, new TestOptionsMonitor<ArcanumSettings>(settings));

        Session session = await repository.CreateAsync(campaignId: null, title: "Sized", CancellationToken.None);

        string oversized = new('x', 1025);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AddEntryAsync(
                session.Id,
                new Entry
                {
                    Id = Guid.NewGuid(),
                    Role = MessageRole.User,
                    Content = oversized,
                    ModelUsed = "test-model",
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None));

        Assert.StartsWith("Session.EntryTooLarge:", ex.Message, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task AddEntryAsync_increments_unsummarized_entry_count()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, _fixture.CreateOptionsMonitor());

        Session session = await repository.CreateAsync(campaignId: null, title: "Counter", CancellationToken.None);

        Assert.Equal(0, session.UnsummarizedEntryCount);

        _ = await repository.AddEntryAsync(
            session.Id,
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = "first turn",
                ModelUsed = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        _ = await repository.AddEntryAsync(
            session.Id,
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.Assistant,
                Content = "second turn",
                ModelUsed = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        Session? reloaded = await repository.GetByIdAsync(session.Id, CancellationToken.None);

        Assert.NotNull(reloaded);

        Assert.Equal(2, reloaded!.UnsummarizedEntryCount);

    }

}
