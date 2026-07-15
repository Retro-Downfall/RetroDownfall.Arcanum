namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

public sealed record SpellVersionDetailDto(
    string Version,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string? Description,
    string Body);
