using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

public sealed record UpdateSpellRequest(
    string? Description,
    string[]? Tags,
    string? SystemPrompt,
    string? Template,
    string? Model,
    string? Provider,
    string[]? Tools,
    string[]? RequiredMcpServers,
    string? Version = null,
    JsonDocument? InputSchema = null,
    JsonDocument? OutputSchema = null,
    string[]? DeclaredTools = null,
    string[]? Dependencies = null,
    Dictionary<string, double>? DefaultParameters = null);
