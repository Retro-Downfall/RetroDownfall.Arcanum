using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed class SessionRepository(
    ArcanumDbContext db,
    ISessionAttachmentStore attachments,
    IOptionsMonitor<ArcanumSettings> optionsMonitor) : ISessionRepository
{

    private const int ExportEntryBatchSize = 500;

    private const int FtsSessionIdLimit = 2048;

    private readonly SessionEntryPersistence _entryPersistence = new(db);

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

        db.Sessions.Add(session);

        _ = await EfSaveChangesRetry
            .ExecuteAsync(db, ct)
            .ConfigureAwait(false);

        return session;
    }

    public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<SessionQueryResult> QueryAsync(SessionQueryRequest request, CancellationToken ct)
    {
        SessionSettings settings = optionsMonitor.CurrentValue.Sessions ?? new SessionSettings();

        int limit = ArcanumSettingClamps.SessionQueryLimit(
            request.Limit ?? settings.DefaultQueryLimit ?? new SessionSettings().DefaultQueryLimit!.Value);

        string statusFilter = string.IsNullOrWhiteSpace(request.Status) ? "active" : request.Status.Trim();

        string? searchTitlePattern = null;

        HashSet<Guid>? ftsSessionIds = null;

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string search = request.Search.Trim();

            int maxQueryLen = ArcanumSettingClamps.ArchiveSearchMaxQueryLength(
                optionsMonitor.CurrentValue.Intelligence.ArchiveSearchMaxQueryLength);

            if (search.Length > maxQueryLen)
            {
                search = search[..maxQueryLen];
            }

            searchTitlePattern = SqlLikePatterns.Contains(search);

            string matchQuery = FtsMatchQuerySanitizer.Sanitize(search);

            if (!string.IsNullOrEmpty(matchQuery))
            {
                ftsSessionIds = await ResolveFtsSessionIdsAsync(matchQuery, ct).ConfigureAwait(false);
            }
        }

        // The EF Core SQLite provider cannot ORDER BY or compare a DateTimeOffset column in
        // LINQ, so the session list is composed as parameterized SQL over the sortable UTC
        // text columns. Every {n} placeholder is bound to the positional parameter array
        // (injection-safe); no user-supplied value is ever concatenated into the SQL text.
        List<object> parameters = [];

        string Bind(object value)
        {
            string placeholder = string.Concat("{", parameters.Count.ToString(CultureInfo.InvariantCulture), "}");

            parameters.Add(value);

            return placeholder;
        }

        List<string> conditions = [];

        if (!string.Equals(statusFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add($"\"Status\" = {Bind(statusFilter)}");
        }

        if (request.CampaignId is Guid campaignId)
        {
            conditions.Add($"\"CampaignId\" = {Bind(campaignId)}");
        }

        if (request.From is DateTimeOffset from)
        {
            conditions.Add($"\"UpdatedAt\" >= {Bind(from.ToUniversalTime())}");
        }

        if (request.To is DateTimeOffset to)
        {
            conditions.Add($"\"UpdatedAt\" <= {Bind(to.ToUniversalTime())}");
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

            if (ftsSessionIds is { Count: > 0 })
            {
                conditions.Add(
                    $"(({titleClause}) OR \"Id\" COLLATE NOCASE IN (SELECT value FROM json_each({Bind(SerializeGuidJsonArray(ftsSessionIds))})))");
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
            conditions.Add($"\"UpdatedAt\" < {Bind(before.ToUniversalTime())}");
        }

        StringBuilder sqlBuilder = new();

        sqlBuilder.Append("SELECT * FROM \"Sessions\"");

        if (conditions.Count > 0)
        {
            sqlBuilder.Append(" WHERE ");

            sqlBuilder.Append(string.Join(" AND ", conditions));
        }

        sqlBuilder.Append($" ORDER BY \"UpdatedAt\" DESC LIMIT {Bind(limit + 1)}");

        List<Session> page = await db.Sessions
            .FromSqlRaw(sqlBuilder.ToString(), parameters.ToArray())
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        bool hasMore = page.Count > limit;

        if (hasMore)
        {
            page = page.Take(limit).ToList();
        }

        Guid[] sessionIds = page.Select(s => s.Id).ToArray();

        Dictionary<Guid, int> entryCounts = [];

        if (sessionIds.Length > 0)
        {

            List<Guid> entrySessionIds = await db.Entries
                .AsNoTracking()
                .Where(e => sessionIds.Contains(e.SessionId))
                .Select(e => e.SessionId)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            entryCounts = entrySessionIds
                .GroupBy(id => id)
                .ToDictionary(g => g.Key, g => g.Count());

        }

        SessionSummaryDto[] summaries = page
            .Select(s => new SessionSummaryDto(
                s.Id,
                s.CampaignId,
                s.Title,
                s.Status,
                entryCounts.GetValueOrDefault(s.Id),
                s.CreatedAt,
                s.UpdatedAt))
            .ToArray();

        DateTimeOffset? nextBefore = hasMore && page.Count > 0 ? page[^1].UpdatedAt : null;

        return new SessionQueryResult(summaries, nextBefore, hasMore);
    }

    public async Task<SessionAnalytics> GetAnalyticsAsync(CancellationToken ct)
    {

        var sessionStats = await db.Sessions
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalSessions = g.Count(),
                ActiveSessions = g.Sum(s => s.Status == "active" ? 1 : 0),
                ArchivedSessions = g.Sum(s => s.Status == "archived" ? 1 : 0),
                TotalTokensUsed = g.Sum(s => s.TotalTokensUsed),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var entryStats = await db.Entries
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalEntries = g.Count(),
                UserEntries = g.Sum(e => e.Role == MessageRole.User ? 1 : 0),
                AssistantEntries = g.Sum(e => e.Role == MessageRole.Assistant ? 1 : 0),
                ToolEntries = g.Sum(e => e.Role == MessageRole.Tool ? 1 : 0),
                SystemEntries = g.Sum(e => e.Role == MessageRole.System ? 1 : 0),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        Dictionary<string, int> entriesByModel = await db.Entries
            .AsNoTracking()
            .Where(e => e.ModelUsed != "")
            .GroupBy(e => e.ModelUsed)
            .Select(g => new { Model = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Model, x => x.Count, StringComparer.OrdinalIgnoreCase, ct)
            .ConfigureAwait(false);

        return new SessionAnalytics(
            sessionStats?.TotalSessions ?? 0,
            sessionStats?.ActiveSessions ?? 0,
            sessionStats?.ArchivedSessions ?? 0,
            entryStats?.TotalEntries ?? 0,
            entryStats?.UserEntries ?? 0,
            entryStats?.AssistantEntries ?? 0,
            entryStats?.ToolEntries ?? 0,
            entryStats?.SystemEntries ?? 0,
            sessionStats?.TotalTokensUsed ?? 0L,
            entriesByModel);

    }

    public async Task<Result<SessionExportResult>> ExportAsync(Guid id, SessionExportFormat format, CancellationToken ct)
    {
        Session? session = await db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false);

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
                new Error("Session.InvalidFormat", "The export format is not supported.")),
        };
    }

    public async Task<Result<Entry>> AddEntryAsync(Guid sessionId, Entry entry, CancellationToken ct)
    {

        using IDisposable _ = await SessionEntryPersistence.AcquireWriteLockAsync(sessionId, ct).ConfigureAwait(false);

        Session? session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct).ConfigureAwait(false);

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

        SessionSettings sessionSettings = optionsMonitor.CurrentValue.Sessions ?? new SessionSettings();

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

        db.Entries.Add(entry);

        session.UpdatedAt = now;

        if (string.IsNullOrWhiteSpace(session.Title)
            && entry.Role == MessageRole.User
            && !string.IsNullOrWhiteSpace(entry.Content))
        {

            session.Title = TruncateTitle(entry.Content);

        }

        // Maintain the unsummarized-entry counter so The Forge append path no longer drifts
        // it (the inference path already does this). -1 means "unknown legacy"; leave it.
        await _entryPersistence.SaveChangesWithRetryAsync(ct).ConfigureAwait(false);

        await _entryPersistence.IncrementUnsummarizedEntryCountIfKnownAsync(sessionId, 1, ct).ConfigureAwait(false);

        return Result<Entry>.Success(entry);
    }

    public async Task<Result<Session>> ForkAsync(Guid sourceId, ForkSessionRequest request, CancellationToken ct)
    {

        Session? source = await db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct)
            .ConfigureAwait(false);

        if (source is null)
        {

            return Result<Session>.Failure(new Error(ErrorCodes.Session.NotFound, "No session exists with that id."));

        }

        Entry? cutoffEntry = null;

        if (request.UpToEntryId is Guid cutoffId)
        {

            cutoffEntry = await db.Entries
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.SessionId == sourceId && e.Id == cutoffId, ct)
                .ConfigureAwait(false);

            if (cutoffEntry is null)
            {

                return Result<Session>.Failure(new Error(
                    ErrorCodes.Session.EntryNotFound,
                    "upToEntryId does not identify an entry in the source session."));

            }

        }

        SessionSettings sessionSettings = optionsMonitor.CurrentValue.Sessions ?? new SessionSettings();

        int maxForkDepth = ArcanumSettingClamps.MaxForkDepth(sessionSettings.MaxForkDepth);

        int sourceDepth = await ComputeForkDepthAsync(source.ForkedFromSessionId, ct).ConfigureAwait(false);

        if (sourceDepth + 1 > maxForkDepth)
        {

            return Result<Session>.Failure(new Error(
                ErrorCodes.Session.ForkDepthExceeded,
                $"Forking this session would exceed the maximum fork depth of {maxForkDepth}."));

        }

        int maxEntries = ArcanumSettingClamps.MaxEntriesPerSession(sessionSettings.MaxEntriesPerSession);

        int entriesToCopy = cutoffEntry is null
            ? await db.Entries.AsNoTracking().CountAsync(e => e.SessionId == sourceId, ct).ConfigureAwait(false)
            : await EntryTemporalQueries
                .CountAtOrBeforeKeyset(db, sourceId, cutoffEntry.CreatedAt, cutoffEntry.Id)
                .FirstAsync(ct)
                .ConfigureAwait(false);

        if (entriesToCopy > maxEntries)
        {

            return Result<Session>.Failure(new Error(
                ErrorCodes.Session.TooManyEntries,
                $"Session cannot exceed {maxEntries} entries; the source has {entriesToCopy} up to the requested cutoff."));

        }

        // Allocate all ids in memory before any durable write.
        Guid forkSessionId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Dictionary<Guid, Guid> entryIdMap = new();

        List<Entry> entryCopies = [];

        await foreach (List<Entry> batch in ReadEntryBatchesAsync(sourceId, ct).ConfigureAwait(false))
        {

            bool reachedCutoff = false;

            foreach (Entry sourceEntry in batch)
            {

                Guid newEntryId = Guid.NewGuid();

                entryIdMap[sourceEntry.Id] = newEntryId;

                entryCopies.Add(new Entry
                {
                    Id = newEntryId,
                    SessionId = forkSessionId,
                    Role = sourceEntry.Role,
                    Content = sourceEntry.Content,
                    ModelUsed = sourceEntry.ModelUsed,
                    CreatedAt = sourceEntry.CreatedAt,
                    ToolCallId = sourceEntry.ToolCallId,
                    ToolName = sourceEntry.ToolName,
                    ToolArguments = sourceEntry.ToolArguments,
                });

                if (cutoffEntry is not null && sourceEntry.Id == cutoffEntry.Id)
                {

                    reachedCutoff = true;

                    break;

                }

            }

            if (reachedCutoff)
            {

                break;

            }

        }

        IReadOnlySet<Guid>? copiedSourceEntryIds = cutoffEntry is null
            ? null
            : entryIdMap.Keys.ToHashSet();

        IReadOnlyList<SessionAttachmentRecord> sourceAttachments = await attachments
            .ListBoundForForkAsync(sourceId, copiedSourceEntryIds, ct)
            .ConfigureAwait(false);

        List<SessionAttachmentForkCopyPlan> attachmentPlans = [];

        foreach (SessionAttachmentRecord sourceAttachment in sourceAttachments)
        {

            Guid? newEntryId = sourceAttachment.EntryId is Guid sourceEntryId
                && entryIdMap.TryGetValue(sourceEntryId, out Guid mapped)
                    ? mapped
                    : null;

            attachmentPlans.Add(new SessionAttachmentForkCopyPlan(
                sourceAttachment,
                Guid.NewGuid(),
                newEntryId));

        }

        // Filesystem first: copy + hash-verify before opening the DB write transaction.
        await attachments.CopyBytesForForkAsync(forkSessionId, attachmentPlans, ct).ConfigureAwait(false);

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
            UnsummarizedEntryCount = entryCopies.Count,
            ForkedFromSessionId = sourceId,
        };

        // Acquire source then fork session gates (stable Guid order to avoid deadlocks with concurrent purge).
        Guid firstGate = sourceId.CompareTo(forkSessionId) <= 0 ? sourceId : forkSessionId;

        Guid secondGate = firstGate == sourceId ? forkSessionId : sourceId;

        using IDisposable gate1 = await attachments.AcquireSessionGateAsync(firstGate, ct).ConfigureAwait(false);

        using IDisposable gate2 = await attachments.AcquireSessionGateAsync(secondGate, ct).ConfigureAwait(false);

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {

            db.Sessions.Add(fork);

            foreach (Entry copy in entryCopies)
            {

                db.Entries.Add(copy);

            }

            await _entryPersistence.SaveChangesWithRetryAsync(ct).ConfigureAwait(false);

            await attachments
                .InsertForkRowsInAmbientTransactionAsync(forkSessionId, attachmentPlans, ct)
                .ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);

        }
        catch
        {

            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            _ = attachments.TryDeleteSessionDirectory(forkSessionId);

            throw;

        }

        return Result<Session>.Success(fork);

    }

    /// <summary>
    /// Walks the fork lineage chain starting from <paramref name="forkedFromSessionId"/>, counting
    /// hops to the root — mirrors <c>ConclaveLineage</c>'s role for Apprentice delegation trees. A
    /// session that was never forked has depth <c>0</c>. Capped defensively at 64 hops (well above
    /// any realistic <c>MaxForkDepth</c>) so a corrupted/cyclic chain cannot loop forever.
    /// </summary>
    private async Task<int> ComputeForkDepthAsync(Guid? forkedFromSessionId, CancellationToken ct)
    {

        const int safetyCap = 64;

        int depth = 0;

        Guid? currentId = forkedFromSessionId;

        while (currentId is Guid id && depth < safetyCap)
        {

            depth++;

            currentId = await db.Sessions
                .AsNoTracking()
                .Where(s => s.Id == id)
                .Select(s => s.ForkedFromSessionId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

        }

        return depth;

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
            .LoadRecentDescending(db, sessionId, clampedTake)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        recentDescending.Reverse();

        return recentDescending;
    }

    public async Task<Entry?> GetEntryAsync(Guid sessionId, Guid entryId, CancellationToken ct = default) =>
        await db.Entries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.SessionId == sessionId && e.Id == entryId, ct)
            .ConfigureAwait(false);

    public async Task<List<Entry>> GetEntriesAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAt,
        Guid afterId,
        int limit,
        CancellationToken ct = default)
    {
        int clampedLimit = ArcanumSettingClamps.SessionStreamReplayLimit(limit);

        return await EntryTemporalQueries
            .LoadAfterKeyset(db, sessionId, afterCreatedAt, afterId, clampedLimit)
            .AsNoTracking()
            .ToListAsync(ct)
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

            return await EntryTemporalQueries
                .LoadBeforeKeyset(db, sessionId, beforeAt, beforeEntryId, clampedLimit)
                .AsNoTracking()
                .ToListAsync(ct)
                .ConfigureAwait(false);

        }

        int clampedOffset = Math.Max(0, offset);

        return await EntryTemporalQueries
            .LoadDescendingPaged(db, sessionId, clampedLimit, clampedOffset)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken ct) =>
        db.Entries.CountAsync(e => e.SessionId == sessionId, ct);

    public async Task UpdateSessionAsync(Session session, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Patch only Forge-owned scalar fields; Grimoire-owned counters and rollups are excluded.
        _ = await db.Sessions
            .Where(s => s.Id == session.Id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.Title, session.Title)
                    .SetProperty(x => x.Status, session.Status)
                    .SetProperty(x => x.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        session.UpdatedAt = now;
    }

    public async Task ArchiveAsync(Guid id, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = await db.Sessions
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.Status, "archived")
                    .SetProperty(x => x.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);
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
        // (TheForgeJsonContext) keep this AOT-safe.
        using MemoryStream buffer = new();

        using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();

        writer.WritePropertyName("session");

        JsonSerializer.Serialize(writer, session, TheForgeJsonContext.Default.Session);

        writer.WritePropertyName("entries");

        writer.WriteStartArray();

        await foreach (List<Entry> batch in ReadEntryBatchesAsync(sessionId, ct).ConfigureAwait(false))
        {

            foreach (Entry entry in batch)
            {

                JsonSerializer.Serialize(writer, entry, TheForgeJsonContext.Default.Entry);

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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        DateTimeOffset cursorCreatedAt = DateTimeOffset.MinValue;

        Guid cursorId = Guid.Empty;

        while (true)
        {
            List<Entry> batch = await EntryTemporalQueries
                .LoadAfterKeyset(db, sessionId, cursorCreatedAt, cursorId, ExportEntryBatchSize)
                .AsNoTracking()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (batch.Count == 0)
            {
                yield break;
            }

            yield return batch;

            Entry last = batch[^1];

            cursorCreatedAt = last.CreatedAt;

            cursorId = last.Id;

            if (batch.Count < ExportEntryBatchSize)
            {
                yield break;
            }
        }
    }

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

    private async Task<HashSet<Guid>> ResolveFtsSessionIdsAsync(string matchQuery, CancellationToken ct)
    {
        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            SELECT DISTINCT c."SessionId"
            FROM "Entries_fts"
            INNER JOIN "Entries" AS c ON c."Id" = "Entries_fts"."Id"
            WHERE "Entries_fts" MATCH @query
            LIMIT @limit
            """;

        DbParameter pQuery = cmd.CreateParameter();

        pQuery.ParameterName = "@query";

        pQuery.Value = matchQuery;

        cmd.Parameters.Add(pQuery);

        DbParameter pLimit = cmd.CreateParameter();

        pLimit.ParameterName = "@limit";

        pLimit.Value = FtsSessionIdLimit;

        cmd.Parameters.Add(pLimit);

        HashSet<Guid> sessionIds = [];

        try
        {
            await using DbDataReader reader = await cmd
                .ExecuteReaderAsync(ct)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                _ = sessionIds.Add(reader.GetGuid(0));
            }
        }
        catch (SqliteException)
        {
            return [];
        }

        return sessionIds;
    }

    private static string SerializeGuidJsonArray(IEnumerable<Guid> ids)
    {
        // GUID text only ever contains [0-9a-f-], so the JSON array can be assembled without
        // escaping. The result is bound as a single SQL parameter and parsed by json_each,
        // and the IN-clause uses COLLATE NOCASE so EF's stored GUID casing is irrelevant.
        StringBuilder builder = new();

        _ = builder.Append('[');

        bool first = true;

        foreach (Guid id in ids)
        {
            if (!first)
            {
                _ = builder.Append(',');
            }

            _ = builder.Append('"').Append(id.ToString()).Append('"');

            first = false;
        }

        _ = builder.Append(']');

        return builder.ToString();
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

}
