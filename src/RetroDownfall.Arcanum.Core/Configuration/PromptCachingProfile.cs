using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Selects how Arcanum classifies and controls provider-managed prompt caching.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<PromptCachingControlMode>))]
public enum PromptCachingControlMode
{
    [JsonStringEnumMemberName("providerManaged")]
    ProviderManaged,

    [JsonStringEnumMemberName("explicit")]
    Explicit,

    [JsonStringEnumMemberName("none")]
    None,
}

/// <summary>
/// Closed set of verified prompt-cache request contracts. Only
/// <see cref="ModelCapabilityCatalog"/> selects a dialect.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<PromptCachingWireDialect>))]
public enum PromptCachingWireDialect
{
    /// <summary>
    /// OpenAI Chat Completions root fields <c>prompt_cache_key</c> and
    /// <c>prompt_cache_retention</c>.
    /// </summary>
    [JsonStringEnumMemberName("openAiPromptCacheRetention")]
    OpenAiPromptCacheRetention,

    /// <summary>
    /// Reserved for OpenAI Chat Completions explicit content breakpoints. Validation rejects this
    /// dialect until the pinned SDK path has a golden-tested implementation.
    /// </summary>
    [JsonStringEnumMemberName("openAiPromptCacheBreakpoints")]
    OpenAiPromptCacheBreakpoints,
}

/// <summary>
/// Provider retention value selected for an explicit prompt-cache request.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<PromptCacheRetentionPolicy>))]
public enum PromptCacheRetentionPolicy
{
    [JsonStringEnumMemberName("providerDefault")]
    ProviderDefault,

    [JsonStringEnumMemberName("inMemory")]
    InMemory,

    [JsonStringEnumMemberName("twentyFourHours")]
    TwentyFourHours,

    [JsonStringEnumMemberName("thirtyMinutes")]
    ThirtyMinutes,
}

/// <summary>
/// Code-owned prompt-cache capability metadata emitted by <see cref="ModelCapabilityCatalog"/>.
/// It is not reachable from the bindable provider/model configuration graph.
/// </summary>
public sealed record PromptCachingProfile
{
    public PromptCachingControlMode ControlMode { get; set; } =
        PromptCachingControlMode.ProviderManaged;

    public PromptCachingWireDialect WireDialect { get; set; } =
        PromptCachingWireDialect.OpenAiPromptCacheRetention;

    public bool CacheKeysSupported { get; set; }

    public bool EmitCacheKey { get; set; }

    public bool RetentionSelectionSupported { get; set; }

    public PromptCacheRetentionPolicy Retention { get; set; } =
        PromptCacheRetentionPolicy.ProviderDefault;

    public bool StablePrefixBreakpointsSupported { get; set; }

    public bool EmitStablePrefixBreakpoint { get; set; }

    public bool ToolSchemasParticipate { get; set; }

    public bool ReportsCachedInputUsage { get; set; }
}
