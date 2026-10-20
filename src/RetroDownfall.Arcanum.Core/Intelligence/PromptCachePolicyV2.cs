namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>Code-owned v2 eligibility; unknown, dynamic and protected sources fail closed.</summary>
public static class PromptCachePolicyV2
{
    public const string Version = "arcanum-dci-cache-policy-v2";

    public static bool IsEligible(PromptCacheSourceClass sourceClass, bool stable, bool sensitive) =>
        stable && !sensitive && sourceClass switch
        {
            PromptCacheSourceClass.CodeOwnedFraming => true,
            PromptCacheSourceClass.CodeOwnedFormatting => true,
            _ => false,
        };
}
