using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed class GrimoireRepository : IGrimoireRepository
{
    private const int MaxLegacyBackfillPerSweep = 200;

    private readonly ArcanumDbContext _db;

    private readonly ILogger<GrimoireRepository> _logger;

    private readonly IOptionsSnapshot<ArcanumSettings> _arcOptions;

    public GrimoireRepository(
        ArcanumDbContext db,
        ILogger<GrimoireRepository> logger,
        IOptionsSnapshot<ArcanumSettings> arcOptions)
    {
        _db = db;

        _logger = logger;

        _arcOptions = arcOptions;
    }

    public async Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
        Guid? sessionId,
        string prompt,
        string model,
        CancellationToken cancellationToken = default)
    {
        Guid userEntryId = Guid.NewGuid();
        Guid assistantEntryId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool useExistingThread = sessionId is { } existingId
            && await _db.Sessions
                .AnyAsync(c => c.Id == existingId, cancellationToken)
                .ConfigureAwait(false);
        if (useExistingThread)
        {
            Guid sid = sessionId!.Value;

            using IDisposable _ = await SessionWriteLock.AcquireAsync(sid, cancellationToken).ConfigureAwait(false);

            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
                await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                int entryCount = await GetEntryCountAsync(sid, cancellationToken).ConfigureAwait(false);

                Error? limitError = GrimoireLimits.EnforceEntryLimits(entryCount, entriesToAdd: 2, GetSessionSettings(), prompt, string.Empty);

                if (limitError is not null)
                {

                    throw new InvalidOperationException(limitError.Value.Message);

                }

                _db.Entries.Add(new Entry
                {
                    Id = userEntryId,
                    SessionId = sid,
                    Role = MessageRole.User,
                    Content = prompt,
                    ModelUsed = model,
                    CreatedAt = now,
                });

                _db.Entries.Add(new Entry
                {
                    Id = assistantEntryId,
                    SessionId = sid,
                    Role = MessageRole.Assistant,
                    Content = string.Empty,
                    ModelUsed = model,
                    CreatedAt = now,
                });

                await SqliteBusyRetry.ExecuteAsync(
                    () => _db.Sessions
                        .Where(s => s.Id == sid)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(x => x.UpdatedAt, now),
                            cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                await SqliteBusyRetry.ExecuteAsync(
                    () => _db.SaveChangesAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                await IncrementUnsummarizedEntryCountIfKnownAsync(sid, 2, cancellationToken).ConfigureAwait(false);

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

                return (sid, assistantEntryId);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);

                throw;
            }
        }

        Guid newSessionId = Guid.NewGuid();

        Error? newSessionLimitError = GrimoireLimits.EnforceEntryLimits(0, entriesToAdd: 2, GetSessionSettings(), prompt, string.Empty);

        if (newSessionLimitError is not null)
        {

            throw new InvalidOperationException(newSessionLimitError.Value.Message);

        }

        _db.Sessions.Add(new Session
        {
            Id = newSessionId,
            CreatedAt = now,
            UpdatedAt = now,
            Status = "active",
            Title = TruncateTitle(prompt),
        });
        _db.Entries.Add(new Entry
        {
            Id = userEntryId,
            SessionId = newSessionId,
            Role = MessageRole.User,
            Content = prompt,
            ModelUsed = model,
            CreatedAt = now,
        });
        _db.Entries.Add(new Entry
        {
            Id = assistantEntryId,
            SessionId = newSessionId,
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ModelUsed = model,
            CreatedAt = now,
        });
        await SqliteBusyRetry.ExecuteAsync(
            () => _db.SaveChangesAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);

        await IncrementUnsummarizedEntryCountIfKnownAsync(newSessionId, 2, cancellationToken).ConfigureAwait(false);

        return (newSessionId, assistantEntryId);
    }

    public async Task FinalizeAssistantEntryAsync(
        Guid assistantEntryId,
        string fullContent,
        CancellationToken cancellationToken = default)
    {
        Guid sessionId = await _db.Entries
            .AsNoTracking()
            .Where(m => m.Id == assistantEntryId)
            .Select(m => m.SessionId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        using IDisposable _ = await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false);

        int updated = await SqliteBusyRetry.ExecuteAsync(
            () => _db.Entries
                .Where(m => m.Id == assistantEntryId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(m => m.Content, fullContent),
                    cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (updated == 0)
        {
            _logger.LogWarning(
                "FinalizeAssistantEntryAsync updated 0 rows for assistant entry {AssistantEntryId}.",
                assistantEntryId);

            throw new InvalidOperationException(
                "Assistant entry could not be finalized; no matching row was updated in Grimoire.");
        }
    }

    public async Task DiscardAssistantEntryAsync(
        Guid assistantEntryId,
        CancellationToken cancellationToken = default)
    {
        Entry? entry = await _db.Entries
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == assistantEntryId, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            return;
        }

        if (entry.Role != MessageRole.Assistant)
        {
            _logger.LogWarning(
                "DiscardAssistantEntryAsync skipped entry {AssistantEntryId} because it is not an assistant row.",
                assistantEntryId);

            return;
        }

        if (!string.IsNullOrEmpty(entry.Content))
        {
            return;
        }

        using IDisposable _ = await SessionWriteLock.AcquireAsync(entry.SessionId, cancellationToken).ConfigureAwait(false);

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Guid sessionId = entry.SessionId;

            int deleted = await SqliteBusyRetry.ExecuteAsync(
                () => _db.Entries
                    .Where(m => m.Id == assistantEntryId && m.Role == MessageRole.Assistant && m.Content == string.Empty)
                    .ExecuteDeleteAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (deleted == 0)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

                return;
            }

            await DecrementUnsummarizedEntryCountIfKnownAsync(sessionId, 1, cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    public async Task AppendToolInteractionAsync(
        Guid sessionId,
        string toolName,
        string arguments,
        string result,
        string modelUsed,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using IDisposable _ = await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false);

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string callLine = $"[ToolCall: {toolName}({arguments})]";

            string resultLine = $"[ToolResult: {result}]";

            int entryCount = await GetEntryCountAsync(sessionId, cancellationToken).ConfigureAwait(false);

            Error? toolLimitError = GrimoireLimits.EnforceEntryLimits(entryCount, entriesToAdd: 2, GetSessionSettings(), callLine, resultLine);

            if (toolLimitError is not null)
            {

                throw new InvalidOperationException(toolLimitError.Value.Message);

            }

            _db.Entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Role = MessageRole.Assistant,
                Content = callLine,
                ModelUsed = modelUsed,
                CreatedAt = now,
                ToolName = toolName,
                ToolArguments = arguments,
            });
            _db.Entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Role = MessageRole.System,
                Content = resultLine,
                ModelUsed = modelUsed,
                CreatedAt = now,
            });
            await SqliteBusyRetry.ExecuteAsync(
                () => _db.Sessions
                    .Where(s => s.Id == sessionId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.UpdatedAt, now),
                        cancellationToken),
                cancellationToken).ConfigureAwait(false);

            await SqliteBusyRetry.ExecuteAsync(
                () => _db.SaveChangesAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);

            await IncrementUnsummarizedEntryCountIfKnownAsync(sessionId, 2, cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SaveCompletedExchangeAsync(
        string userPrompt,
        string assistantText,
        string modelUsed,
        CancellationToken cancellationToken = default)
    {
        Guid sessionId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Error? exchangeLimitError = GrimoireLimits.EnforceEntryLimits(0, entriesToAdd: 2, GetSessionSettings(), userPrompt, assistantText);

            if (exchangeLimitError is not null)
            {

                throw new InvalidOperationException(exchangeLimitError.Value.Message);

            }

            _db.Sessions.Add(new Session
            {
                Id = sessionId,
                CreatedAt = now,
                UpdatedAt = now,
                Status = "active",
                Title = TruncateTitle(userPrompt),
            });
            _db.Entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = userPrompt,
                ModelUsed = modelUsed,
                CreatedAt = now,
            });
            _db.Entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Role = MessageRole.Assistant,
                Content = assistantText,
                ModelUsed = modelUsed,
                CreatedAt = now,
            });
            await SqliteBusyRetry.ExecuteAsync(
                () => _db.SaveChangesAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);

            await IncrementUnsummarizedEntryCountIfKnownAsync(sessionId, 2, cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> PurgeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using IDisposable _ = await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false);

        await using var tx = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await SqliteBusyRetry.ExecuteAsync(
            () => _db.Entries
                .Where(m => m.SessionId == sessionId)
                .ExecuteDeleteAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);

        int removed = await SqliteBusyRetry.ExecuteAsync(
            () => _db.Sessions
                .Where(c => c.Id == sessionId)
                .ExecuteDeleteAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return removed;
    }

    public async Task<Session?> GetSessionAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Session? session = await _db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {
            return null;
        }

        int maxMessages = ArcanumSettingClamps.MaxMessagesPerConversationLoad(
            _arcOptions.Value.Grimoire.MaxMessagesPerConversationLoad);

        DateTime? watermark = session.LastSummarizedMessageAt;

        int afterWatermarkCount = 0;

        if (watermark is { } watermarkValue)
        {

            afterWatermarkCount = await CountEntriesAfterAsync(
                id,
                new DateTimeOffset(watermarkValue, TimeSpan.Zero),
                cancellationToken).ConfigureAwait(false);

        }

        int take = EntryWindowPolicy.ResolveTake(
            EntryWindowPolicy.EntryWindowKind.WatermarkAware,
            maxMessages,
            hasWatermark: watermark is not null,
            afterWatermarkCount: afterWatermarkCount);

        List<Entry> recent = await EntryTemporalQueries
            .LoadRecentDescending(_db, id, take)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        recent.Reverse();

        session.Entries = recent;

        return session;
    }

    public async Task<Session?> GetSessionHeaderAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        await _db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);

    public async Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        bool exists = await _db.Sessions
            .AnyAsync(c => c.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return null;
        }

        int maxMessages = ArcanumSettingClamps.MaxMessagesPerConversationLoad(
            _arcOptions.Value.Grimoire.MaxMessagesPerConversationLoad);

        int take = EntryWindowPolicy.ResolveTake(
            EntryWindowPolicy.EntryWindowKind.MaxMessagesOnly,
            maxMessages);

        List<Entry> recent = await EntryTemporalQueries
            .LoadRecentDescending(_db, sessionId, take)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        recent.Reverse();

        return recent
            .Select(m => new GrimoireEntryDto(m.Id, m.Role, m.Content, m.ModelUsed, m.CreatedAt))
            .ToList();
    }

    public async Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(
        Guid sessionId,
        int takeLast,
        CancellationToken cancellationToken = default)
    {
        bool exists = await _db.Sessions
            .AnyAsync(c => c.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return null;
        }

        int maxMessages = ArcanumSettingClamps.MaxMessagesPerConversationLoad(
            _arcOptions.Value.Grimoire.MaxMessagesPerConversationLoad);

        int clampedTake = EntryWindowPolicy.ResolveTake(
            EntryWindowPolicy.EntryWindowKind.ClampedTakeLast,
            maxMessages,
            requestedTake: takeLast);

        List<Entry> recent = await EntryTemporalQueries
            .LoadRecentDescending(_db, sessionId, clampedTake)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        recent.Reverse();

        return recent
            .Select(m => new GrimoireEntryDto(m.Id, m.Role, m.Content, m.ModelUsed, m.CreatedAt))
            .ToList();
    }

    public async Task<GrimoireEntryDto?> GetEntryByIdAsync(
        Guid sessionId,
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        return await _db.Entries
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.Id == entryId)
            .Select(m => new GrimoireEntryDto(m.Id, m.Role, m.Content, m.ModelUsed, m.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        return await _db.MageSettings
            .AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;

        MageSetting? existing = await _db.MageSettings
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _db.MageSettings.Add(
                new MageSetting
                {
                    Key = key,
                    Value = value,
                    UpdatedAt = now,
                });
        }
        else
        {
            existing.Value = value;

            existing.UpdatedAt = now;
        }

        await SqliteBusyRetry.ExecuteAsync(
            () => _db.SaveChangesAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return new LoreDto(key, value, now);
    }

    public async Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        return await SqliteBusyRetry.ExecuteAsync(
            () => _db.MageSettings
                .Where(s => s.Key == key)
                .ExecuteDeleteAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<ListPageResult<LoreDto>> ListLoreAsync(
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        GrimoireSettings settings = _arcOptions.Value.Grimoire ?? new GrimoireSettings();

        int pageSize = ArcanumSettingClamps.ListQueryLimit(
            limit ?? settings.DefaultLoreListLimit);

        int skip = Math.Max(0, offset);

        List<LoreDto> page = await _db.MageSettings
            .AsNoTracking()
            .OrderBy(m => m.Key)
            .Skip(skip)
            .Take(pageSize + 1)
            .Select(m => new LoreDto(m.Key, m.Value, m.UpdatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        bool hasMore = page.Count > pageSize;

        if (hasMore)
        {
            page = page.Take(pageSize).ToList();
        }

        int? nextOffset = hasMore ? skip + pageSize : null;

        return new ListPageResult<LoreDto>(page.ToArray(), hasMore, nextOffset);
    }

    public async Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        return await _db.MageSettings
            .AsNoTracking()
            .Where(m => m.Key == key)
            .Select(m => new LoreDto(m.Key, m.Value, m.UpdatedAt))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "No matching archives found.";
        }

        string trimmed = query.Trim();

        int maxQueryLen = ArcanumSettingClamps.ArchiveSearchMaxQueryLength(
            _arcOptions.Value.Intelligence.ArchiveSearchMaxQueryLength);

        if (trimmed.Length > maxQueryLen)
        {
            return "Archive search query is too long. Use a shorter phrase.";
        }

        string matchQuery = FtsMatchQuerySanitizer.Sanitize(trimmed);

        if (string.IsNullOrEmpty(matchQuery))
        {
            return "No matching archives found.";
        }

        int limit = Math.Clamp(maxResults, 1, 500);

        try
        {
            DbConnection connection = _db.Database.GetDbConnection();

            // W3.4 Group D #8: EF Core closes its connection after each SaveChanges, so the
            // raw DbCommand below cannot assume the connection is open. Open it explicitly
            // (mirroring ResolveFtsSessionIdsAsync) before ExecuteReaderAsync; otherwise a
            // search issued without a prior open query throws InvalidOperationException.
            if (connection.State != ConnectionState.Open)
            {

                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            }

            await using DbCommand cmd = connection.CreateCommand();

            cmd.CommandText =
                """
                SELECT c."Role", c."Content", c."CreatedAt"
                FROM "Entries_fts"
                INNER JOIN "Entries" AS c ON c."Id" = "Entries_fts"."Id"
                WHERE "Entries_fts" MATCH @query
                ORDER BY rank
                LIMIT @limit
                """;

            DbParameter pQuery = cmd.CreateParameter();

            pQuery.ParameterName = "@query";

            pQuery.Value = matchQuery;

            cmd.Parameters.Add(pQuery);

            DbParameter pLimit = cmd.CreateParameter();

            pLimit.ParameterName = "@limit";

            pLimit.Value = limit;

            cmd.Parameters.Add(pLimit);

            await using DbDataReader reader = await cmd
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            StringBuilder sb = new();

            bool any = false;

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                any = true;

                MessageRole role = (MessageRole)reader.GetInt32(0);

                string content = reader.GetString(1);

                DateTimeOffset timestamp = DateTimeOffset.Parse(
                    reader.GetString(2),
                    CultureInfo.InvariantCulture);

                _ = sb.Append('[')
                    .Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                    .Append("] ")
                    .Append(role)
                    .Append(": ")
                    .AppendLine(content);
            }

            return any ? sb.ToString().TrimEnd() : "No matching archives found.";
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "FTS archive search failed for sanitized query.");

            return "Archive search could not run for that input. Try simpler keywords (letters, numbers, spaces).";
        }
    }

    public async Task<List<Guid>> GetSessionsNeedingSummarizationAsync(
        int threshold,
        DateTime idleCutoff,
        CancellationToken cancellationToken = default)
    {
        List<Guid> unknownIds = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.UnsummarizedEntryCount == -1)
            .OrderBy(s => s.UpdatedAt)
            .Take(MaxLegacyBackfillPerSweep)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (Guid sessionId in unknownIds)
        {
            int count = await ComputeUnsummarizedEntryCountAsync(sessionId, cancellationToken).ConfigureAwait(false);

            _ = await SqliteBusyRetry.ExecuteAsync(
                () => _db.Sessions
                    .Where(s => s.Id == sessionId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.UnsummarizedEntryCount, count),
                        cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset idleCutoffOffset = new(idleCutoff, TimeSpan.Zero);

        return await _db.Sessions
            .AsNoTracking()
            .Where(s =>
                s.UnsummarizedEntryCount == -1
                || s.UnsummarizedEntryCount > threshold
                || (s.UnsummarizedEntryCount > 0 && s.UpdatedAt < idleCutoffOffset))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<List<Entry>> GetUnsummarizedEntriesAsync(
        Guid sessionId,
        DateTime watermark,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        int take = Math.Max(1, batchSize);

        DateTimeOffset watermarkOffset = new(watermark, TimeSpan.Zero);

        List<Entry> candidates = await _db.Entries
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.CreatedAt > watermarkOffset)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return candidates
            .OrderBy(m => m.CreatedAt)
            .Take(take)
            .ToList();
    }

    public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _db.Sessions.AnyAsync(c => c.Id == sessionId, cancellationToken);

    public async Task IncrementSessionTokensAsync(
        Guid sessionId,
        long totalTokens,
        CancellationToken cancellationToken = default)
    {
        if (totalTokens <= 0)
        {
            return;
        }

        _ = await SqliteBusyRetry.ExecuteAsync(
            () => _db.Sessions
                .Where(c => c.Id == sessionId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.TotalTokensUsed, c => c.TotalTokensUsed + totalTokens),
                    cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _db.WorkspaceContexts.Add(context);

            await SqliteBusyRetry.ExecuteAsync(
                () => _db.SaveChangesAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);

            int retain = ArcanumSettingClamps.WorkspaceContextRetentionCount(
                _arcOptions.Value.Grimoire.WorkspaceContextRetentionCount);

            List<Guid> idsToKeep = await _db.WorkspaceContexts
                .AsNoTracking()
                .Where(w => w.WorkspacePath == context.WorkspacePath)
                .OrderByDescending(w => w.CreatedAt)
                .Take(retain)
                .Select(w => w.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (idsToKeep.Count >= retain)
            {
                _ = await SqliteBusyRetry.ExecuteAsync(
                    () => _db.WorkspaceContexts
                        .Where(w => w.WorkspacePath == context.WorkspacePath && !idsToKeep.Contains(w.Id))
                        .ExecuteDeleteAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    public async Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        return await _db.WorkspaceContexts
            .AsNoTracking()
            .Where(w => w.WorkspacePath == workspacePath)
            .OrderByDescending(w => w.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        DateTime utcNow = DateTime.UtcNow;

        _ = await SqliteBusyRetry.ExecuteAsync(
            () => _db.Sessions
                .Where(c => c.Id == sessionId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(
                            c => c.LastSummarizedMessageAt,
                            c => _db.Entries
                                .Where(e => e.SessionId == c.Id)
                                .Select(e => (DateTime?)e.CreatedAt.UtcDateTime)
                                .Max() ?? utcNow)
                        .SetProperty(c => c.UnsummarizedEntryCount, 0),
                    cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateSessionCampaignRollupAsync(
        Guid sessionId,
        string summary,
        DateTime lastSummarizedMessageAt,
        CancellationToken cancellationToken = default)
    {
        bool exists = await _db.Sessions
            .AnyAsync(c => c.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return;
        }

        DateTimeOffset watermark = new(lastSummarizedMessageAt, TimeSpan.Zero);

        int remaining = await CountEntriesAfterAsync(sessionId, watermark, cancellationToken).ConfigureAwait(false);

        await SqliteBusyRetry.ExecuteAsync(
            () => _db.Sessions
                .Where(c => c.Id == sessionId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(c => c.Summary, summary)
                        .SetProperty(c => c.LastSummarizedMessageAt, lastSummarizedMessageAt)
                        .SetProperty(c => c.UnsummarizedEntryCount, remaining),
                    cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task IncrementUnsummarizedEntryCountIfKnownAsync(
        Guid sessionId,
        int delta,
        CancellationToken cancellationToken)
    {
        if (delta <= 0)
        {
            return;
        }

        _ = await SqliteBusyRetry.ExecuteAsync(
            () => _db.Sessions
                .Where(s => s.Id == sessionId && s.UnsummarizedEntryCount >= 0)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.UnsummarizedEntryCount, x => x.UnsummarizedEntryCount + delta),
                    cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DecrementUnsummarizedEntryCountIfKnownAsync(
        Guid sessionId,
        int delta,
        CancellationToken cancellationToken)
    {
        if (delta <= 0)
        {
            return;
        }

        _ = await SqliteBusyRetry.ExecuteAsync(
            () => _db.Sessions
                .Where(s => s.Id == sessionId && s.UnsummarizedEntryCount > 0)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.UnsummarizedEntryCount, x => x.UnsummarizedEntryCount - delta),
                    cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ComputeUnsummarizedEntryCountAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        DateTime? watermark = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => s.LastSummarizedMessageAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset cutoff = watermark is { } w
            ? new DateTimeOffset(w, TimeSpan.Zero)
            : DateTimeOffset.MinValue;

        return await CountEntriesAfterAsync(sessionId, cutoff, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> CountEntriesAfterAsync(
        Guid sessionId,
        DateTimeOffset afterExclusive,
        CancellationToken cancellationToken)
    {

        return await EntryTemporalQueries
            .CountAfter(_db, sessionId, afterExclusive)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);

    }

    private SessionSettings GetSessionSettings() =>
        _arcOptions.Value.Sessions ?? new SessionSettings();

    private Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _db.Entries.CountAsync(e => e.SessionId == sessionId, cancellationToken);

    private static string TruncateTitle(string prompt)
    {
        string trimmed = prompt.Trim();
        const int maxLen = 200;
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen];
    }

}
