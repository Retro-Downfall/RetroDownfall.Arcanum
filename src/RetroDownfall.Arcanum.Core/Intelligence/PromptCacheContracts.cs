using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Core.Intelligence;

public enum PromptCacheEligibility
{
    Eligible = 0,
    NonCacheable = 1,
    ProviderManaged = 2,
}

public enum PromptCacheNonEligibilityReason
{
    None = 0,
    ProfileAbsent = 1,
    ProviderManaged = 2,
    DisabledByProfile = 3,
    UnsupportedDialect = 4,
    NoStablePrefix = 5,
    AuxiliaryPurpose = 6,
    InvalidPlan = 7,
}

public enum PromptCacheSemanticNamespace
{
    Main = 0,
    SpellRouting = 1,
}

public sealed record PromptCacheBoundary(
    int MessageIndex,
    int SegmentIndex);

/// <summary>
/// Provider-neutral, plaintext-free plan for one materialized provider call.
/// </summary>
public sealed record PromptCachePlan(
    string CacheKey,
    string Provider,
    string Model,
    PromptCacheSemanticNamespace Namespace,
    PromptCacheRetentionPolicy Retention,
    IReadOnlyList<PromptCacheBoundary> Boundaries,
    string StableSegmentDigest,
    string ToolSchemaDigest,
    int EligiblePrefixTokenEstimate,
    PromptCacheEligibility Eligibility,
    PromptCacheNonEligibilityReason NonEligibilityReason)
{
    public static PromptCachePlan NonCacheable(
        string provider,
        string model,
        PromptCacheSemanticNamespace semanticNamespace,
        PromptCacheEligibility eligibility,
        PromptCacheNonEligibilityReason reason) =>
        new(
            string.Empty,
            provider,
            model,
            semanticNamespace,
            PromptCacheRetentionPolicy.ProviderDefault,
            [],
            string.Empty,
            string.Empty,
            0,
            eligibility,
            reason);
}
