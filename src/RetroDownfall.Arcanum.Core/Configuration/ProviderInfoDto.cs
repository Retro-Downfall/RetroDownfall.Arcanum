namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ProviderInfoDto(
    string Name,
    string Type,
    string Endpoint,
    string CredentialEnvironmentVariable,
    string[] Models,
    int ContextWindowLimit);
