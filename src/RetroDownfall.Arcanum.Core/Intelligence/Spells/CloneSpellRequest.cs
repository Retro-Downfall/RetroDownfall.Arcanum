namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

public sealed record CloneSpellRequest(
    string NewName,
    string? Workspace = null);
