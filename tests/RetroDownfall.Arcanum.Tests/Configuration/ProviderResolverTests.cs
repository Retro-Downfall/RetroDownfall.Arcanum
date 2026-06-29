using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ProviderResolverTests
{

    [Theory]
    [InlineData("llama3", "llama3", true)]
    [InlineData("Llama3", "llama3", true)]
    [InlineData("llama3:latest", "llama3", true)]
    [InlineData("llama3:latest", "llama3:latest", true)]
    [InlineData("llama3:8b", "llama3", true)]
    [InlineData("llama3", "llama3:8b", true)]
    [InlineData("llama3:8b", "llama3:70b", false)]
    [InlineData("gpt-4", "gpt-4o", false)]
    public void ModelNameMatches_HandlesCaseAndSymmetricTagStripping(string configured, string needle, bool expected)
    {

        bool matches = ProviderResolver.ModelNameMatches(configured, needle);

        Assert.Equal(expected, matches);

    }

    [Fact]
    public void EnumerateAdvertisedModels_ReturnsDistinctNonEmptyModels()
    {

        ProviderSettings provider = new()
        {
            Name = "ollama",
            Type = AiProviderKind.Ollama,
            Models = ["llama3", "llama3", " ", "mistral"],
        };

        string[] models = ProviderResolver.EnumerateAdvertisedModels(provider).ToArray();

        Assert.Equal(2, models.Length);

        Assert.Contains("llama3", models);

        Assert.Contains("mistral", models);

    }

    [Fact]
    public void EnumerateAdvertisedModels_IncludesLlamaCppModelMapKeys()
    {

        ProviderSettings provider = new()
        {
            Name = "local",
            Type = AiProviderKind.LlamaCppServer,
            Models = ["mapped-model"],
            LlamaCpp = new ProviderLlamaCppSettings
            {
                ModelMap = new Dictionary<string, string>
                {
                    ["mapped-model"] = "https://example.com/mapped.gguf",
                    ["extra-model"] = "https://example.com/extra.gguf",
                },
            },
        };

        string[] models = ProviderResolver.EnumerateAdvertisedModels(provider).ToArray();

        Assert.Equal(2, models.Length);

        Assert.Contains("mapped-model", models);

        Assert.Contains("extra-model", models);

    }

    [Fact]
    public void EnumerateAdvertisedModels_NonLlamaProvider_IgnoresModelMap()
    {

        ProviderSettings provider = new()
        {
            Name = "openai",
            Type = AiProviderKind.OpenAICompatible,
            Models = ["gpt-4"],
            LlamaCpp = new ProviderLlamaCppSettings
            {
                ModelMap = new Dictionary<string, string>
                {
                    ["ignored"] = "https://example.com/ignored.gguf",
                },
            },
        };

        string[] models = ProviderResolver.EnumerateAdvertisedModels(provider).ToArray();

        Assert.Single(models);

        Assert.Equal("gpt-4", models[0]);

    }

    [Fact]
    public void TryResolveProviderForModel_ExplicitTarget_FindsMatchingProvider()
    {

        ProviderSettings provider = new()
        {
            Name = "ollama",
            Type = AiProviderKind.Ollama,
            Models = ["llama3:latest"],
        };

        ArcanumSettings settings = new() { Providers = [provider] };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            "llama3",
            out ProviderSettings? found,
            out string resolvedModel);

        Assert.True(resolved);

        Assert.Same(provider, found);

        Assert.Equal("llama3:latest", resolvedModel);

    }

    [Fact]
    public void TryResolveProviderForModel_ExplicitTarget_NotFound_ReturnsFalse()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.Ollama,
                    Models = ["llama3"],
                },
            ],
        };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            "unknown-model",
            out ProviderSettings? found,
            out string resolvedModel);

        Assert.False(resolved);

        Assert.Null(found);

        Assert.Equal(string.Empty, resolvedModel);

    }

    [Fact]
    public void TryResolveProviderForModel_DefaultModel_ResolvesWhenSet()
    {

        ProviderSettings provider = new()
        {
            Name = "ollama",
            Type = AiProviderKind.Ollama,
            Models = ["mistral"],
        };

        ArcanumSettings settings = new()
        {
            DefaultModel = "mistral",
            Providers = [provider],
        };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            targetModel: null,
            out ProviderSettings? found,
            out string resolvedModel);

        Assert.True(resolved);

        Assert.Same(provider, found);

        Assert.Equal("mistral", resolvedModel);

    }

    [Fact]
    public void TryResolveProviderForModel_DefaultModelMissing_ReturnsFalse()
    {

        ArcanumSettings settings = new()
        {
            DefaultModel = "missing",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.Ollama,
                    Models = ["llama3"],
                },
            ],
        };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            targetModel: null,
            out ProviderSettings? found,
            out string resolvedModel);

        Assert.False(resolved);

        Assert.Null(found);

        Assert.Equal(string.Empty, resolvedModel);

    }

    [Fact]
    public void TryResolveProviderForModel_NoTargetOrDefault_UsesFirstProviderFirstModel()
    {

        ProviderSettings first = new()
        {
            Name = "first",
            Type = AiProviderKind.Ollama,
            Models = ["alpha", "beta"],
        };

        ProviderSettings second = new()
        {
            Name = "second",
            Type = AiProviderKind.Ollama,
            Models = ["gamma"],
        };

        ArcanumSettings settings = new() { Providers = [first, second] };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            targetModel: null,
            out ProviderSettings? found,
            out string resolvedModel);

        Assert.True(resolved);

        Assert.Same(first, found);

        Assert.Equal("alpha", resolvedModel);

    }

    [Fact]
    public void TryResolveProviderForModel_NoProviders_ReturnsFalse()
    {

        ArcanumSettings settings = new() { Providers = [] };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            targetModel: null,
            out ProviderSettings? found,
            out string resolvedModel);

        Assert.False(resolved);

        Assert.Null(found);

        Assert.Equal(string.Empty, resolvedModel);

    }

}
