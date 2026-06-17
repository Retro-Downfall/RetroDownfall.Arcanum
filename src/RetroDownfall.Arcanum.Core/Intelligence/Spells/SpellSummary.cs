using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

public sealed record SpellSummary(
    string Name,
    string? Description,
    SpellSource Source,
    string[] Tags,
    string? Version = null,
    JsonDocument? InputSchema = null,
    JsonDocument? OutputSchema = null,
    string[]? DeclaredTools = null,
    string[]? Dependencies = null);
