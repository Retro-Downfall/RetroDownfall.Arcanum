namespace RetroDownfall.Arcanum.Core.Storage.Entities;

public sealed class Conversation
{

    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public string Title { get; set; } = string.Empty;

    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

}
