using System.Linq;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ProviderSettings
{

    public string Name { get; set; } = string.Empty;

    public AiProviderKind Type { get; set; }

    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Optional exact environment-variable name containing this provider's API key. When omitted,
    /// Arcanum derives <c>ARCANUM_PROVIDER_{NORMALIZED_NAME}_API_KEY</c>; secret values never enter
    /// configuration.
    /// </summary>
    public string? CredentialEnvironmentVariable { get; set; }

    public IReadOnlyList<ModelEntry> Models { get; set; } = [];

    public int ContextWindowLimit { get; set; } = 8192;

    public override string ToString()
    {
        return $"{nameof(ProviderSettings)} {{ {nameof(Name)} = {Name}, {nameof(Type)} = {Type}, {nameof(Endpoint)} = {Endpoint}, {nameof(CredentialEnvironmentVariable)} = {CredentialEnvironmentVariable ?? "null"}, {nameof(Models)} = [{string.Join(", ", Models.Select(static m => m.SupportsVision ? $"{m.Name}(vision)" : m.Name))}], {nameof(ContextWindowLimit)} = {ContextWindowLimit} }}";
    }

}
