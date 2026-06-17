namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Per-provider settings for <see cref="AiProviderKind.LlamaCppServer"/>.
/// </summary>
public sealed record ProviderLlamaCppSettings
{

    /// <summary>
    /// Maps model keys to remote <c>http</c>/<c>https</c> URLs for on-demand download when the GGUF is not yet cached.
    /// </summary>
    public Dictionary<string, string>? ModelMap { get; init; }

}
