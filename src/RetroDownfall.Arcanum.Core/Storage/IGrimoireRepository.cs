using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Core.Storage;

public interface IGrimoireRepository
{
    Task<(Guid ConversationId, Guid AssistantMessageId)> BeginAssistantReplyAsync(
        Guid? conversationId,
        string prompt,
        string model,
        CancellationToken cancellationToken = default);

    Task FinalizeAssistantMessageAsync(
        Guid assistantMessageId,
        string fullContent,
        CancellationToken cancellationToken = default);

    Task AppendToolInteractionAsync(
        Guid conversationId,
        string toolName,
        string arguments,
        string result,
        string modelUsed,
        CancellationToken cancellationToken = default);

    Task SaveCompletedExchangeAsync(
        string userPrompt,
        string assistantText,
        string modelUsed,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationSummaryDto>> ListRecentConversationsAsync(
        int take,
        CancellationToken cancellationToken = default);

    Task<int> DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<Conversation?> GetConversationAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<ConversationDetailDto?> GetConversationDetailAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<List<ConversationMessageDto>?> GetConversationMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<List<Guid>> GetConversationsNeedingSummarizationAsync(
        int threshold,
        DateTime idleCutoff,
        CancellationToken cancellationToken = default);

    Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default);

    Task ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default);

    Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default);

    Task<List<LoreDto>> ListLoreAsync(CancellationToken cancellationToken = default);

    Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default);

    Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default);
}
