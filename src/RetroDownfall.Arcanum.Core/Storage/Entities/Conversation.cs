namespace RetroDownfall.Arcanum.Core.Storage.Entities;

public sealed class Conversation
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public DateTime? LastSummarizedMessageAt { get; set; }

    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}
