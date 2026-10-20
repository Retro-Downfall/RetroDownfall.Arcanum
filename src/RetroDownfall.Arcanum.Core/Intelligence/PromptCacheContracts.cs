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
    SensitiveContent = 8,
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

/// <summary>Closed source provenance classes; stability alone never grants v2 eligibility.</summary>
public enum PromptCacheSourceClass
{
    Unclassified = 0,
    CodeOwnedFraming = 1,
    CodeOwnedFormatting = 2,
    Protected = 3,
    Dynamic = 4,
}

/// <summary>
/// Content-free local planning result over one already rendered system prompt. This decision
/// grants no authority and is not proof of tool provenance or eligibility to dispatch.
/// </summary>
public sealed record PromptCachePrefixDecision(
    string PolicyVersion,
    int Utf16Start,
    int Utf16Length,
    string PrefixDigest,
    string ScopeMarkerDigest,
    string ToolSchemaDigest,
    PromptCacheEligibility Eligibility,
    PromptCacheNonEligibilityReason NonEligibilityReason);
