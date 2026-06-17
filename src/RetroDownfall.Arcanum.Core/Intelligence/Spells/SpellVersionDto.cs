namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

public sealed record SpellVersionDto(
    int Version,
    DateTimeOffset CreatedAt,
    string? Description);
