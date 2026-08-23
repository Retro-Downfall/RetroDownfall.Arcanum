using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.Tower;

public sealed record SkillMetadata(
    string Name,
    string Version,
    string? Description,
    List<string> Tags,
    JsonDocument? InputSchema,
    JsonDocument? OutputSchema,
    List<string> DeclaredTools,
    List<string> Dependencies,
    string? Model,
    string? Provider,
    Dictionary<string, double>? DefaultParameters,
    DateTimeOffset? LastModified,
    string? ActiveVersion = null);
