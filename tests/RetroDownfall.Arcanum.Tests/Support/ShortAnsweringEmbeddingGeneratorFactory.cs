using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// An embedding provider that answers every batch with fewer vectors than it was sent: a proxy that
/// drops an input, a model that skips one, a server still loading and answering an empty <c>data</c>
/// array. Microsoft.Extensions.AI does not enforce one vector per input, so this is the fault the
/// real <c>WeaveService</c> has to turn into a failure at the provider boundary.
/// </summary>
/// <remarks>
/// Consumers of <c>IWeaveService.EmbedBatchAsync</c> no longer re-check the vector count themselves, so
/// their tests must reach the fault through the real service rather than through a fake
/// <c>IWeaveService</c> that breaks the contract the service exists to uphold.
/// </remarks>
internal sealed class ShortAnsweringEmbeddingGeneratorFactory(int omittedVectors = 1) : IEmbeddingGeneratorFactory
{
    /// <summary>
    /// A real <see cref="WeaveService"/> over a provider that omits vectors, with embeddings enabled so
    /// the consumer reaches the provider rather than the feature-disabled fast path.
    /// </summary>
    internal static WeaveService CreateWeaveService(int omittedVectors = 1)
    {
        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "local",
                    Model = "test-embed",
                },
            },
        };

        ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        return new WeaveService(
            new ShortAnsweringEmbeddingGeneratorFactory(omittedVectors),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WeaveService>.Instance);
    }

    public Task<EmbeddingGeneratorLease> ResolveGeneratorAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new EmbeddingGeneratorLease(new ShortAnsweringGenerator(omittedVectors), ownsGenerator: false));

    private sealed class ShortAnsweringGenerator(int omittedVectors) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            int answered = Math.Max(0, values.Count() - omittedVectors);

            GeneratedEmbeddings<Embedding<float>> result = new(answered);

            for (int i = 0; i < answered; i++)
            {
                result.Add(new Embedding<float>(new float[] { 1f, 0f, 0f }));
            }

            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
