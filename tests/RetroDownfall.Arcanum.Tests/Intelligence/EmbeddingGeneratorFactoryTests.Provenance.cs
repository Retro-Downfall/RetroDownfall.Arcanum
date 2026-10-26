using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class EmbeddingGeneratorFactoryTests
{
    [Fact]
    public async Task EmbeddingProvenance_FactoryCapturesTheModelActuallyResolvedAcrossOptionsChange()
    {
        ArcanumSettings original = new()
        {
            Features = new() { Embeddings = true },
            Integrations = new() { Embeddings = new() { Provider = "local", Model = "original-model" } },
            Providers = [new() { Name = "local", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://example.test/v1" }],
        };

        MutableOptionsMonitor<ArcanumSettings> monitor = new(original);

        EmbeddingGeneratorFactory factory = new(new FakeHttpClientFactory(), monitor, new OptionsChangingKeyResolver(() =>
        {
            monitor.CurrentValue = new()
            {
                Features = new() { Embeddings = true },
                Integrations = new() { Embeddings = new() { Provider = "new-provider", Model = "replacement-model" } },
            };
        }));

        using EmbeddingGeneratorLease lease = await factory.ResolveGeneratorAsync(CancellationToken.None);

        Assert.Equal("replacement-model", monitor.CurrentValue.Integrations.Embeddings.Model);

        Assert.Equal(new EmbeddingProvenance("local", "original-model"), lease.Provenance);

        Assert.Equal("original-model", lease.Generator.GetService<EmbeddingGeneratorMetadata>()!.DefaultModelId);
    }

    private sealed class OptionsChangingKeyResolver(Action change) : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
        {
            change();

            return Task.FromResult<string?>(null);
        }
    }
}
