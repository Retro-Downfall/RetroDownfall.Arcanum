using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record ConversationMessageDto(
    Guid Id,
    MessageRole Role,
    string Content,
    string ModelUsed,
    DateTime Timestamp);
