using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed class SessionRepository(
    ArcanumDbContext db,
    IOptionsMonitor<ArcanumSettings> optionsMonitor) : ISessionRepository
{

    private const int ExportEntryBatchSize = 500;

    private const int FtsSessionIdLimit = 2048;

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

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return session;
    }

    public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<SessionQueryResult> QueryAsync(SessionQueryRequest request, CancellationToken ct)
    {
        SessionSettings settings = optionsMonitor.CurrentValue.Sessions ?? new SessionSettings();

        int limit = ArcanumSettingClamps.SessionQueryLimit(
            request.Limit ?? settings.DefaultQueryLimit ?? new SessionSettings().DefaultQueryLimit!.Value);

        IQueryable<Session> query = db.Sessions.AsNoTracking();

        string statusFilter = string.IsNullOrWhiteSpace(request.Status) ? "active" : request.Status.Trim();

        if (!string.Equals(statusFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(s => s.Status == statusFilter);
        }

        if (request.CampaignId is Guid campaignId)
        {
            query = query.Where(s => s.CampaignId == campaignId);
        }

        if (request.From is DateTimeOffset from)
        {
            query = query.Where(s => s.UpdatedAt >= from);
        }

        if (request.To is DateTimeOffset to)
        {
            query = query.Where(s => s.UpdatedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            string titlePattern = SqlLikePatterns.Contains(request.Title.Trim());

            query = query.Where(s =>
                s.Title != null
                && EF.Functions.Like(s.Title, titlePattern, SqlLikePatterns.EscapeString));
        }

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

            string titlePattern = SqlLikePatterns.Contains(search);

            string matchQuery = FtsMatchQuerySanitizer.Sanitize(search);

            if (!string.IsNullOrEmpty(matchQuery))
            {
                ftsSessionIds = await ResolveFtsSessionIdsAsync(matchQuery, ct).ConfigureAwait(false);
            }

            if (ftsSessionIds is { Count: > 0 })
            {
                query = query.Where(s =>
                    (s.Title != null
                        && EF.Functions.Like(s.Title, titlePattern, SqlLikePatterns.EscapeString))
                    || ftsSessionIds.Contains(s.Id));
            }
            else
            {
                query = query.Where(s =>
                    s.Title != null
                    && EF.Functions.Like(s.Title, titlePattern, SqlLikePatterns.EscapeString));
            }
        }

        if (request.Role is MessageRole role)
        {
            query = query.Where(s => s.Entries.Any(e => e.Role == role));
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            string modelPattern = SqlLikePatterns.EscapeLiteral(request.Model.Trim());

            query = query.Where(s =>
                s.Entries.Any(e =>
                    EF.Functions.Like(e.ModelUsed, modelPattern, SqlLikePatterns.EscapeString)));
        }

        if (request.BeforeUpdatedAt is DateTimeOffset before)
        {
            query = query.Where(s => s.UpdatedAt < before);
        }

        List<Session> page = await query
            .OrderByDescending(s => s.UpdatedAt)
            .Take(limit + 1)
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

        int totalSessions = await db.Sessions.AsNoTracking().CountAsync(ct).ConfigureAwait(false);

        int activeSessions = await db.Sessions.AsNoTracking().CountAsync(s => s.Status == "active", ct).ConfigureAwait(false);

        int archivedSessions = await db.Sessions.AsNoTracking().CountAsync(s => s.Status == "archived", ct).ConfigureAwait(false);

        long totalTokensUsed = await db.Sessions.AsNoTracking().SumAsync(s => s.TotalTokensUsed, ct).ConfigureAwait(false);

        int totalEntries = await db.Entries.AsNoTracking().CountAsync(ct).ConfigureAwait(false);

        int userEntries = await db.Entries.AsNoTracking().CountAsync(e => e.Role == MessageRole.User, ct).ConfigureAwait(false);

        int assistantEntries = await db.Entries.AsNoTracking().CountAsync(e => e.Role == MessageRole.Assistant, ct).ConfigureAwait(false);

        int toolEntries = await db.Entries.AsNoTracking().CountAsync(e => e.Role == MessageRole.Tool, ct).ConfigureAwait(false);

        int systemEntries = await db.Entries.AsNoTracking().CountAsync(e => e.Role == MessageRole.System, ct).ConfigureAwait(false);

        List<string> modelNames = await db.Entries
            .AsNoTracking()
            .Where(e => e.ModelUsed != "")
            .Select(e => e.ModelUsed)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        Dictionary<string, int> entriesByModel = modelNames
            .GroupBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new SessionAnalytics(
            totalSessions,
            activeSessions,
            archivedSessions,
            totalEntries,
            userEntries,
            assistantEntries,
            toolEntries,
            systemEntries,
            totalTokensUsed,
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
                new Error("Session.NotFound", "No session exists with that id."));
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

    public async Task<Entry> AddEntryAsync(Guid sessionId, Entry entry, CancellationToken ct)
    {
        Session? session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct).ConfigureAwait(false);

        if (session is null)
        {
            throw new InvalidOperationException($"Session {sessionId} was not found.");
        }

        if (string.Equals(session.Status, "archived", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cannot append entries to an archived session.");
        }

        int entryCount = await db.Entries.CountAsync(e => e.SessionId == sessionId, ct).ConfigureAwait(false);

        SessionSettings sessionSettings = optionsMonitor.CurrentValue.Sessions ?? new SessionSettings();

        GrimoireLimits.EnforceEntryLimits(entryCount, entriesToAdd: 1, sessionSettings, entry.Content);

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

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return entry;
    }

    public async Task<List<Entry>> GetEntriesAscendingAsync(Guid sessionId, int takeLast, CancellationToken ct = default)
    {
        int clampedTake = Math.Max(1, takeLast);

        List<Entry> recentDescending = await db.Entries
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(clampedTake)
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

        return await db.Entries
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .Where(e =>
                e.CreatedAt > afterCreatedAt
                || (e.CreatedAt == afterCreatedAt && e.Id.CompareTo(afterId) > 0))
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Take(clampedLimit)
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

        IQueryable<Entry> query = db.Entries
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId);

        if (beforeCreatedAt is DateTimeOffset beforeAt && beforeId is Guid beforeEntryId)
        {
            query = query.Where(e =>
                e.CreatedAt < beforeAt
                || (e.CreatedAt == beforeAt && e.Id.CompareTo(beforeEntryId) < 0));
        }
        else
        {
            int clampedOffset = Math.Max(0, offset);

            query = query.Skip(clampedOffset);
        }

        return await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(clampedLimit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken ct) =>
        db.Entries.CountAsync(e => e.SessionId == sessionId, ct);

    public async Task UpdateSessionAsync(Session session, CancellationToken ct)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;

        db.Sessions.Update(session);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
        List<Entry> entries = new();

        await foreach (List<Entry> batch in ReadEntryBatchesAsync(sessionId, ct).ConfigureAwait(false))
        {
            entries.AddRange(batch);
        }

        return JsonSerializer.Serialize(
            new SessionExportPayload(session, entries),
            TheForgeJsonContext.Default.SessionExportPayload);
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
            List<Entry> batch = await db.Entries
                .AsNoTracking()
                .Where(e => e.SessionId == sessionId)
                .Where(e =>
                    e.CreatedAt > cursorCreatedAt
                    || (e.CreatedAt == cursorCreatedAt && e.Id.CompareTo(cursorId) > 0))
                .OrderBy(e => e.CreatedAt)
                .ThenBy(e => e.Id)
                .Take(ExportEntryBatchSize)
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
