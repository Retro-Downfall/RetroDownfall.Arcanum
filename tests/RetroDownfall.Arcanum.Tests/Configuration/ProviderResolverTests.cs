using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ProviderResolverTests
{

    [Theory]
    [InlineData("llama3", "llama3", true)]
    [InlineData("Llama3", "llama3", true)]
    [InlineData("llama3:latest", "llama3:latest", true)]
    [InlineData("llama3:latest", "llama3", false)]
    [InlineData("llama3:8b", "llama3", false)]
    [InlineData("llama3", "llama3:8b", false)]
    [InlineData("llama3:8b", "llama3:70b", false)]
    [InlineData("gpt-4", "gpt-4o", false)]
    public void ModelNameMatches_IsCaseInsensitiveExactMatch(string configured, string needle, bool expected)
    {

        bool matches = ProviderResolver.ModelNameMatches(configured, needle);

        Assert.Equal(expected, matches);

    }

    [Fact]
    public void EnumerateAdvertisedModels_ReturnsDistinctNonEmptyModels()
    {

        ProviderSettings provider = new()
        {
            Name = "compat",
            Type = AiProviderKind.OpenAICompatible,
            Models = ["llama3", "llama3", " ", "mistral"],
        };

        string[] models = ProviderResolver.EnumerateAdvertisedModels(provider).ToArray();

        Assert.Equal(2, models.Length);

        Assert.Contains("llama3", models);

        Assert.Contains("mistral", models);

    }

    [Fact]
    public void TryResolveProviderForModel_ExplicitTarget_FindsMatchingProvider()
    {

        ProviderSettings provider = new()
        {
            Name = "compat",
            Type = AiProviderKind.OpenAICompatible,
            Models = ["llama3:latest"],
        };

        ArcanumSettings settings = new() { Providers = [provider] };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            "llama3:latest",
            out ProviderSettings? found,
            out string resolvedModel);

        Assert.True(resolved);

        Assert.Same(provider, found);

        Assert.Equal("llama3:latest", resolvedModel);

    }

    [Fact]
    public void TryResolveProviderForModel_OpenAICompatibleOllamaEndpoint_ExactModelOnly()
    {

        ProviderSettings provider = new()
        {
            Name = "Local Ollama",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "http://localhost:11434/v1",
            Models = ["mistral:latest"],
        };

        ArcanumSettings settings = new() { Providers = [provider] };

        Assert.True(ProviderResolver.TryResolveProviderForModel(
            settings,
            "mistral:latest",
            out ProviderSettings? found,
            out string resolvedExact));

        Assert.Same(provider, found);

        Assert.Equal("mistral:latest", resolvedExact);

        Assert.False(ProviderResolver.TryResolveProviderForModel(
            settings,
            "mistral",
            out _,
            out _));

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
                    Type = AiProviderKind.OpenAICompatible,
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
            Type = AiProviderKind.OpenAICompatible,
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
                    Type = AiProviderKind.OpenAICompatible,
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
            Type = AiProviderKind.OpenAICompatible,
            Models = ["alpha", "beta"],
        };

        ProviderSettings second = new()
        {
            Name = "second",
            Type = AiProviderKind.OpenAICompatible,
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
