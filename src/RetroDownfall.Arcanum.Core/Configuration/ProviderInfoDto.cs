namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ProviderInfoDto(
    string Name,
    string Type,
    string Endpoint,
    string? ApiKey,
    string[] Models,
    int ContextWindowLimit,
    bool HasLlamaCppModelMap);
