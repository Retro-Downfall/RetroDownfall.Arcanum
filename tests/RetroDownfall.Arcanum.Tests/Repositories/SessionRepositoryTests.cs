using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using System.Text.Json;

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

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

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

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

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

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

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

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

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
    public async Task AddEntryAsync_TooManyEntries_ReturnsFailure()
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

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), new TestOptionsMonitor<ArcanumSettings>(settings));

        Session session = await repository.CreateAsync(campaignId: null, title: "Limited", CancellationToken.None);

        for (int i = 0; i < 100; i++)
        {

            Result<Entry> ok = await repository.AddEntryAsync(
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

            Assert.True(ok.IsSuccess, ok.Error.Code);

        }

        Result<Entry> result = await repository.AddEntryAsync(
            session.Id,
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = "second",
                ModelUsed = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Session.TooManyEntries, result.Error.Code);

        Assert.StartsWith("Session.TooManyEntries:", result.Error.Message, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task AddEntryAsync_EntryTooLarge_ReturnsFailure()
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

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), new TestOptionsMonitor<ArcanumSettings>(settings));

        Session session = await repository.CreateAsync(campaignId: null, title: "Sized", CancellationToken.None);

        string oversized = new('x', 1025);

        Result<Entry> result = await repository.AddEntryAsync(
            session.Id,
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = oversized,
                ModelUsed = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Session.EntryTooLarge, result.Error.Code);

        Assert.StartsWith("Session.EntryTooLarge:", result.Error.Message, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task AddEntryAsync_NotFound_ReturnsFailure()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

        Result<Entry> result = await repository.AddEntryAsync(
            Guid.NewGuid(),
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = "orphan",
                ModelUsed = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Session.NotFound, result.Error.Code);

        Assert.Equal("Session was not found.", result.Error.Message);

    }

    [SkippableFact]
    public async Task AddEntryAsync_Archived_ReturnsFailure()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

        Session session = await repository.CreateAsync(campaignId: null, title: "Closed", CancellationToken.None);

        await repository.ArchiveAsync(session.Id, CancellationToken.None);

        _db!.ChangeTracker.Clear();

        Result<Entry> result = await repository.AddEntryAsync(
            session.Id,
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = "too late",
                ModelUsed = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Session.Archived, result.Error.Code);

        Assert.Equal("Cannot append entries to an archived session.", result.Error.Message);

    }

    [SkippableFact]
    public async Task AddEntryAsync_increments_unsummarized_entry_count()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

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

    [SkippableFact]
    public async Task UpdateSessionAsync_does_not_clobber_unsummarized_entry_count()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

        Session session = await repository.CreateAsync(campaignId: null, title: "Before patch", CancellationToken.None);

        _ = await repository.AddEntryAsync(
            session.Id,
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = "turn one",
                ModelUsed = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        Session? beforePatch = await repository.GetByIdAsync(session.Id, CancellationToken.None);

        Assert.NotNull(beforePatch);

        Assert.Equal(1, beforePatch!.UnsummarizedEntryCount);

        beforePatch.Title = "After patch";

        beforePatch.UnsummarizedEntryCount = 0;

        await repository.UpdateSessionAsync(beforePatch, CancellationToken.None);

        Session? afterPatch = await repository.GetByIdAsync(session.Id, CancellationToken.None);

        Assert.NotNull(afterPatch);

        Assert.Equal("After patch", afterPatch!.Title);

        Assert.Equal(1, afterPatch.UnsummarizedEntryCount);

    }

    [SkippableFact]
    public async Task GetEntriesAscendingAsync_returns_entries_in_created_at_order()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        DateTimeOffset baseline = new(2025, 2, 1, 7, 0, 0, TimeSpan.Zero);

        _db!.Sessions.Add(NewSession(sessionId, "ordered", baseline));

        Guid e1 = Guid.NewGuid();

        Guid e2 = Guid.NewGuid();

        Guid e3 = Guid.NewGuid();

        // Insert out of chronological order to prove ordering is by CreatedAt, not insertion.
        _db.Entries.Add(NewEntry(e2, sessionId, "msg-2", baseline.AddMinutes(2)));

        _db.Entries.Add(NewEntry(e3, sessionId, "msg-3", baseline.AddMinutes(3)));

        _db.Entries.Add(NewEntry(e1, sessionId, "msg-1", baseline.AddMinutes(1)));

        await _db.SaveChangesAsync(CancellationToken.None);

        SessionRepository repository = new(_db, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

        List<Entry> recent = await repository.GetEntriesAscendingAsync(sessionId, takeLast: 2, CancellationToken.None);

        Assert.Equal(new[] { e2, e3 }, recent.Select(e => e.Id).ToArray());

        Assert.Equal(new[] { "msg-2", "msg-3" }, recent.Select(e => e.Content).ToArray());

    }

    [SkippableFact]
    public async Task QueryAsync_orders_by_updated_at_desc_and_paginates()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset baseline = new(2025, 5, 1, 9, 0, 0, TimeSpan.Zero);

        Guid s1 = Guid.NewGuid();

        Guid s2 = Guid.NewGuid();

        Guid s3 = Guid.NewGuid();

        Guid s4 = Guid.NewGuid();

        _db!.Sessions.Add(NewSession(s1, "oldest", baseline));

        _db.Sessions.Add(NewSession(s2, "older", baseline.AddMinutes(10)));

        _db.Sessions.Add(NewSession(s3, "newer", baseline.AddMinutes(20)));

        _db.Sessions.Add(NewSession(s4, "newest", baseline.AddMinutes(30)));

        await _db.SaveChangesAsync(CancellationToken.None);

        SessionRepository repository = new(_db, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

        SessionQueryResult firstPage = await repository.QueryAsync(
            new SessionQueryRequest(Limit: 2),
            CancellationToken.None);

        Assert.True(firstPage.HasMore);

        Assert.Equal(new[] { s4, s3 }, firstPage.Summaries.Select(x => x.Id).ToArray());

        Assert.Equal(baseline.AddMinutes(20), firstPage.NextBeforeUpdatedAt);

        SessionQueryResult secondPage = await repository.QueryAsync(
            new SessionQueryRequest(Limit: 2, BeforeUpdatedAt: firstPage.NextBeforeUpdatedAt),
            CancellationToken.None);

        Assert.False(secondPage.HasMore);

        Assert.Equal(new[] { s2, s1 }, secondPage.Summaries.Select(x => x.Id).ToArray());

        Assert.Null(secondPage.NextBeforeUpdatedAt);

    }

    [SkippableFact]
    public async Task QueryAsync_filters_by_updated_at_range()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset baseline = new(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);

        Guid s1 = Guid.NewGuid();

        Guid s2 = Guid.NewGuid();

        Guid s3 = Guid.NewGuid();

        Guid s4 = Guid.NewGuid();

        _db!.Sessions.Add(NewSession(s1, "before-range", baseline));

        _db.Sessions.Add(NewSession(s2, "low-bound", baseline.AddHours(1)));

        _db.Sessions.Add(NewSession(s3, "high-bound", baseline.AddHours(2)));

        _db.Sessions.Add(NewSession(s4, "after-range", baseline.AddHours(3)));

        await _db.SaveChangesAsync(CancellationToken.None);

        SessionRepository repository = new(_db, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

        SessionQueryResult result = await repository.QueryAsync(
            new SessionQueryRequest(
                From: baseline.AddHours(1),
                To: baseline.AddHours(2),
                Limit: 50),
            CancellationToken.None);

        Assert.Equal(new[] { s3, s2 }, result.Summaries.Select(x => x.Id).ToArray());

    }

    [SkippableFact]
    public async Task GetEntriesAfterAsync_returns_entries_after_cursor_in_created_at_order()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        DateTimeOffset baseline = new(2025, 4, 1, 6, 0, 0, TimeSpan.Zero);

        _db!.Sessions.Add(NewSession(sessionId, "cursor-after", baseline));

        Guid e1 = Guid.NewGuid();

        Guid e2 = Guid.NewGuid();

        Guid e3 = Guid.NewGuid();

        Guid e4 = Guid.NewGuid();

        _db.Entries.Add(NewEntry(e1, sessionId, "after-1", baseline.AddMinutes(1)));

        _db.Entries.Add(NewEntry(e2, sessionId, "after-2", baseline.AddMinutes(2)));

        _db.Entries.Add(NewEntry(e3, sessionId, "after-3", baseline.AddMinutes(3)));

        _db.Entries.Add(NewEntry(e4, sessionId, "after-4", baseline.AddMinutes(4)));

        await _db.SaveChangesAsync(CancellationToken.None);

        SessionRepository repository = new(_db, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

        List<Entry> after = await repository.GetEntriesAfterAsync(
            sessionId,
            afterCreatedAt: baseline.AddMinutes(2),
            afterId: e2,
            limit: 10,
            CancellationToken.None);

        Assert.Equal(new[] { e3, e4 }, after.Select(e => e.Id).ToArray());

        Assert.Equal(new[] { "after-3", "after-4" }, after.Select(e => e.Content).ToArray());

    }

    [SkippableFact]
    public async Task GetEntriesAsync_paginates_before_cursor_in_descending_order()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        DateTimeOffset baseline = new(2025, 7, 1, 5, 0, 0, TimeSpan.Zero);

        _db!.Sessions.Add(NewSession(sessionId, "pagination", baseline));

        Guid e1 = Guid.NewGuid();

        Guid e2 = Guid.NewGuid();

        Guid e3 = Guid.NewGuid();

        Guid e4 = Guid.NewGuid();

        _db.Entries.Add(NewEntry(e1, sessionId, "page-1", baseline.AddMinutes(1)));

        _db.Entries.Add(NewEntry(e2, sessionId, "page-2", baseline.AddMinutes(2)));

        _db.Entries.Add(NewEntry(e3, sessionId, "page-3", baseline.AddMinutes(3)));

        _db.Entries.Add(NewEntry(e4, sessionId, "page-4", baseline.AddMinutes(4)));

        await _db.SaveChangesAsync(CancellationToken.None);

        SessionRepository repository = new(_db, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

        List<Entry> firstPage = await repository.GetEntriesAsync(
            sessionId,
            limit: 2,
            ct: CancellationToken.None);

        Assert.Equal(new[] { e4, e3 }, firstPage.Select(e => e.Id).ToArray());

        Entry cursor = firstPage[^1];

        List<Entry> secondPage = await repository.GetEntriesAsync(
            sessionId,
            limit: 2,
            beforeCreatedAt: cursor.CreatedAt,
            beforeId: cursor.Id,
            ct: CancellationToken.None);

        Assert.Equal(new[] { e2, e1 }, secondPage.Select(e => e.Id).ToArray());

    }

    [SkippableFact]
    public async Task QueryAsync_search_matches_entry_content_via_fts_json_each()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

        Session match = await repository.CreateAsync(campaignId: null, title: "Hidden chronicle", CancellationToken.None);

        _ = await repository.AddEntryAsync(
            match.Id,
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = "northern ward sigil cobalt",
                ModelUsed = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        Session noise = await repository.CreateAsync(campaignId: null, title: "Unrelated ledger", CancellationToken.None);

        _ = await repository.AddEntryAsync(
            noise.Id,
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.User,
                Content = "quartermaster supplies tally",
                ModelUsed = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        SessionQueryResult result = await repository.QueryAsync(
            new SessionQueryRequest(Search: "cobalt", Limit: 50),
            CancellationToken.None);

        Guid[] ids = result.Summaries.Select(x => x.Id).ToArray();

        // "cobalt" appears only in the entry content, never in either title, so the only way
        // `match` can be returned is through the FTS-id set bound via json_each.
        Assert.Contains(match.Id, ids);

        Assert.DoesNotContain(noise.Id, ids);

    }

    [SkippableFact]
    public async Task QueryAsync_filters_by_role_and_model_via_exists_subqueries()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset baseline = new(2025, 8, 1, 4, 0, 0, TimeSpan.Zero);

        Guid withUser = Guid.NewGuid();

        Guid assistantOnly = Guid.NewGuid();

        _db!.Sessions.Add(NewSession(withUser, "has-user", baseline));

        _db.Sessions.Add(NewSession(assistantOnly, "assistant-only", baseline.AddMinutes(1)));

        _db.Entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = withUser,
            Role = MessageRole.User,
            Content = "user turn",
            ModelUsed = "gpt-oracle",
            CreatedAt = baseline,
        });

        _db.Entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = assistantOnly,
            Role = MessageRole.Assistant,
            Content = "assistant turn",
            ModelUsed = "llama-scribe",
            CreatedAt = baseline.AddMinutes(1),
        });

        await _db.SaveChangesAsync(CancellationToken.None);

        SessionRepository repository = new(_db, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

        SessionQueryResult byRole = await repository.QueryAsync(
            new SessionQueryRequest(Role: MessageRole.User, Limit: 50),
            CancellationToken.None);

        Assert.Equal(new[] { withUser }, byRole.Summaries.Select(x => x.Id).ToArray());

        SessionQueryResult byModel = await repository.QueryAsync(
            new SessionQueryRequest(Model: "llama-scribe", Limit: 50),
            CancellationToken.None);

        Assert.Equal(new[] { assistantOnly }, byModel.Summaries.Select(x => x.Id).ToArray());

    }

    // W3.4 Group E #10: JSON export must not accumulate every entry batch into one List<T>
    // before serializing. The stream-serializing implementation writes each batch's entries
    // to a Utf8JsonWriter as they are read, keeping the SessionExportPayload wire shape
    // ({ "session": {...}, "entries": [...] }) and the camelCase contract identical to the
    // previous JsonSerializer.Serialize(SessionExportPayload) output. This characterization
    // test pins the wire shape so the streaming refactor cannot drift it.
    [SkippableFact]
    public async Task ExportAsync_json_preserves_session_export_payload_wire_shape()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), _fixture.CreateOptionsMonitor());

        Session session = await repository.CreateAsync(campaignId: null, title: "Export shape", CancellationToken.None);

        await repository.AddEntryAsync(
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

        await repository.AddEntryAsync(
            session.Id,
            new Entry
            {
                Id = Guid.NewGuid(),
                Role = MessageRole.Assistant,
                Content = "first reply",
                ModelUsed = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        Result<SessionExportResult> result = await repository.ExportAsync(
            session.Id,
            SessionExportFormat.Json,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);

        using JsonDocument doc = JsonDocument.Parse(result.Value.Content);

        Assert.True(doc.RootElement.TryGetProperty("session", out JsonElement sessionEl));

        Assert.True(sessionEl.TryGetProperty("id", out JsonElement idEl));

        Assert.Equal(session.Id, idEl.GetGuid());

        Assert.True(doc.RootElement.TryGetProperty("entries", out JsonElement entriesEl));

        Assert.Equal(JsonValueKind.Array, entriesEl.ValueKind);

        Assert.Equal(2, entriesEl.GetArrayLength());

    }

    // W3.4 Group E #10: a session exceeding the export batch size (500) must export ALL
    // entries via the streaming writer (multiple batches), proving the streaming path
    // produces the complete payload without accumulating a single List of every entry.
    [SkippableFact]
    public async Task ExportAsync_json_streams_all_entries_across_multiple_batches()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ArcanumSettings settings = new()
        {
            Sessions = new SessionSettings
            {
                MaxEntriesPerSession = 10_000,
            },
        };

        SessionRepository repository = new(_db!, new NoOpSessionAttachmentStore(), new TestOptionsMonitor<ArcanumSettings>(settings));

        Session session = await repository.CreateAsync(campaignId: null, title: "Large export", CancellationToken.None);

        const int entryCount = 750; // spans two 500-entry batches

        for (int i = 0; i < entryCount; i++)
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

        Result<SessionExportResult> result = await repository.ExportAsync(
            session.Id,
            SessionExportFormat.Json,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);

        using JsonDocument doc = JsonDocument.Parse(result.Value.Content);

        Assert.True(doc.RootElement.TryGetProperty("entries", out JsonElement entriesEl));

        Assert.Equal(entryCount, entriesEl.GetArrayLength());

    }

    private static Session NewSession(Guid id, string title, DateTimeOffset updatedAt, string status = "active") =>
        new()
        {
            Id = id,
            Title = title,
            Status = status,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
        };

    private static Entry NewEntry(Guid id, Guid sessionId, string content, DateTimeOffset createdAt) =>
        new()
        {
            Id = id,
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = content,
            ModelUsed = "test-model",
            CreatedAt = createdAt,
        };

}
