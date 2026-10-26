using RetroDownfall.Arcanum.Core.Weave;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WeaveServiceTests
{
    [Fact]
    public async Task EmbeddingProvenance_UsesResolvedLeaseAcrossOptionsChange_AndOverwritesProviderMetadata()
    {
        ArcanumSettings settings = EnabledSettings();

        settings.Integrations.Embeddings.Model = "original-model";

        EmbeddingProvenance resolved = new("local", "original-model");

        FakeEmbeddingGeneratorFactory factory = new() { Provenance = resolved };

        factory.Generator.ProviderProvenance = new("forged-provider", "forged-model");

        factory.Generator.BeforeGenerate = () => settings.Integrations.Embeddings.Model = "replacement-model";

        Result<Embedding<float>> result = await CreateService(settings, factory).EmbedAsync("query", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("replacement-model", settings.Integrations.Embeddings.Model);

        Assert.Same(resolved, result.Value.AdditionalProperties![EmbeddingProvenance.MetadataKey]);
    }

    [Fact]
    public async Task EmbeddingProvenance_UnstampedLeaseCannotAdoptProviderMetadata()
    {
        FakeEmbeddingGeneratorFactory factory = new();

        factory.Generator.ProviderProvenance = new("forged-provider", "forged-model");

        Result<Embedding<float>> result = await CreateService(EnabledSettings(), factory).EmbedAsync("query", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.False(result.Value.AdditionalProperties?.ContainsKey(EmbeddingProvenance.MetadataKey) ?? false);
    }
}
