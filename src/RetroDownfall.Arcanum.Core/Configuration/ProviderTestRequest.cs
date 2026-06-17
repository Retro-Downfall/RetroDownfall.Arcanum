namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ProviderTestRequest(
    string Endpoint,
    string? ApiKey,
    AiProviderKind Type);
