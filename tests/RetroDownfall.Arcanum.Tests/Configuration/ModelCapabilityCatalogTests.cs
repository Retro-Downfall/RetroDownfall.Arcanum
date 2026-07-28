using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ModelCapabilityCatalogTests
{
    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4.1-mini")]
    [InlineData("gpt-5")]
    [InlineData("o3")]
    public void ResolveKnownOfficialOpenAiModelReturnsVerifiedCapabilities(string model)
    {
        ProviderSettings provider = OfficialOpenAi(model);

        ModelCapabilityProfile profile =
            Assert.IsType<ModelCapabilityProfile>(
                ModelCapabilityCatalog.Resolve(provider, model));
        PromptCachingProfile caching =
            Assert.IsType<PromptCachingProfile>(profile.PromptCaching);

        Assert.Equal(ModelTokenizationProfileType.ExactLocalTokenizer, profile.Tokenization.Type);
        Assert.Equal("o200k_base", profile.Tokenization.TokenizerId);
        Assert.Equal(0, profile.Tokenization.SafetyMarginPercent);
        Assert.Equal(PromptCachingControlMode.Explicit, caching.ControlMode);
        Assert.Equal(
            PromptCachingWireDialect.OpenAiPromptCacheRetention,
            caching.WireDialect);
        Assert.True(caching.CacheKeysSupported);
        Assert.True(caching.EmitCacheKey);
        Assert.True(caching.ToolSchemasParticipate);
        Assert.True(caching.ReportsCachedInputUsage);
    }

    [Fact]
    public void ResolveUnknownModelReturnsNoClaimedCapabilities()
    {
        ProviderSettings provider = OfficialOpenAi("future-unknown-model");

        Assert.Null(ModelCapabilityCatalog.Resolve(provider, "future-unknown-model"));
        Assert.Null(
            ProviderResolver.ResolvePromptCachingProfile(
                provider,
                "future-unknown-model"));
    }

    [Fact]
    public void ResolveOpenAiNamedAliasOnUnverifiedEndpointReturnsNoClaimedCapabilities()
    {
        ProviderSettings provider = OfficialOpenAi("gpt-5");
        provider.Endpoint = "http://localhost:11434/v1";

        Assert.Null(ModelCapabilityCatalog.Resolve(provider, "gpt-5"));
    }

    [Fact]
    public void ResolveRequiresExactConfiguredModelEntry()
    {
        ProviderSettings provider = OfficialOpenAi("gpt-5");

        Assert.Null(ModelCapabilityCatalog.Resolve(provider, "gpt-5-unconfigured"));
    }

    private static ProviderSettings OfficialOpenAi(string model) =>
        new()
        {
            Name = "OpenAI",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.openai.com/v1",
            Models = [model],
        };
}
