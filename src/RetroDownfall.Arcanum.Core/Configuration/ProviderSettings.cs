using System.Linq;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ProviderSettings
{

    public string Name { get; set; } = string.Empty;

    public AiProviderKind Type { get; set; }

    public string Endpoint { get; set; } = string.Empty;

    public string? ApiKey { get; set; }

    public IReadOnlyList<ModelEntry> Models { get; set; } = [];

    public int ContextWindowLimit { get; set; } = 8192;

    /// <summary>
    /// When <see langword="true"/>, Arcanum records <c>arcanum_prompt_cache_tokens</c> metrics for
    /// this provider when the response usage reports cached prompt tokens. Defaults to
    /// <see langword="true"/> for OpenAI-compatible providers (which cache automatically).
    /// Operators can force this off for providers that do not support caching to avoid misleading metrics.
    /// </summary>
    public bool? SupportsPromptCaching { get; set; }

    public override string ToString()
    {
        return $"{nameof(ProviderSettings)} {{ {nameof(Name)} = {Name}, {nameof(Type)} = {Type}, {nameof(Endpoint)} = {Endpoint}, {nameof(ApiKey)} = {(ApiKey is null ? "null" : "***")}, {nameof(Models)} = [{string.Join(", ", Models.Select(static m => m.SupportsVision ? $"{m.Name}(vision)" : m.Name))}], {nameof(ContextWindowLimit)} = {ContextWindowLimit} }}";
    }

}
