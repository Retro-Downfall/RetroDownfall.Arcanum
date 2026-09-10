using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// Persists Sessions and their Entries: creation, paging, full-text search, forking, and export.
/// </summary>
/// <remarks>
/// Internal, unlike the <see cref="ISessionRepository" /> it implements. The connection factory
/// this type's constructor requires, <see cref="IGrimoireOrdinaryConnectionFactory" />, is an
/// Infrastructure implementation detail, so a primary constructor that requires it cannot sit on a
/// public type without putting that factory on the assembly's public surface. The type's
/// accessibility follows the constructor's requirement, not the other way around.
/// </remarks>
internal sealed class SessionRepository(
    ArcanumDbContext db,
    ISessionAttachmentStore attachments,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IGrimoireOrdinaryConnectionFactory connections,
    ISessionAttachmentIndexQueue? attachmentIndexQueue = null) : ISessionRepository
{
    /// <summary>
    /// Ceiling on how far a session-list page may widen to keep a one-timestamp tie group whole.
    /// </summary>
    /// <remarks>
    /// Widening is unavoidable — a bare-timestamp cursor cannot express a position inside a tie
    /// group — but it must not be unbounded, or a population whose <c>UpdatedAt</c> values collapse
    /// onto one tick turns a paged list into a whole-table materialization.
    /// </remarks>
    internal const int MaxTieGroupWidening = 1_000;

    private const int ExportEntryBatchSize = 500;

    private const int ForkEntryBatchSize = 256;

    private readonly SessionEntryPersistence _entryPersistence = new(db, connections);

    public async Task<Session> CreateAsync(Guid? campaignId, string? title, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Session session = new()
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        };

        await SqliteBusyRetry.ExecuteAsync(
            () => InsertSessionAsync(session, ct),
            ct).ConfigureAwait(false);

        return session;
    }

    public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct) =>
        ReadSessionAsync(id, ct);

    public async Task<SessionQueryResult> QueryAsync(SessionQueryRequest request, CancellationToken ct)
    {
        SessionSettings settings = optionsMonitor.CurrentValue.ResolveSessions();

        int limit = ArcanumSettingClamps.SessionQueryLimit(
            request.Limit
            ?? settings.DefaultQueryLimit
            ?? ArcanumRuntimeDefaults.Sessions.DefaultQueryLimit!.Value);

        string statusFilter = string.IsNullOrWhiteSpace(request.Status) ? "active" : request.Status.Trim();

        string? searchTitlePattern = null;

        string? ftsMatchQuery = null;

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string search = request.Search.Trim();

            int maxQueryLen = ArcanumSettingClamps.ArchiveSearchMaxQueryLength(
                optionsMonitor.CurrentValue.ResolveIntelligence().ArchiveSearchMaxQueryLength);

            if (search.Length > maxQueryLen)
            {
                search = search[..maxQueryLen];
            }

            searchTitlePattern = SqlLikePatterns.Contains(search);

            string matchQuery = FtsMatchQuerySanitizer.Sanitize(search);

            if (!string.IsNullOrEmpty(matchQuery))
            {
                ftsMatchQuery = matchQuery;
            }
        }

        List<(string Name, object Value)> parameters = [];

        string Bind(object value)
        {
            string placeholder = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);

            parameters.Add((placeholder, value));

            return placeholder;
        }

        List<string> conditions = [];

        if (!string.Equals(statusFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add($"\"Status\" = {Bind(statusFilter)}");
        }

        if (request.CampaignId is Guid campaignId)
        {
            conditions.Add($"\"CampaignId\" = {Bind(Format(campaignId))}");
        }

        if (request.From is DateTimeOffset from)
        {
            conditions.Add($"\"UpdatedAt\" >= {Bind(GrimoireEntitySql.Format(from.ToUniversalTime()))}");
        }

        if (request.To is DateTimeOffset to)
        {
            conditions.Add($"\"UpdatedAt\" <= {Bind(GrimoireEntitySql.Format(to.ToUniversalTime()))}");
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            string titlePattern = SqlLikePatterns.Contains(request.Title.Trim());

            conditions.Add(
                $"(\"Title\" IS NOT NULL AND \"Title\" LIKE {Bind(titlePattern)} ESCAPE {Bind(SqlLikePatterns.EscapeString)})");
        }

        if (searchTitlePattern is not null)
        {
            string titleClause =
                $"\"Title\" IS NOT NULL AND \"Title\" LIKE {Bind(searchTitlePattern)} ESCAPE {Bind(SqlLikePatterns.EscapeString)}";

            if (ftsMatchQuery is not null)
            {
                conditions.Add(
                    $"(({titleClause}) OR EXISTS ("
                    + "SELECT 1 FROM \"Entries_fts\" "
                    + "INNER JOIN \"Entries\" AS fts_entry ON fts_entry.\"Id\" = \"Entries_fts\".\"Id\" "
                    + "WHERE fts_entry.\"SessionId\" = \"Sessions\".\"Id\" "
                    + $"AND \"Entries_fts\" MATCH {Bind(ftsMatchQuery)}))");
            }
            else
            {
                conditions.Add($"({titleClause})");
            }
        }

        if (request.Role is MessageRole role)
        {
            conditions.Add(
                $"EXISTS (SELECT 1 FROM \"Entries\" AS e WHERE e.\"SessionId\" = \"Sessions\".\"Id\" AND e.\"Role\" = {Bind((int)role)})");
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            string modelPattern = SqlLikePatterns.EscapeLiteral(request.Model.Trim());

            conditions.Add(
                $"EXISTS (SELECT 1 FROM \"Entries\" AS e WHERE e.\"SessionId\" = \"Sessions\".\"Id\" AND e.\"ModelUsed\" LIKE {Bind(modelPattern)} ESCAPE {Bind(SqlLikePatterns.EscapeString)})");
        }

        if (request.BeforeUpdatedAt is DateTimeOffset before)
        {
            conditions.Add($"\"UpdatedAt\" < {Bind(GrimoireEntitySql.Format(before.ToUniversalTime()))}");
        }

        StringBuilder sqlBuilder = new();

        sqlBuilder.Append("SELECT ");

        sqlBuilder.Append(GrimoireEntitySql.SessionColumns);

        sqlBuilder.Append(" FROM \"Sessions\"");

        if (conditions.Count > 0)
        {
            sqlBuilder.Append(" WHERE ");

            sqlBuilder.Append(string.Join(" AND ", conditions));
        }

        // Snapshotted before the LIMIT parameter is bound, so the tie-completion query below can reuse
        // exactly the filter placeholders and nothing else.
        List<(string Name, object Value)> conditionParameters = [.. parameters];

        // "Id" is the identity tie-breaker. Without it the sort is undefined among sessions sharing an
        // "UpdatedAt" (a burst of creations, or a backup import that replays original timestamps
        // verbatim), so a tie straddling a page boundary would be ordered differently on each query and
        // the keyset cursor below could not reason about it at all.
        sqlBuilder.Append($" ORDER BY \"UpdatedAt\" DESC, \"Id\" DESC LIMIT {Bind(limit + 1)}");

        List<Session> page = await ReadSessionsAsync(
            sqlBuilder.ToString(),
            parameters,
            ct).ConfigureAwait(false);

        bool hasMore = page.Count > limit;

        if (hasMore)
        {
            // The cursor this method hands back is a bare timestamp consumed as a strict
            // "UpdatedAt" < @before, so any session sharing the boundary timestamp with the last row of
            // this page would be excluded from the next page and become permanently unreachable. Rather
            // than widen the wire contract, the page is cut at a tie boundary: every row sharing the
            // first excluded row's timestamp is deferred to the next page, which the strict predicate
            // then admits in full.
            DateTimeOffset boundary = page[limit].UpdatedAt;

            List<Session> kept = page.Take(limit).Where(s => s.UpdatedAt != boundary).ToList();

            // Degenerate case: the entire page is one timestamp, so cutting at the tie boundary would
            // return nothing and leave the cursor exactly where it started. Widen to the complete tie
            // group instead — the page exceeds the requested limit, but it is whole and the cursor still
            // advances past it.
            if (kept.Count > 0)
            {
                page = kept;
            }
            else
            {
                SessionTieGroup tieGroup = await LoadTieGroupAsync(
                    conditions,
                    conditionParameters,
                    boundary,
                    ct).ConfigureAwait(false);

                page = tieGroup.Sessions;

                hasMore = tieGroup.HasOlderSessions;
            }
        }

        Guid[] sessionIds = page.Select(s => s.Id).ToArray();

        Dictionary<Guid, int> entryCounts = [];

        if (sessionIds.Length > 0)
        {
            entryCounts = await ReadEntryCountsAsync(sessionIds, ct).ConfigureAwait(false);
        }

        SessionSummaryDto[] summaries = page
            .Select(s => new SessionSummaryDto(
                s.Id,
                s.CampaignId,
                s.Title,
                s.Status,
                entryCounts.GetValueOrDefault(s.Id),
                s.CreatedAt,
                s.UpdatedAt,
                s.ForkedFromSessionId))
            .ToArray();

        DateTimeOffset? nextBefore = hasMore && page.Count > 0 ? page[^1].UpdatedAt : null;

        return new SessionQueryResult(summaries, nextBefore, hasMore);
    }

    /// <summary>
    /// Loads every session matching the current list filters whose <c>UpdatedAt</c> is exactly
    /// <paramref name="boundary"/>, so a page can never be cut through the middle of a group of sessions
    /// sharing one timestamp.
    /// </summary>
    /// <remarks>
    /// Only reached when a whole page turned out to be a single timestamp, which the bare-timestamp
    /// cursor cannot express a position inside of. Returning the complete group costs one extra query in
    /// that rare case and guarantees the strict <c>"UpdatedAt" &lt; @before</c> cursor advances past it
    /// without skipping a sibling.
    /// </remarks>
    private async Task<SessionTieGroup> LoadTieGroupAsync(
        List<string> conditions,
        List<(string Name, object Value)> parameters,
        DateTimeOffset boundary,
        CancellationToken ct)
    {
        List<(string Name, object Value)> tieParameters = [.. parameters];

        string boundaryParameter = "$p" + tieParameters.Count.ToString(CultureInfo.InvariantCulture);

        List<string> tieConditions =
        [
            .. conditions,
            $"\"UpdatedAt\" = {boundaryParameter}",
        ];

        tieParameters.Add((
            boundaryParameter,
            GrimoireEntitySql.Format(boundary.ToUniversalTime())));

        // One more than the bound, so an oversized group is detected rather than silently clipped.
        // Clipping would leave the cursor on the boundary timestamp and make the rest of the group
        // permanently unreachable through the strict "UpdatedAt" < @before predicate, so the bound
        // has to fail loudly instead.
        string originalConditions = conditions.Count == 0
            ? string.Empty
            : string.Join(" AND ", conditions) + " AND ";

        string sql =
            $"SELECT {GrimoireEntitySql.SessionColumns}, "
            + "EXISTS (SELECT 1 FROM \"Sessions\" WHERE "
            + originalConditions
            + $"\"UpdatedAt\" < {boundaryParameter}) FROM \"Sessions\" WHERE "
            + string.Join(" AND ", tieConditions)
            + " ORDER BY \"UpdatedAt\" DESC, \"Id\" DESC LIMIT "
            + (MaxTieGroupWidening + 1).ToString(CultureInfo.InvariantCulture);

        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            sql,
            ct).ConfigureAwait(false);

        foreach ((string name, object value) in tieParameters)
        {
            GrimoireEntitySql.AddParameter(command, name, value);
        }

        List<Session> group = [];

        bool hasOlderSessions = false;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            group.Add(GrimoireEntitySql.ReadSession(reader));

            hasOlderSessions = reader.GetBoolean(12);
        }

        if (group.Count > MaxTieGroupWidening)
        {
            throw new InvalidOperationException(
                $"More than {MaxTieGroupWidening.ToString(CultureInfo.InvariantCulture)} sessions share "
                + "the timestamp at this page boundary. The session-list cursor is a bare timestamp and "
                + "cannot express a position inside a tie group, so a group this large cannot be paged; "
                + "narrow the query with a status, campaign, or date filter.");
        }

        return new SessionTieGroup(group, hasOlderSessions);
    }

    public async Task<SessionAnalytics> GetAnalyticsAsync(CancellationToken ct)
    {
        SessionStatsRow sessionStats = await ReadSessionStatsAsync(ct).ConfigureAwait(false);

        EntryStatsRow entryStats = await ReadEntryStatsAsync(ct).ConfigureAwait(false);

        Dictionary<string, int> entriesByModel = await ReadEntryCountsByModelAsync(ct).ConfigureAwait(false);

        return new SessionAnalytics(
            sessionStats.TotalSessions,
            sessionStats.ActiveSessions,
            sessionStats.ArchivedSessions,
            entryStats.TotalEntries,
            entryStats.UserEntries,
            entryStats.AssistantEntries,
            entryStats.ToolEntries,
            entryStats.SystemEntries,
            sessionStats.TotalTokensUsed,
            entriesByModel);
    }

    public async Task<Result<SessionExportResult>> ExportAsync(Guid id, SessionExportFormat format, CancellationToken ct)
    {
        Session? session = await ReadSessionAsync(id, ct).ConfigureAwait(false);

        if (session is null)
        {
            return Result<SessionExportResult>.Failure(
                new Error(ErrorCodes.Session.NotFound, "No session exists with that id."));
        }

        return format switch
        {
            SessionExportFormat.Json => Result<SessionExportResult>.Success(new SessionExportResult(
                id,
                "json",
                await SerializeJsonExportAsync(session, id, ct).ConfigureAwait(false),
                "application/json")),

            SessionExportFormat.Markdown => Result<SessionExportResult>.Success(new SessionExportResult(
                id,
                "markdown",
                await FormatMarkdownExportAsync(session, id, ct).ConfigureAwait(false),
                "text/markdown")),

            _ => Result<SessionExportResult>.Failure(
                new Error(ErrorCodes.Session.InvalidFormat, "The export format is not supported.")),
        };
    }

    public async Task<Result<Entry>> AddEntryAsync(Guid sessionId, Entry entry, CancellationToken ct)
    {
        using IDisposable _ = await SessionEntryPersistence.AcquireWriteLockAsync(sessionId, ct).ConfigureAwait(false);

        Session? session = await ReadSessionAsync(sessionId, ct).ConfigureAwait(false);

        if (session is null)
        {
            return Result<Entry>.Failure(new Error(ErrorCodes.Session.NotFound, "Session was not found."));
        }

        if (string.Equals(session.Status, "archived", StringComparison.OrdinalIgnoreCase))
        {
            return Result<Entry>.Failure(
                new Error(ErrorCodes.Session.Archived, "Cannot append entries to an archived session."));
        }

        int entryCount = await _entryPersistence.GetEntryCountAsync(sessionId, ct).ConfigureAwait(false);

        SessionSettings sessionSettings = optionsMonitor.CurrentValue.ResolveSessions();

        Error? limitError = SessionEntryPersistence.CheckEntryLimits(entryCount, entriesToAdd: 1, sessionSettings, entry.Content);

        if (limitError is not null)
        {
            return Result<Entry>.Failure(limitError.Value);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        entry.SessionId = sessionId;

        if (entry.CreatedAt == default)
        {
            entry.CreatedAt = now;
        }

        entry.Sequence = await _entryPersistence
            .ReserveSequenceRangeAsync(sessionId, count: 1, ct)
            .ConfigureAwait(false);

        session.UpdatedAt = now;

        string? automaticTitle = null;

        if (string.IsNullOrWhiteSpace(session.Title)
            && entry.Role == MessageRole.User
            && !string.IsNullOrWhiteSpace(entry.Content))
        {
            automaticTitle = TruncateTitle(entry.Content);
        }

        await using IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            await _entryPersistence.InsertEntryAsync(entry, ct).ConfigureAwait(false);

            await SqliteBusyRetry.ExecuteAsync(
                () => UpdateSessionAfterEntryAsync(session, automaticTitle, ct),
                ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        return Result<Entry>.Success(entry);
    }

    public async Task<Result<Session>> ForkAsync(Guid sourceId, ForkSessionRequest request, CancellationToken ct)
    {
        Guid forkSessionId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Acquire source then fork session gates (stable Guid order to avoid deadlocks with concurrent purge).
        Guid firstGate = sourceId.CompareTo(forkSessionId) <= 0 ? sourceId : forkSessionId;

        Guid secondGate = firstGate == sourceId ? forkSessionId : sourceId;

        using IDisposable gate1 = await attachments.AcquireSessionGateAsync(firstGate, ct).ConfigureAwait(false);

        using IDisposable gate2 = await attachments.AcquireSessionGateAsync(secondGate, ct).ConfigureAwait(false);

        Session? source = await ReadSessionAsync(sourceId, ct).ConfigureAwait(false);

        if (source is null)
        {
            return Result<Session>.Failure(new Error(
                ErrorCodes.Session.NotFound,
                "No session exists with that id."));
        }

        Entry? cutoffEntry = null;

        if (request.UpToEntryId is Guid cutoffId)
        {
            cutoffEntry = await ReadEntryAsync(sourceId, cutoffId, ct).ConfigureAwait(false);

            if (cutoffEntry is null)
            {
                return Result<Session>.Failure(new Error(
                    ErrorCodes.Session.EntryNotFound,
                    "upToEntryId does not identify an entry in the source session."));
            }
        }

        long maximumSourceEntrySequence = cutoffEntry?.Sequence
            ?? await ReadMaximumSequenceAsync(sourceId, ct).ConfigureAwait(false);

        Session fork = new()
        {
            Id = forkSessionId,
            CampaignId = request.CampaignId ?? source.CampaignId,
            Title = string.IsNullOrWhiteSpace(request.Title) ? BuildForkTitle(source.Title) : request.Title.Trim(),
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
            Summary = null,
            LastSummarizedMessageAt = null,
            TotalTokensUsed = 0,
            UnsummarizedEntryCount = 0,
            ForkedFromSessionId = sourceId,
        };

        try
        {
            await foreach (IReadOnlyList<SessionAttachmentRecord> sourcePage in attachments
                               .ReadBoundForForkPagesAsync(
                                   sourceId,
                                   maximumSourceEntrySequence,
                                   includeEntrylessAttachments: cutoffEntry is null,
                                   ct)
                               .ConfigureAwait(false))
            {
                List<SessionAttachmentForkCopyPlan> copyPlans = BuildForkAttachmentPlans(
                    forkSessionId,
                    sourcePage);

                await attachments
                    .CopyBytesForForkAsync(forkSessionId, copyPlans, ct)
                    .ConfigureAwait(false);
            }

            await using IDbContextTransaction tx =
                await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            try
            {
                await SqliteBusyRetry.ExecuteAsync(
                    () => InsertSessionAsync(fork, ct),
                    ct).ConfigureAwait(false);

                int copiedEntryCount = 0;

                await foreach (List<Entry> sourcePage in ReadEntryBatchesAsync(
                                   sourceId,
                                   ct,
                                   ForkEntryBatchSize,
                                   maximumSourceEntrySequence).ConfigureAwait(false))
                {
                    List<Entry> entryCopies = new(sourcePage.Count);

                    foreach (Entry sourceEntry in sourcePage)
                    {
                        entryCopies.Add(new Entry
                        {
                            Id = CreateForkEntryId(forkSessionId, sourceEntry.Id),
                            SessionId = forkSessionId,
                            Role = sourceEntry.Role,
                            Content = sourceEntry.Content,
                            ModelUsed = sourceEntry.ModelUsed,
                            CreatedAt = sourceEntry.CreatedAt,
                            Sequence = sourceEntry.Sequence,
                            ToolCallId = sourceEntry.ToolCallId,
                            ToolName = sourceEntry.ToolName,
                            ToolArguments = sourceEntry.ToolArguments,
                        });
                    }

                    await _entryPersistence
                        .InsertEntriesAsync(entryCopies, ct)
                        .ConfigureAwait(false);

                    copiedEntryCount = checked(copiedEntryCount + entryCopies.Count);
                }

                fork.UnsummarizedEntryCount = copiedEntryCount;

                await UpdateForkEntryCountAsync(fork.Id, copiedEntryCount, ct).ConfigureAwait(false);

                await foreach (IReadOnlyList<SessionAttachmentRecord> sourcePage in attachments
                                   .ReadBoundForForkPagesAsync(
                                       sourceId,
                                       maximumSourceEntrySequence,
                                       includeEntrylessAttachments: cutoffEntry is null,
                                       ct)
                                   .ConfigureAwait(false))
                {
                    List<SessionAttachmentForkCopyPlan> insertPlans = BuildForkAttachmentPlans(
                        forkSessionId,
                        sourcePage);

                    await attachments
                        .InsertForkRowsInAmbientTransactionAsync(
                            forkSessionId,
                            insertPlans,
                            ct)
                        .ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                throw;
            }
        }
        catch
        {
            _ = attachments.TryDeleteSessionDirectory(forkSessionId);

            throw;
        }

        if (attachmentIndexQueue is not null)
        {
            await foreach (IReadOnlyList<SessionAttachmentRecord> sourcePage in attachments
                               .ReadBoundForForkPagesAsync(
                                   sourceId,
                                   maximumSourceEntrySequence,
                                   includeEntrylessAttachments: cutoffEntry is null,
                                   CancellationToken.None)
                               .ConfigureAwait(false))
            {
                foreach (SessionAttachmentRecord sourceAttachment in sourcePage)
                {
                    _ = attachmentIndexQueue.TryEnqueue(new SessionAttachmentIndexRequest(
                        CreateForkAttachmentId(forkSessionId, sourceAttachment.Id),
                        forkSessionId));
                }
            }
        }

        return Result<Session>.Success(fork);
    }

    private static List<SessionAttachmentForkCopyPlan> BuildForkAttachmentPlans(
        Guid forkSessionId,
        IReadOnlyList<SessionAttachmentRecord> sourcePage)
    {
        List<SessionAttachmentForkCopyPlan> plans = new(sourcePage.Count);

        foreach (SessionAttachmentRecord sourceAttachment in sourcePage)
        {
            Guid? newEntryId = sourceAttachment.EntryId is Guid sourceEntryId
                ? CreateForkEntryId(forkSessionId, sourceEntryId)
                : null;

            plans.Add(new SessionAttachmentForkCopyPlan(
                sourceAttachment,
                CreateForkAttachmentId(forkSessionId, sourceAttachment.Id),
                newEntryId));
        }

        return plans;
    }

    private static Guid CreateForkEntryId(Guid forkSessionId, Guid sourceEntryId) =>
        CreateForkScopedId(forkSessionId, sourceEntryId, discriminator: 1);

    private static Guid CreateForkAttachmentId(Guid forkSessionId, Guid sourceAttachmentId) =>
        CreateForkScopedId(forkSessionId, sourceAttachmentId, discriminator: 2);

    private static Guid CreateForkScopedId(
        Guid forkSessionId,
        Guid sourceId,
        byte discriminator)
    {
        Span<byte> material = stackalloc byte[33];

        _ = forkSessionId.TryWriteBytes(material[..16]);

        _ = sourceId.TryWriteBytes(material[16..32]);

        material[32] = discriminator;

        Span<byte> digest = stackalloc byte[32];

        _ = SHA256.HashData(material, digest);

        return new Guid(digest[..16]);
    }

    private static string BuildForkTitle(string? sourceTitle) =>
        string.IsNullOrWhiteSpace(sourceTitle) ? "Forked session" : $"Fork of {sourceTitle}";

    public async Task<List<Entry>> GetEntriesAscendingAsync(Guid sessionId, int takeLast, CancellationToken ct = default)
    {
        int clampedTake = EntryWindowPolicy.ResolveTake(
            EntryWindowPolicy.EntryWindowKind.RawTakeLast,
            maxMessages: 0,
            requestedTake: takeLast);

        List<Entry> recentDescending = await EntryTemporalQueries
            .LoadRecentDescendingAsync(db, sessionId, clampedTake, ct)
            .ConfigureAwait(false);

        recentDescending.Reverse();

        return recentDescending;
    }

    public Task<Entry?> GetEntryAsync(Guid sessionId, Guid entryId, CancellationToken ct = default) =>
        ReadEntryAsync(sessionId, entryId, ct);

    public async Task<List<Entry>> GetEntriesAfterAsync(
        Guid sessionId,
        long afterSequence,
        int limit,
        CancellationToken ct = default)
    {
        int clampedLimit = ArcanumSettingClamps.SessionStreamReplayLimit(limit);

        return await EntryTemporalQueries
            .LoadAfterSequenceAsync(db, sessionId, afterSequence, clampedLimit, ct)
            .ConfigureAwait(false);
    }

    public async Task<List<Entry>> GetEntriesAsync(
        Guid sessionId,
        int offset = 0,
        int limit = 100,
        DateTimeOffset? beforeCreatedAt = null,
        Guid? beforeId = null,
        CancellationToken ct = default)
    {
        int clampedLimit = Math.Clamp(limit, 1, 1000);

        if (beforeCreatedAt is DateTimeOffset beforeAt && beforeId is Guid beforeEntryId)
        {
            long? beforeSequence = await EntryTemporalQueries
                .SequenceOfAsync(db, sessionId, beforeEntryId, ct)
                .ConfigureAwait(false);

            // A cursor entry deleted since the client read it has no sequence to page from, so fall
            // back to its timestamp/id position rather than reporting the end of the transcript.
            return beforeSequence is long cursorSequence
                ? await EntryTemporalQueries
                    .LoadBeforeSequenceAsync(db, sessionId, cursorSequence, clampedLimit, ct)
                    .ConfigureAwait(false)
                : await EntryTemporalQueries
                    .LoadBeforeDeletedKeysetAsync(
                        db,
                        sessionId,
                        beforeAt,
                        beforeEntryId,
                        clampedLimit,
                        ct)
                    .ConfigureAwait(false);
        }

        int clampedOffset = Math.Max(0, offset);

        return await EntryTemporalQueries
            .LoadDescendingPagedAsync(db, sessionId, clampedLimit, clampedOffset, ct)
            .ConfigureAwait(false);
    }

    public Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken ct) =>
        _entryPersistence.GetEntryCountAsync(sessionId, ct);

    public async Task UpdateSessionAsync(Session session, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await ExecuteNonQueryAsync(
            """
            UPDATE "Sessions"
            SET "Title" = $title, "Status" = $status, "UpdatedAt" = $updatedAt
            WHERE "Id" = $id;
            """,
            command =>
            {
                GrimoireEntitySql.AddParameter(command, "$title", session.Title);
                GrimoireEntitySql.AddParameter(command, "$status", session.Status);
                GrimoireEntitySql.AddParameter(command, "$updatedAt", GrimoireEntitySql.Format(now));
                GrimoireEntitySql.AddParameter(command, "$id", Format(session.Id));
            },
            ct).ConfigureAwait(false);

        session.UpdatedAt = now;
    }

    public async Task ArchiveAsync(Guid id, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await ExecuteNonQueryAsync(
            """
            UPDATE "Sessions"
            SET "Status" = 'archived', "UpdatedAt" = $updatedAt
            WHERE "Id" = $id;
            """,
            command =>
            {
                GrimoireEntitySql.AddParameter(command, "$updatedAt", GrimoireEntitySql.Format(now));
                GrimoireEntitySql.AddParameter(command, "$id", Format(id));
            },
            ct).ConfigureAwait(false);
    }

    private async Task<string> SerializeJsonExportAsync(Session session, Guid sessionId, CancellationToken ct)
    {
        // W3.4 Group E #10: stream-serialize the export instead of accumulating every entry
        // batch into one List<Entry> before serializing. Each batch's entries are written to a
        // Utf8JsonWriter as they are read, so the peak managed-memory pressure is one batch
        // (ExportEntryBatchSize) rather than the whole session. The wire shape is identical to
        // the previous JsonSerializer.Serialize(SessionExportPayload) output: a camelCase
        // { "session": {...}, "entries": [...] } object. The endpoint buffers the result into a
        // string (SessionExportResult.Content), so the output string is still O(total) — that
        // is inherent to the wire contract, not the accumulation. Source-generated type infos
        // (ArcanumCoreJsonContext) keep this AOT-safe.
        using MemoryStream buffer = new();

        using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();

        writer.WritePropertyName("session");

        JsonSerializer.Serialize(writer, session, ArcanumCoreJsonContext.Default.Session);

        writer.WritePropertyName("entries");

        writer.WriteStartArray();

        await foreach (List<Entry> batch in ReadEntryBatchesAsync(sessionId, ct).ConfigureAwait(false))
        {
            foreach (Entry entry in batch)
            {
                JsonSerializer.Serialize(writer, entry, ArcanumCoreJsonContext.Default.Entry);
            }
        }

        writer.WriteEndArray();

        writer.WriteEndObject();

        await writer.FlushAsync(ct).ConfigureAwait(false);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<string> FormatMarkdownExportAsync(Session session, Guid sessionId, CancellationToken ct)
    {
        StringBuilder builder = new();

        builder.Append("# ");

        builder.AppendLine(session.Title ?? "Untitled session");

        builder.AppendLine();

        await foreach (List<Entry> batch in ReadEntryBatchesAsync(sessionId, ct).ConfigureAwait(false))
        {
            AppendMarkdownEntries(builder, batch);
        }

        return builder.ToString();
    }

    private async IAsyncEnumerable<List<Entry>> ReadEntryBatchesAsync(
        Guid sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        int batchSize = ExportEntryBatchSize,
        long maximumSequence = long.MaxValue)
    {
        long cursorSequence = 0L;

        while (true)
        {
            List<Entry> batch = await EntryTemporalQueries
                .LoadAfterSequenceAsync(
                    db,
                    sessionId,
                    cursorSequence,
                    batchSize,
                    ct,
                    maximumSequence)
                .ConfigureAwait(false);

            if (batch.Count == 0)
            {
                yield break;
            }

            yield return batch;

            Entry last = batch[^1];

            cursorSequence = last.Sequence;

            if (batch.Count < batchSize)
            {
                yield break;
            }
        }
    }

    private async Task InsertSessionAsync(Session session, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            """
            INSERT INTO "Sessions"
                ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt", "Summary",
                 "LastSummarizedMessageAt", "TotalTokensUsed", "TotalCostUsd",
                 "UnsummarizedEntryCount", "ForkedFromSessionId")
            VALUES
                ($id, $campaignId, $title, $status, $createdAt, $updatedAt, $summary,
                 $lastSummarizedAt, $totalTokens, $totalCost, $unsummarizedCount, $forkedFrom);
            """,
            command => BindSession(command, session),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Session?> ReadSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            $"SELECT {GrimoireEntitySql.SessionColumns} FROM \"Sessions\" WHERE \"Id\" = $id LIMIT 1;",
            cancellationToken).ConfigureAwait(false);
        GrimoireEntitySql.AddParameter(command, "$id", Format(id));
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? GrimoireEntitySql.ReadSession(reader)
            : null;
    }

    private async Task<List<Session>> ReadSessionsAsync(
        string commandText,
        IReadOnlyList<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            commandText,
            cancellationToken).ConfigureAwait(false);

        foreach ((string name, object value) in parameters)
        {
            GrimoireEntitySql.AddParameter(command, name, value);
        }

        List<Session> sessions = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(GrimoireEntitySql.ReadSession(reader));
        }

        return sessions;
    }

    private async Task<Dictionary<Guid, int>> ReadEntryCountsAsync(
        IReadOnlyList<Guid> sessionIds,
        CancellationToken cancellationToken)
    {
        string[] placeholders = new string[sessionIds.Count];

        for (int index = 0; index < sessionIds.Count; index++)
        {
            string name = "$id" + index.ToString(CultureInfo.InvariantCulture);
            placeholders[index] = name;
        }

        string commandText =
            "SELECT \"SessionId\", COUNT(*) FROM \"Entries\" WHERE \"SessionId\" IN ("
            + string.Join(", ", placeholders)
            + ") GROUP BY \"SessionId\";";
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            commandText,
            cancellationToken).ConfigureAwait(false);

        for (int index = 0; index < sessionIds.Count; index++)
        {
            GrimoireEntitySql.AddParameter(command, placeholders[index], Format(sessionIds[index]));
        }

        Dictionary<Guid, int> counts = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts.Add(
                GrimoireEntitySql.ReadGuid(reader, 0),
                reader.GetInt32(1));
        }

        return counts;
    }

    private async Task<SessionStatsRow> ReadSessionStatsAsync(CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN "Status" = 'active' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN "Status" = 'archived' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM("TotalTokensUsed"), 0)
            FROM "Sessions";
            """,
            cancellationToken).ConfigureAwait(false);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new SessionStatsRow(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt64(3));
    }

    private async Task<EntryStatsRow> ReadEntryStatsAsync(CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN "Role" = $user THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN "Role" = $assistant THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN "Role" = $tool THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN "Role" = $system THEN 1 ELSE 0 END), 0)
            FROM "Entries";
            """,
            cancellationToken).ConfigureAwait(false);
        GrimoireEntitySql.AddParameter(command, "$user", (int)MessageRole.User);
        GrimoireEntitySql.AddParameter(command, "$assistant", (int)MessageRole.Assistant);
        GrimoireEntitySql.AddParameter(command, "$tool", (int)MessageRole.Tool);
        GrimoireEntitySql.AddParameter(command, "$system", (int)MessageRole.System);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new EntryStatsRow(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    private async Task<Dictionary<string, int>> ReadEntryCountsByModelAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            """
            SELECT "ModelUsed", COUNT(*)
            FROM "Entries"
            WHERE "ModelUsed" <> ''
            GROUP BY "ModelUsed"
            ORDER BY "ModelUsed" COLLATE BINARY;
            """,
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string model = reader.GetString(0);

            int count = reader.GetInt32(1);

            counts[model] = checked(counts.GetValueOrDefault(model) + count);
        }

        return counts;
    }

    private async Task<Entry?> ReadEntryAsync(
        Guid sessionId,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            $"SELECT {GrimoireEntitySql.EntryColumns} FROM \"Entries\" "
            + "WHERE \"SessionId\" = $sessionId AND \"Id\" = $entryId LIMIT 1;",
            cancellationToken).ConfigureAwait(false);
        GrimoireEntitySql.AddParameter(command, "$sessionId", Format(sessionId));
        GrimoireEntitySql.AddParameter(command, "$entryId", Format(entryId));
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? GrimoireEntitySql.ReadEntry(reader)
            : null;
    }

    private async Task<long> ReadMaximumSequenceAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            "SELECT COALESCE(MAX(\"Sequence\"), 0) FROM \"Entries\" WHERE \"SessionId\" = $sessionId;",
            cancellationToken).ConfigureAwait(false);
        GrimoireEntitySql.AddParameter(command, "$sessionId", Format(sessionId));
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private Task UpdateSessionAfterEntryAsync(
        Session session,
        string? automaticTitle,
        CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(
            """
            UPDATE "Sessions"
            SET "Title" =
                    CASE
                        WHEN $automaticTitle IS NOT NULL
                             AND "Title" IS $observedTitle
                            THEN $automaticTitle
                        ELSE "Title"
                    END,
                "UpdatedAt" = $updatedAt,
                "UnsummarizedEntryCount" =
                    CASE
                        WHEN "UnsummarizedEntryCount" >= 0 THEN "UnsummarizedEntryCount" + 1
                        ELSE "UnsummarizedEntryCount"
                    END
            WHERE "Id" = $id;
            """,
            command =>
            {
                GrimoireEntitySql.AddParameter(command, "$automaticTitle", automaticTitle);
                GrimoireEntitySql.AddParameter(command, "$observedTitle", session.Title);
                GrimoireEntitySql.AddParameter(
                    command,
                    "$updatedAt",
                    GrimoireEntitySql.Format(session.UpdatedAt));
                GrimoireEntitySql.AddParameter(command, "$id", Format(session.Id));
            },
            cancellationToken);

    private Task UpdateForkEntryCountAsync(
        Guid sessionId,
        int copiedEntryCount,
        CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(
            "UPDATE \"Sessions\" SET \"UnsummarizedEntryCount\" = $count WHERE \"Id\" = $id;",
            command =>
            {
                GrimoireEntitySql.AddParameter(command, "$count", copiedEntryCount);
                GrimoireEntitySql.AddParameter(command, "$id", Format(sessionId));
            },
            cancellationToken);

    private async Task ExecuteNonQueryAsync(
        string commandText,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            commandText,
            cancellationToken).ConfigureAwait(false);
        bind(command);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindSession(SqliteCommand command, Session session)
    {
        GrimoireEntitySql.AddParameter(command, "$id", Format(session.Id));
        GrimoireEntitySql.AddParameter(
            command,
            "$campaignId",
            session.CampaignId is { } campaignId ? Format(campaignId) : null);
        GrimoireEntitySql.AddParameter(command, "$title", session.Title);
        GrimoireEntitySql.AddParameter(command, "$status", session.Status);
        GrimoireEntitySql.AddParameter(command, "$createdAt", GrimoireEntitySql.Format(session.CreatedAt));
        GrimoireEntitySql.AddParameter(command, "$updatedAt", GrimoireEntitySql.Format(session.UpdatedAt));
        GrimoireEntitySql.AddParameter(command, "$summary", session.Summary);
        GrimoireEntitySql.AddParameter(
            command,
            "$lastSummarizedAt",
            session.LastSummarizedMessageAt is { } lastSummarizedAt
                ? GrimoireEntitySql.Format(lastSummarizedAt)
                : null);
        GrimoireEntitySql.AddParameter(command, "$totalTokens", session.TotalTokensUsed);
        _ = ExactUsdText.AddParameter(command, "$totalCost", session.TotalCostUsd);
        GrimoireEntitySql.AddParameter(command, "$unsummarizedCount", session.UnsummarizedEntryCount);
        GrimoireEntitySql.AddParameter(
            command,
            "$forkedFrom",
            session.ForkedFromSessionId is { } sourceId ? Format(sourceId) : null);
    }

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    private static void AppendMarkdownEntries(StringBuilder builder, IReadOnlyList<Entry> entries)
    {
        foreach (Entry entry in entries)
        {
            builder.Append("## ");

            builder.Append(entry.Role.ToString().ToLowerInvariant());

            builder.Append(" — ");

            builder.AppendLine(entry.CreatedAt.ToString("O"));

            builder.AppendLine();

            builder.AppendLine(entry.Content);

            builder.AppendLine();
        }
    }

    private static string TruncateTitle(string content)
    {
        string trimmed = content.Trim();

        const int maxLen = 80;

        if (trimmed.Length <= maxLen)
        {
            return trimmed;
        }

        int cut = trimmed.LastIndexOf(' ', maxLen);

        if (cut < 20)
        {
            cut = maxLen;
        }

        return trimmed[..cut].TrimEnd() + "...";
    }

    private sealed record SessionTieGroup(
        List<Session> Sessions,
        bool HasOlderSessions);

    private sealed record SessionStatsRow(
        int TotalSessions,
        int ActiveSessions,
        int ArchivedSessions,
        long TotalTokensUsed);

    private sealed record EntryStatsRow(
        int TotalEntries,
        int UserEntries,
        int AssistantEntries,
        int ToolEntries,
        int SystemEntries);
}
