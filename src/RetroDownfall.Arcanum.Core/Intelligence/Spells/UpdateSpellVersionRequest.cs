namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

public sealed record UpdateSpellVersionRequest(
    string Body,
    string? Workspace = null);
