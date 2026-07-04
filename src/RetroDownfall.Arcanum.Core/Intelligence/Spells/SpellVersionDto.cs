namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

public sealed record SpellVersionDto(
    string Version,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string? Description,
    string? PreviousVersion = null);
