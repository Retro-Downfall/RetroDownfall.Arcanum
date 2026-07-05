using System.Linq;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ProviderSettings
{

    public string Name { get; init; } = string.Empty;

    public AiProviderKind Type { get; init; }

    public string Endpoint { get; init; } = string.Empty;

    public string? ApiKey { get; init; }

    public IReadOnlyList<ModelEntry> Models { get; init; } = [];

    public int ContextWindowLimit { get; init; } = 8192;

    public ProviderLlamaCppSettings? LlamaCpp { get; init; }

    public override string ToString()
    {
        int mapCount = LlamaCpp?.ModelMap?.Count ?? 0;

        return $"{nameof(ProviderSettings)} {{ {nameof(Name)} = {Name}, {nameof(Type)} = {Type}, {nameof(Endpoint)} = {Endpoint}, {nameof(ApiKey)} = {(ApiKey is null ? "null" : "***")}, {nameof(Models)} = [{string.Join(", ", Models.Select(static m => m.SupportsVision ? $"{m.Name}(vision)" : m.Name))}], {nameof(ContextWindowLimit)} = {ContextWindowLimit}, LlamaCppModelMapCount = {mapCount} }}";
    }

}
