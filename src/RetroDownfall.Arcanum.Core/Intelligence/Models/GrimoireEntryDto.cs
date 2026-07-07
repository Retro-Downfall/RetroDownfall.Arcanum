using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record GrimoireEntryDto(
    Guid Id,
    MessageRole Role,
    string Content,
    string ModelUsed,
    DateTimeOffset CreatedAt,
    bool IsPinned = false);
