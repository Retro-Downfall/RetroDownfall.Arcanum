using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("ProcessEnvironment")]
public sealed class EmbeddingGeneratorFactoryTests : IDisposable
{
    private const string CredentialVariable = "ARCANUM_TEST_EMBEDDING_PROVIDER_KEY";
    private readonly string? _originalCredential;

    public EmbeddingGeneratorFactoryTests()
    {
        _originalCredential =
            System.Environment.GetEnvironmentVariable(CredentialVariable);
        System.Environment.SetEnvironmentVariable(CredentialVariable, null);
    }
    [Fact]
    public async Task ResolveGeneratorAsync_EmbeddingsDisabled_Throws()
    {

        EmbeddingGeneratorFactory factory = CreateFactory(new ArcanumSettings
        {
            Features = new FeatureSettings { Embeddings = false },
        });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.ResolveGeneratorAsync(CancellationToken.None));

        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ResolveGeneratorAsync_MissingProviderOrModel_Throws()
    {

        EmbeddingGeneratorFactory factory = CreateFactory(new ArcanumSettings
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings { Provider = "local" },
            },
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.ResolveGeneratorAsync(CancellationToken.None));

    }

    [Fact]
    public async Task ResolveGeneratorAsync_UnknownProviderName_Throws()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "missing",
                    Model = "nomic-embed-text",
                },
            },
            Providers =
            [
                new ProviderSettings { Name = "local", Type = AiProviderKind.OpenAICompatible, Endpoint = "http://127.0.0.1:11434/v1", Models = ["nomic-embed-text"] },
            ],
        };

        EmbeddingGeneratorFactory factory = CreateFactory(settings);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.ResolveGeneratorAsync(CancellationToken.None));

        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ResolveGeneratorAsync_OllamaViaOpenAiCompatibleProvider_ReturnsLease()
    {

        // Ollama has no bespoke embedding integration — it is routed through the same
        // OpenAI-compatible EmbeddingClient path as any other AiProviderKind.OpenAICompatible provider.
        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "local",
                    Model = "nomic-embed-text",
                },
            },
            Providers =
            [
                new ProviderSettings { Name = "local", Type = AiProviderKind.OpenAICompatible, Endpoint = "http://127.0.0.1:11434/v1", Models = ["nomic-embed-text"] },
            ],
        };

        EmbeddingGeneratorFactory factory = CreateFactory(settings);

        using EmbeddingGeneratorLease lease = await factory.ResolveGeneratorAsync(CancellationToken.None);

        Assert.NotNull(lease.Generator);

    }

    [Fact]
    public async Task ResolveGeneratorAsync_OpenAiCompatibleProvider_ReturnsLease()
    {
        System.Environment.SetEnvironmentVariable(CredentialVariable, "sk-test");

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "compat",
                    Model = "text-embedding-3-small",
                },
            },
            Providers =
            [
                new ProviderSettings { Name = "compat", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://example.test/v1", CredentialEnvironmentVariable = CredentialVariable, Models = ["text-embedding-3-small"] },
            ],
        };

        EmbeddingGeneratorFactory factory = CreateFactory(settings);

        using EmbeddingGeneratorLease lease = await factory.ResolveGeneratorAsync(CancellationToken.None);

        Assert.NotNull(lease.Generator);

    }

    [Fact]
    public async Task ResolveGeneratorAsync_CachesGeneratorByProviderAndModel()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "compat",
                    Model = "text-embedding-3-small",
                },
            },
            Providers =
            [
                new ProviderSettings { Name = "compat", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://example.test/v1", Models = ["text-embedding-3-small"] },
            ],
        };

        EmbeddingGeneratorFactory factory = CreateFactory(settings);

        using EmbeddingGeneratorLease first = await factory.ResolveGeneratorAsync(CancellationToken.None);

        using EmbeddingGeneratorLease second = await factory.ResolveGeneratorAsync(CancellationToken.None);

        Assert.Same(first.Generator, second.Generator);

    }

    [Fact]
    public async Task ResolveGeneratorAsync_EndpointChangedViaHotReload_BuildsNewGenerator()
    {

        MutableOptionsMonitor<ArcanumSettings> monitor = new(new ArcanumSettings
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "compat",
                    Model = "text-embedding-3-small",
                },
            },
            Providers =
            [
                new ProviderSettings { Name = "compat", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://example.test/v1", Models = ["text-embedding-3-small"] },
            ],
        });

        EmbeddingGeneratorFactory factory = CreateFactoryWithMonitor(monitor);

        using EmbeddingGeneratorLease first = await factory.ResolveGeneratorAsync(CancellationToken.None);

        // Hot-reload: the operator changes the endpoint for the same provider name/model, without
        // an app restart. A cache keyed only on "providerName::model" would keep serving `first`'s
        // generator (built against the OLD endpoint) forever.
        monitor.CurrentValue = monitor.CurrentValue with
        {
            Providers =
            [
                new ProviderSettings { Name = "compat", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://changed.example.test/v1", Models = ["text-embedding-3-small"] },
            ],
        };

        using EmbeddingGeneratorLease second = await factory.ResolveGeneratorAsync(CancellationToken.None);

        Assert.NotSame(first.Generator, second.Generator);

    }

    [Fact]
    public async Task ResolveGeneratorAsync_EnvironmentCredentialRotation_BuildsNewGenerator()
    {
        System.Environment.SetEnvironmentVariable(CredentialVariable, "sk-old");

        MutableOptionsMonitor<ArcanumSettings> monitor = new(new ArcanumSettings
        {
            Features = new FeatureSettings { Embeddings = true },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "compat",
                    Model = "text-embedding-3-small",
                },
            },
            Providers =
            [
                new ProviderSettings { Name = "compat", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://example.test/v1", CredentialEnvironmentVariable = CredentialVariable, Models = ["text-embedding-3-small"] },
            ],
        });

        EmbeddingGeneratorFactory factory = CreateFactoryWithMonitor(monitor);

        using EmbeddingGeneratorLease first = await factory.ResolveGeneratorAsync(CancellationToken.None);

        // The environment value rotates without copying secret material into hot-reloaded settings.
        System.Environment.SetEnvironmentVariable(CredentialVariable, "sk-new");

        using EmbeddingGeneratorLease second = await factory.ResolveGeneratorAsync(CancellationToken.None);

        Assert.NotSame(first.Generator, second.Generator);

    }

    private static EmbeddingGeneratorFactory CreateFactory(ArcanumSettings settings)
    {

        return CreateFactoryWithMonitor(new TestOptionsMonitor<ArcanumSettings>(settings));

    }

    private static EmbeddingGeneratorFactory CreateFactoryWithMonitor(
        Microsoft.Extensions.Options.IOptionsMonitor<ArcanumSettings> monitor)
    {
        return new EmbeddingGeneratorFactory(
            new FakeHttpClientFactory(),
            monitor);

    }

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable(
            CredentialVariable,
            _originalCredential);
    }

    /// <summary>Simulates <c>IOptionsMonitor&lt;T&gt;.CurrentValue</c> changing across calls (hot-reload), unlike the fixed-snapshot <see cref="TestOptionsMonitor{T}"/>.</summary>
    private sealed class MutableOptionsMonitor<T>(T current) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {

        public T CurrentValue { get; set; } = current;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {

            public void Dispose()
            {
            }

        }

    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new();

    }

}
