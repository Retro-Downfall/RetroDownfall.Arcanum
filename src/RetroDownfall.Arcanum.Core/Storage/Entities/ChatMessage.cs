namespace RetroDownfall.Arcanum.Core.Storage.Entities;

public sealed class ChatMessage
{

    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public MessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public string ModelUsed { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }

    public Conversation? Conversation { get; set; }

}
