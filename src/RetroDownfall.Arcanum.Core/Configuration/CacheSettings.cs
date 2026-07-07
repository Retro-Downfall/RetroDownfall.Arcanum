namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Prompt-caching settings. For llama.cpp providers, Arcanum injects <c>cache_prompt: true</c> on
/// eligible requests (estimated prompt token count &gt;= <see cref="MinCacheableTokens"/>) to reduce
/// latency and cost for multi-turn conversations with large system prompts. OpenAI-compatible
/// providers cache automatically; Arcanum only reads the cached-token metric from the response usage.
/// </summary>
public sealed record CacheSettings
{

    /// <summary>Master toggle for prompt-caching hints. When <see langword="false"/> (default), no <c>cache_prompt</c> is injected.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Minimum estimated prompt token count before <c>cache_prompt: true</c> is injected. Avoids the
    /// overhead of cache lookup/insert for short prompts. Default 256; clamped 1–131072.
    /// </summary>
    public int MinCacheableTokens { get; init; } = 256;

}
