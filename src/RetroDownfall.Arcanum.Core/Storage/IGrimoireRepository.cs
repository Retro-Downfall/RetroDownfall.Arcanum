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

    Task<IReadOnlyList<ConversationSummary>> ListRecentConversationsAsync(
        int take,
        CancellationToken cancellationToken = default);

    Task<Conversation?> GetConversationAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
