namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ProviderSettings
{

    public string Name { get; init; } = string.Empty;

    public AiProviderKind Type { get; init; }

    public string Endpoint { get; init; } = string.Empty;

    public string? ApiKey { get; init; }

    public string[] Models { get; init; } = [];

    public int ContextWindowLimit { get; init; } = 8192;

    public override string ToString() =>
        $"{nameof(ProviderSettings)} {{ {nameof(Name)} = {Name}, {nameof(Type)} = {Type}, {nameof(Endpoint)} = {Endpoint}, {nameof(ApiKey)} = {(ApiKey is null ? "null" : "***")}, {nameof(Models)} = [{string.Join(", ", Models)}], {nameof(ContextWindowLimit)} = {ContextWindowLimit} }}";

}
