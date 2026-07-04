namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

public sealed record CreateSpellVersionRequest(
    string Version,
    string Body,
    string? Workspace = null);
