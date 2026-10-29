using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// Repository reads compare persisted UTC text against the same representation, including when a
/// transcript cursor has been deleted and its sequence can no longer supply the boundary.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class EntryTimestampComparisonTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Token => CancellationToken.None;

    private string _path = string.Empty;

    private ArcanumDbContext _db = null!;

    public Task InitializeAsync()
    {
        _path = fixture.CopyDatabase();

        _db = fixture.CreateContext(_path);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        File.Delete(_path);
    }

    [SkippableTheory]
    [InlineData(12, 0, 0)]
    [InlineData(12, 1_234_567, -4)]
    [InlineData(0, 0, 2)]
    [InlineData(0, 1_234_567, 0)]
    public async Task Deleted_cursor_pages_by_its_instant_and_id_in_descending_sequence(
        int hour,
        int fractionalTicks,
        int cursorOffsetHours)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository sessions = Sessions();

        Session session = await sessions.CreateAsync(null, "deleted timestamp cursor", Token);

        DateTimeOffset boundary = Boundary(hour, fractionalTicks);

        Entry previousDay = await AppendAsync(sessions, session.Id, 1, boundary.AddDays(-1));

        Entry earlier = await AppendAsync(sessions, session.Id, 2, boundary.AddTicks(-1));

        Entry lowerId = await AppendAsync(sessions, session.Id, 3, boundary);

        Entry cursor = await AppendAsync(sessions, session.Id, 4, boundary);

        _ = await AppendAsync(sessions, session.Id, 5, boundary);

        _ = await AppendAsync(sessions, session.Id, 6, boundary.AddTicks(1));

        _ = await AppendAsync(sessions, session.Id, 7, boundary.AddDays(1));

        // Pin the real writer's bytes: a raw DateTimeOffset parameter is bound by the SQLite driver
        // with a space separator, whereas stored instants have T and a fixed seven-digit fraction.
        Assert.Equal(
            boundary.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            await StoredCreatedAtAsync(cursor.Id));

        Assert.True(await Grimoire().DeleteEntryAsync(session.Id, cursor.Id, Token));

        Assert.Null(await sessions.GetEntryAsync(session.Id, cursor.Id, Token));

        List<Entry> page = await sessions.GetEntriesAsync(
            session.Id,
            beforeCreatedAt: boundary.ToOffset(TimeSpan.FromHours(cursorOffsetHours)),
            beforeId: cursor.Id,
            ct: Token);

        Assert.Equal(new[] { lowerId.Id, earlier.Id, previousDay.Id }, page.Select(entry => entry.Id));
    }

    [SkippableTheory]
    [InlineData(12, 0)]
    [InlineData(12, 1_234_567)]
    [InlineData(0, 0)]
    [InlineData(0, 1_234_567)]
    public async Task Unsummarized_page_excludes_the_watermark_and_keeps_the_complete_next_timestamp_group(
        int hour,
        int fractionalTicks)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository sessions = Sessions();

        Session session = await sessions.CreateAsync(null, "watermark timestamp group", Token);

        DateTimeOffset boundary = Boundary(hour, fractionalTicks);

        _ = await AppendAsync(sessions, session.Id, 1, boundary.AddDays(-1));

        _ = await AppendAsync(sessions, session.Id, 2, boundary.AddTicks(-1));

        _ = await AppendAsync(sessions, session.Id, 3, boundary);

        Entry next = await AppendAsync(sessions, session.Id, 4, boundary.AddTicks(1));

        Entry tied = await AppendAsync(sessions, session.Id, 5, boundary.AddTicks(1).ToOffset(TimeSpan.FromHours(-4)));

        _ = await AppendAsync(sessions, session.Id, 6, boundary.AddDays(1));

        List<Entry> page = await Grimoire().GetUnsummarizedEntriesAsync(
            session.Id,
            boundary.UtcDateTime,
            batchSize: 1,
            cancellationToken: Token);

        Assert.Equal(new[] { next.Id, tied.Id }, page.Select(entry => entry.Id));
    }

    [SkippableTheory]
    [InlineData(12, 0)]
    [InlineData(12, 1_234_567)]
    [InlineData(0, 0)]
    [InlineData(0, 1_234_567)]
    public async Task Campaign_rollup_counts_only_entries_strictly_after_its_watermark(
        int hour,
        int fractionalTicks)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository sessions = Sessions();

        Session session = await sessions.CreateAsync(null, "remaining timestamp count", Token);

        DateTimeOffset boundary = Boundary(hour, fractionalTicks);

        _ = await AppendAsync(sessions, session.Id, 1, boundary.AddDays(-1));

        _ = await AppendAsync(sessions, session.Id, 2, boundary.AddTicks(-1));

        _ = await AppendAsync(sessions, session.Id, 3, boundary);

        _ = await AppendAsync(sessions, session.Id, 4, boundary.AddTicks(1));

        _ = await AppendAsync(sessions, session.Id, 5, boundary.AddDays(1));

        GrimoireRepository grimoire = Grimoire();

        await grimoire.UpdateSessionCampaignRollupAsync(session.Id, "through the boundary", boundary.UtcDateTime, Token);

        Session? updated = await grimoire.GetSessionHeaderAsync(session.Id, Token);

        Assert.NotNull(updated);

        Assert.Equal(2, updated.UnsummarizedEntryCount);

        Assert.Equal(boundary.UtcDateTime, updated.LastSummarizedMessageAt);
    }

    [SkippableFact]
    public async Task Timestamp_filtered_session_queries_reject_invalid_status_and_keep_title_payloads_as_literal_data()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionRepository sessions = Sessions();

        const string hostileTitle = "literal'); DROP TABLE Sessions; --";

        Session literal = await sessions.CreateAsync(null, hostileTitle, Token);

        _ = await sessions.CreateAsync(null, "ordinary session", Token);

        DateTimeOffset from = DateTimeOffset.UtcNow.AddMinutes(-1);

        DateTimeOffset to = from.AddMinutes(2);

        Result<SessionQueryResult> injectedStatus = await sessions.QueryAsync(
            new SessionQueryRequest(Status: "active' OR 1=1 --", From: from, To: to), Token);

        Assert.True(injectedStatus.IsFailure);

        Assert.Equal(ErrorCodes.Session.InvalidStatus, injectedStatus.Error.Code);

        SessionQueryResult literalTitle = (await sessions.QueryAsync(
            new SessionQueryRequest(Title: hostileTitle, From: from, To: to), Token)).Value;

        Assert.Equal(literal.Id, Assert.Single(literalTitle.Summaries).Id);

        SessionQueryResult surviving = (await sessions.QueryAsync(
            new SessionQueryRequest(From: from, To: to), Token)).Value;

        Assert.Equal(2, surviving.Summaries.Length);
    }

    private SessionRepository Sessions() =>
        new(_db, new NoOpSessionAttachmentStore(), fixture.CreateOptionsMonitor(), FixtureOrdinaryConnectionFactory.For(_db));

    private GrimoireRepository Grimoire() =>
        new(
            _db,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            covenantKernel: null,
            availabilityRepublisher: null,
            FixtureOrdinaryConnectionFactory.For(_db),
            FixtureLabeledArtifactGuard.For(_db),
            memoryDelegation: null);

    private static DateTimeOffset Boundary(int hour, int fractionalTicks) =>
        new DateTimeOffset(2026, 9, 9, hour, 0, 0, TimeSpan.Zero).AddTicks(fractionalTicks);

    private static async Task<Entry> AppendAsync(
        SessionRepository repository,
        Guid sessionId,
        int identity,
        DateTimeOffset createdAt)
    {
        Entry entry = new()
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{identity:D12}"),
            Role = MessageRole.User,
            Content = $"entry-{identity}",
            ModelUsed = "test-model",
            CreatedAt = createdAt,
        };

        Result<Entry> result = await repository.AddEntryAsync(sessionId, entry, Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return entry;
    }

    private async Task<string> StoredCreatedAtAsync(Guid entryId)
    {
        await using SqliteCommand command = ((SqliteConnection)_db.Database.GetDbConnection()).CreateCommand();

        command.CommandText = "SELECT CreatedAt FROM Entries WHERE Id = $id;";

        _ = command.Parameters.AddWithValue("$id", entryId.ToString("D").ToUpperInvariant());

        return Assert.IsType<string>(await command.ExecuteScalarAsync(Token));
    }
}
