using RetroDownfall.Arcanum.Api.LlamaCpp;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.LlamaCpp;

/// <summary>
/// Pure-logic coverage for <see cref="LlamaEndpoints.ResolveWarmupModelName"/> — the model name
/// picked for <c>POST /api/llama/servers/{cacheKey}/warmup</c>'s dummy chat request. HTTP-level
/// behavior (404/400 envelope) is covered by <c>LlamaEndpointsWarmupHttpTests</c>.
/// </summary>
public sealed class LlamaWarmupModelResolutionTests
{

    [Fact]
    public void ResolveWarmupModelName_MatchesConfiguredModelByCacheKey()
    {

        ArcanumSettings settings = new()
        {
            DefaultModel = "some-other-model",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "local-llama",
                    Type = AiProviderKind.LlamaCppServer,
                    Endpoint = "http://localhost:9000",
                    Models = [new ModelEntry("llama-3-8b-instruct")],
                },
            ],
        };

        string resolved = LlamaEndpoints.ResolveWarmupModelName(settings, "llama-3-8b-instruct");

        Assert.Equal("llama-3-8b-instruct", resolved);

    }

    [Fact]
    public void ResolveWarmupModelName_MatchesModelMapEntry()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "local-llama",
                    Type = AiProviderKind.LlamaCppServer,
                    Endpoint = "http://localhost:9000",
                    LlamaCpp = new ProviderLlamaCppSettings
                    {
                        ModelMap = new Dictionary<string, string> { ["mapped-model"] = "https://example.test/model.gguf" },
                    },
                },
            ],
        };

        string resolved = LlamaEndpoints.ResolveWarmupModelName(settings, "mapped-model");

        Assert.Equal("mapped-model", resolved);

    }

    [Fact]
    public void ResolveWarmupModelName_FallsBackToDefaultModel_WhenNoMatch()
    {

        ArcanumSettings settings = new()
        {
            DefaultModel = "fallback-model",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "local-llama",
                    Type = AiProviderKind.LlamaCppServer,
                    Endpoint = "http://localhost:9000",
                    Models = [new ModelEntry("unrelated-model")],
                },
            ],
        };

        string resolved = LlamaEndpoints.ResolveWarmupModelName(settings, "no-such-cache-key");

        Assert.Equal("fallback-model", resolved);

    }

    [Fact]
    public void ResolveWarmupModelName_FallsBackToCacheKey_WhenNoMatchAndNoDefaultModel()
    {

        ArcanumSettings settings = new() { Providers = [] };

        string resolved = LlamaEndpoints.ResolveWarmupModelName(settings, "raw-cache-key");

        Assert.Equal("raw-cache-key", resolved);

    }

    [Fact]
    public void ResolveWarmupModelName_IgnoresNonLlamaCppProviders()
    {

        ArcanumSettings settings = new()
        {
            DefaultModel = "fallback-model",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "openai-compatible",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://example.test/v1",
                    Models = [new ModelEntry("gpt-4o-mini")],
                },
            ],
        };

        // "gpt-4o-mini" is configured, but on an OpenAICompatible provider — not eligible for
        // warm-up model resolution, which only considers LlamaCppServer providers.
        string resolved = LlamaEndpoints.ResolveWarmupModelName(settings, "gpt-4o-mini");

        Assert.Equal("fallback-model", resolved);

    }

}
