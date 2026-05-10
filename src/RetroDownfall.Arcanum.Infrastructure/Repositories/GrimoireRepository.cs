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
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed class GrimoireRepository : IGrimoireRepository
{
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

    public async Task<(Guid ConversationId, Guid AssistantMessageId)> BeginAssistantReplyAsync(
        Guid? conversationId,
        string prompt,
        string model,
        CancellationToken cancellationToken = default)
    {
        Guid userMessageId = Guid.NewGuid();
        Guid assistantMessageId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        bool useExistingThread = conversationId is { } existingId
            && await _db.Conversations
                .AnyAsync(c => c.Id == existingId, cancellationToken)
                .ConfigureAwait(false);
        if (useExistingThread)
        {
            Guid cid = conversationId!.Value;
            _db.ChatMessages.Add(new ChatMessage
            {
                Id = userMessageId,
                ConversationId = cid,
                Role = MessageRole.User,
                Content = prompt,
                ModelUsed = model,
                Timestamp = now,
            });
            _db.ChatMessages.Add(new ChatMessage
            {
                Id = assistantMessageId,
                ConversationId = cid,
                Role = MessageRole.Assistant,
                Content = string.Empty,
                ModelUsed = model,
                Timestamp = now,
            });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return (cid, assistantMessageId);
        }
        // Stale or missing conversation id: start a new thread (same as null conversationId).
        Guid newConversationId = Guid.NewGuid();
        _db.Conversations.Add(new Conversation
        {
            Id = newConversationId,
            CreatedAt = now,
            Title = TruncateTitle(prompt),
        });
        _db.ChatMessages.Add(new ChatMessage
        {
            Id = userMessageId,
            ConversationId = newConversationId,
            Role = MessageRole.User,
            Content = prompt,
            ModelUsed = model,
            Timestamp = now,
        });
        _db.ChatMessages.Add(new ChatMessage
        {
            Id = assistantMessageId,
            ConversationId = newConversationId,
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ModelUsed = model,
            Timestamp = now,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (newConversationId, assistantMessageId);
    }

    public async Task FinalizeAssistantMessageAsync(
        Guid assistantMessageId,
        string fullContent,
        CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;

        int updated = await _db.ChatMessages
            .Where(m => m.Id == assistantMessageId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(m => m.Content, fullContent)
                    .SetProperty(m => m.Timestamp, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            _logger.LogWarning(
                "FinalizeAssistantMessageAsync updated 0 rows for assistant message {AssistantMessageId}.",
                assistantMessageId);

            throw new InvalidOperationException(
                "Assistant message could not be finalized; no matching row was updated in Grimoire.");
        }
    }

    public async Task AppendToolInteractionAsync(
        Guid conversationId,
        string toolName,
        string arguments,
        string result,
        string modelUsed,
        CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string callLine = $"[ToolCall: {toolName}({arguments})]";
            string resultLine = $"[ToolResult: {result}]";
            _db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                Role = MessageRole.Assistant,
                Content = callLine,
                ModelUsed = modelUsed,
                Timestamp = now,
            });
            _db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                Role = MessageRole.System,
                Content = resultLine,
                ModelUsed = modelUsed,
                Timestamp = now,
            });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
        Guid conversationId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _db.Conversations.Add(new Conversation
            {
                Id = conversationId,
                CreatedAt = now,
                Title = TruncateTitle(userPrompt),
            });
            _db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                Role = MessageRole.User,
                Content = userPrompt,
                ModelUsed = modelUsed,
                Timestamp = now,
            });
            _db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                Role = MessageRole.Assistant,
                Content = assistantText,
                ModelUsed = modelUsed,
                Timestamp = now,
            });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<ConversationSummaryDto>> ListRecentConversationsAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0)
        {
            return Array.Empty<ConversationSummaryDto>();
        }

        var rows = await _db.Conversations
            .AsNoTracking()
            .OrderByDescending(c => c.Messages.Max(m => (DateTime?)m.Timestamp) ?? c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.CreatedAt,
                LastUpdate = c.Messages.Max(m => (DateTime?)m.Timestamp),
                FirstMsg = c.Messages.OrderBy(m => m.Timestamp).Select(m => m.Content).FirstOrDefault(),
            })
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ConversationSummaryDto> result = new(rows.Count);

        foreach (var row in rows)
        {
            DateTime updatedAtUtc = row.LastUpdate ?? row.CreatedAt;

            string snippet = BuildSnippet(row.FirstMsg);

            result.Add(new ConversationSummaryDto(row.Id, row.CreatedAt, updatedAtUtc, snippet));
        }

        return result;
    }

    public async Task<int> DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var tx = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        _ = await _db.ChatMessages
            .Where(m => m.ConversationId == conversationId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        int removed = await _db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return removed;
    }

    public async Task<Conversation?> GetConversationAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _db.Conversations
            .AsNoTracking()
            .Include(c => c.Messages.OrderBy(m => m.Timestamp))
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ConversationDetailDto?> GetConversationDetailAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new ConversationDetailDto(c.Id, c.Title, c.CreatedAt, c.Summary))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<List<ConversationMessageDto>?> GetConversationMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        bool exists = await _db.Conversations
            .AnyAsync(c => c.Id == conversationId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return null;
        }

        return await _db.ChatMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Timestamp)
            .Select(m => new ConversationMessageDto(m.Id, m.Role, m.Content, m.ModelUsed, m.Timestamp))
            .ToListAsync(cancellationToken)
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

    public async Task ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default)
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

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        return await _db.MageSettings
            .Where(s => s.Key == key)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false) > 0;
    }

    public async Task<List<LoreDto>> ListLoreAsync(CancellationToken cancellationToken = default)
    {
        return await _db.MageSettings
            .AsNoTracking()
            .OrderBy(m => m.Key)
            .Select(m => new LoreDto(m.Key, m.Value, m.UpdatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
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

        string matchQuery = SanitizeFtsMatchQuery(trimmed);

        if (string.IsNullOrEmpty(matchQuery))
        {
            return "No matching archives found.";
        }

        int limit = Math.Clamp(maxResults, 1, 500);

        try
        {
            DbConnection connection = _db.Database.GetDbConnection();

            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            await using DbCommand cmd = connection.CreateCommand();

            cmd.CommandText =
                """
                SELECT c."Role", c."Content", c."Timestamp"
                FROM "ChatMessages_fts" AS f
                INNER JOIN "ChatMessages" AS c ON c."Id" = f."Id"
                WHERE f MATCH @query
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

                DateTime timestamp = reader.GetDateTime(2);

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

    public async Task<List<Guid>> GetConversationsNeedingSummarizationAsync(
        int threshold,
        DateTime idleCutoff,
        CancellationToken cancellationToken = default)
    {
        return await _db.Conversations
            .AsNoTracking()
            .Where(c =>
                c.Messages.Count(m => m.Timestamp > (c.LastSummarizedMessageAt ?? DateTime.MinValue)) > threshold
                || (c.Messages.Any(m => m.Timestamp > (c.LastSummarizedMessageAt ?? DateTime.MinValue))
                    && c.Messages.Max(m => (DateTime?)m.Timestamp) < idleCutoff))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<bool> ConversationExistsAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        _db.Conversations.AnyAsync(c => c.Id == conversationId, cancellationToken);

    public async Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default)
    {
        _db.WorkspaceContexts.Add(context);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task AdvanceCampaignLogWatermarkAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        bool exists = await _db.Conversations
            .AnyAsync(c => c.Id == conversationId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return;
        }

        DateTime? latestMessageUtc = await _db.ChatMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => (DateTime?)m.Timestamp)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTime watermark = latestMessageUtc ?? DateTime.UtcNow;

        await _db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.LastSummarizedMessageAt, watermark),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reduces FTS5 syntax injection / parse errors: keep token characters and spaces (implicit AND between tokens).
    /// </summary>
    private static string SanitizeFtsMatchQuery(string query)
    {
        StringBuilder sb = new(query.Length);

        bool pendingSpace = false;

        foreach (char c in query)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                if (pendingSpace && sb.Length > 0)
                {
                    _ = sb.Append(' ');
                }

                pendingSpace = false;

                _ = sb.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
            }
            else
            {
                pendingSpace = sb.Length > 0;
            }
        }

        return sb.ToString().Trim();
    }

    private static string TruncateTitle(string prompt)
    {
        string trimmed = prompt.Trim();
        const int maxLen = 200;
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen];
    }

    private static string BuildSnippet(string? firstMsg)
    {
        if (string.IsNullOrEmpty(firstMsg))
        {
            return string.Empty;
        }

        return firstMsg.Length > 50 ? string.Concat(firstMsg.AsSpan(0, 50), "...") : firstMsg;
    }
}
