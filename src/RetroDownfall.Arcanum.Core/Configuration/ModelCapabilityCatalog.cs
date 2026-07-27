using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Non-bindable, code-owned capabilities verified for a provider/model pair.
/// </summary>
public sealed record ModelCapabilityProfile(
    ResolvedModelTokenizationProfile Tokenization,
    PromptCachingProfile? PromptCaching);

/// <summary>
/// Built-in provider/model capability catalog. Unknown providers, endpoints, and model families
/// return no profile so callers can apply conservative behavior without operator algorithm knobs.
/// </summary>
public static class ModelCapabilityCatalog
{
    public static ModelCapabilityProfile? Resolve(
        ProviderSettings provider,
        string? canonicalModel)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (!ProviderResolver.TryResolveModelEntry(
                provider,
                canonicalModel,
                out ModelEntry? configuredModel)
            || configuredModel is null
            || !IsVerifiedOpenAiProvider(provider)
            || !IsKnownO200kModel(configuredModel.Name))
        {
            return null;
        }

        string model = configuredModel.Name.Trim();
        int imageReserve = ArcanumSettingClamps.UnknownImageTokenReserve(
            ArcanumRuntimeDefaults.Intelligence.UnknownImageTokenReserve);

        return new ModelCapabilityProfile(
            new ResolvedModelTokenizationProfile
            {
                ProfileId = $"catalog:openai:{model}:o200k_base",
                Type = ModelTokenizationProfileType.ExactLocalTokenizer,
                TokenizerId = "o200k_base",
                SafetyMarginPercent = 0,
                PerMessageOverheadTokens = 3,
                PerToolOverheadTokens = 8,
                ProviderFramingTokens = 3,
                StopTokenOverheadTokens = 1,
                UnknownImageReserveTokens = imageReserve,
                Confidence = 1d,
            },
            new PromptCachingProfile
            {
                ControlMode = PromptCachingControlMode.Explicit,
                WireDialect = PromptCachingWireDialect.OpenAiPromptCacheRetention,
                CacheKeysSupported = true,
                EmitCacheKey = true,
                RetentionSelectionSupported = false,
                Retention = PromptCacheRetentionPolicy.ProviderDefault,
                StablePrefixBreakpointsSupported = false,
                EmitStablePrefixBreakpoint = false,
                ToolSchemasParticipate = true,
                ReportsCachedInputUsage = true,
            });
    }

    public static PromptCachingProfile? ResolvePromptCaching(
        ProviderSettings provider,
        string? canonicalModel) =>
        Resolve(provider, canonicalModel)?.PromptCaching;

    private static bool IsVerifiedOpenAiProvider(ProviderSettings provider)
    {
        if (provider.Type != AiProviderKind.OpenAICompatible
            || !Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out Uri? endpoint))
        {
            return false;
        }

        return endpoint.Scheme == Uri.UriSchemeHttps
            && string.Equals(
                endpoint.Host,
                "api.openai.com",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownO200kModel(string model)
    {
        string value = model.Trim();

        return IsModelFamily(value, "gpt-4o")
            || IsModelFamily(value, "chatgpt-4o")
            || IsModelFamily(value, "gpt-4.1")
            || IsModelFamily(value, "gpt-5")
            || IsModelFamily(value, "o1")
            || IsModelFamily(value, "o3")
            || IsModelFamily(value, "o4");
    }

    private static bool IsModelFamily(string model, string family) =>
        model.Equals(family, StringComparison.OrdinalIgnoreCase)
        || model.StartsWith(family, StringComparison.OrdinalIgnoreCase)
        && model.Length > family.Length
        && model[family.Length] is '-' or '.' or ':';
}
