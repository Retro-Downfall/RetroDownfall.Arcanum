using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

public sealed record SpellDetail(
    string Name,
    string? Description,
    SpellSource Source,
    string[] Tags,
    string? SystemPrompt,
    string? Template,
    string? Body,
    string? Model,
    string? Provider,
    string[] Tools,
    string[] RequiredMcpServers,
    string? WorkingDirectory,
    string? FilePath,
    string? Version = null,
    JsonDocument? InputSchema = null,
    JsonDocument? OutputSchema = null,
    string[]? DeclaredTools = null,
    string[]? Dependencies = null);
