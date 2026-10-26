using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class EmbeddingProviderIdentityTests
{
    [Theory]
    [InlineData("LOCAL", "local")]
    [InlineData("LoCaL", "local")]
    [InlineData("missing", "missing")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void ResolveEmbeddings_CanonicalizesOnlyKnownProviderWithoutMutatingConfiguration(string? configured, string? expected)
    {
        ArcanumSettings settings = new()
        {
            Integrations = new() { Embeddings = new() { Provider = configured, Model = "Exact-Model" } },
            Providers = [new() { Name = "local" }],
        };

        EmbeddingSettings runtime = settings.ResolveEmbeddings();

        Assert.Equal(expected, runtime.Provider);

        Assert.Equal(configured, settings.Integrations.Embeddings.Provider);

        Assert.Equal("local", settings.Providers[0].Name);

        Assert.Equal("Exact-Model", runtime.Model);

        Assert.NotEqual(TapestryHash.OfEmbeddingModel(runtime.Provider, runtime.Model), TapestryHash.OfEmbeddingModel(runtime.Provider, "exact-model"));
    }
}
