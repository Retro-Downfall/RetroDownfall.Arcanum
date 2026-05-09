namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record ConversationSummaryDto(Guid Id, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, string Snippet);
