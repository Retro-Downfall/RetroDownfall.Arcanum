using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed class GrimoireRepository : IGrimoireRepository
{
    private readonly ArcanumDbContext _db;
    public GrimoireRepository(ArcanumDbContext db)
    {
        _db = db;
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
        _ = await _db.ChatMessages
            .Where(m => m.Id == assistantMessageId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(m => m.Content, fullContent)
                    .SetProperty(m => m.Timestamp, now),
                cancellationToken)
            .ConfigureAwait(false);
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

    public async Task<IReadOnlyList<ConversationSummary>> ListRecentConversationsAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0)
        {
            return Array.Empty<ConversationSummary>();
        }

        return await _db.Conversations
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Take(take)
            .Select(c => new ConversationSummary(c.Id, c.CreatedAt, c.Title))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
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

    private static string TruncateTitle(string prompt)
    {
        string trimmed = prompt.Trim();
        const int maxLen = 200;
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen];
    }
}
