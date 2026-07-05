using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class EmbeddingGeneratorFactoryTests
{

    [Fact]
    public async Task ResolveGeneratorAsync_EmbeddingsDisabled_Throws()
    {

        EmbeddingGeneratorFactory factory = CreateFactory(new ArcanumSettings
        {
            Embeddings = new EmbeddingSettings { Enabled = false },
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
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "local" },
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.ResolveGeneratorAsync(CancellationToken.None));

    }

    [Fact]
    public async Task ResolveGeneratorAsync_UnknownProviderName_Throws()
    {

        ArcanumSettings settings = new()
        {
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "missing", Model = "nomic-embed-text" },
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
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "local", Model = "nomic-embed-text" },
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

        ArcanumSettings settings = new()
        {
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "compat", Model = "text-embedding-3-small" },
            Providers =
            [
                new ProviderSettings { Name = "compat", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://example.test/v1", ApiKey = "sk-test", Models = ["text-embedding-3-small"] },
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
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "compat", Model = "text-embedding-3-small" },
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
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "compat", Model = "text-embedding-3-small" },
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
    public async Task ResolveGeneratorAsync_ApiKeyChangedViaHotReload_BuildsNewGenerator()
    {

        MutableOptionsMonitor<ArcanumSettings> monitor = new(new ArcanumSettings
        {
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "compat", Model = "text-embedding-3-small" },
            Providers =
            [
                new ProviderSettings { Name = "compat", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://example.test/v1", ApiKey = "sk-old", Models = ["text-embedding-3-small"] },
            ],
        });

        EmbeddingGeneratorFactory factory = CreateFactoryWithMonitor(monitor);

        using EmbeddingGeneratorLease first = await factory.ResolveGeneratorAsync(CancellationToken.None);

        // Hot-reload: the operator rotates the API key for the same provider name/model/endpoint.
        monitor.CurrentValue = monitor.CurrentValue with
        {
            Providers =
            [
                new ProviderSettings { Name = "compat", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://example.test/v1", ApiKey = "sk-new", Models = ["text-embedding-3-small"] },
            ],
        };

        using EmbeddingGeneratorLease second = await factory.ResolveGeneratorAsync(CancellationToken.None);

        Assert.NotSame(first.Generator, second.Generator);

    }

    [Fact]
    public async Task ResolveGeneratorAsync_LlamaCppFailure_Throws()
    {

        ArcanumSettings settings = new()
        {
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "llama", Model = "local.gguf" },
            Providers =
            [
                new ProviderSettings { Name = "llama", Type = AiProviderKind.LlamaCppServer, Endpoint = "http://127.0.0.1:8080", Models = ["local.gguf"] },
            ],
        };

        EmbeddingGeneratorFactory factory = CreateFactory(settings, new FailingLlamaServerManager());

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.ResolveGeneratorAsync(CancellationToken.None));

        Assert.Equal("server unavailable", ex.Message);

    }

    [Fact]
    public async Task ResolveGeneratorAsync_LlamaCppSuccess_OwnsGenerator_AndDisposeReleasesSlot()
    {

        TrackingLlamaServerManager llama = new();

        ArcanumSettings settings = new()
        {
            Embeddings = new EmbeddingSettings { Enabled = true, Provider = "llama", Model = "local.gguf" },
            Providers =
            [
                new ProviderSettings { Name = "llama", Type = AiProviderKind.LlamaCppServer, Endpoint = "http://127.0.0.1:8080", Models = ["local.gguf"] },
            ],
        };

        EmbeddingGeneratorFactory factory = CreateFactory(settings, llama);

        EmbeddingGeneratorLease lease = await factory.ResolveGeneratorAsync(CancellationToken.None);

        Assert.NotNull(lease.Generator);

        Assert.False(llama.SlotDisposed);

        lease.Dispose();

        Assert.True(llama.SlotDisposed);

    }

    private static EmbeddingGeneratorFactory CreateFactory(
        ArcanumSettings settings,
        ILlamaServerManager? llama = null)
    {

        return CreateFactoryWithMonitor(new TestOptionsMonitor<ArcanumSettings>(settings), llama);

    }

    private static EmbeddingGeneratorFactory CreateFactoryWithMonitor(
        Microsoft.Extensions.Options.IOptionsMonitor<ArcanumSettings> monitor,
        ILlamaServerManager? llama = null)
    {

        IDataProtectionProvider protection = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector secretProtector = new(protection);

        return new EmbeddingGeneratorFactory(
            new FakeHttpClientFactory(),
            monitor,
            llama ?? new TrackingLlamaServerManager(),
            secretProtector);

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

    private sealed class TrackingDisposable : IDisposable
    {

        public bool Disposed { get; private set; }

        public void Dispose()
        {

            Disposed = true;

        }

    }

    private sealed class TrackingLlamaServerManager : ILlamaServerManager
    {

        private readonly TrackingDisposable _slot = new();

        public bool SlotDisposed => _slot.Disposed;

        public Task<Result<LlamaServerInfo>> EnsureServerAsync(
            string modelKey,
            string? sourceUrl,
            int? gpuLayersOverride,
            int? portOverride,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<LlamaServerInfo>.Success(new LlamaServerInfo
            {
                Endpoint = "http://127.0.0.1:8081",
                Port = 8081,
            }));

        public Task<IDisposable> AcquireSlotAsync(string modelKey, CancellationToken cancellationToken) =>
            Task.FromResult<IDisposable>(_slot);

        public bool IsModelInUse(string cacheKey) => false;

        public bool IsLlamaServerAvailable() => true;

        public LlamaServerInfo? TryGetRunningServer(string cacheKey) => null;

        public Task<Result> StopAsync(string cacheKey, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<LlamaServerInfo> ListServers() => [];

    }

    private sealed class FailingLlamaServerManager : ILlamaServerManager
    {

        public Task<Result<LlamaServerInfo>> EnsureServerAsync(
            string modelKey,
            string? sourceUrl,
            int? gpuLayersOverride,
            int? portOverride,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<LlamaServerInfo>.Failure(new Error("Llama.Down", "server unavailable")));

        public Task<IDisposable> AcquireSlotAsync(string modelKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Should not be called when EnsureServerAsync fails.");

        public bool IsModelInUse(string cacheKey) => false;

        public bool IsLlamaServerAvailable() => false;

        public LlamaServerInfo? TryGetRunningServer(string cacheKey) => null;

        public Task<Result> StopAsync(string cacheKey, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<LlamaServerInfo> ListServers() => [];

    }

}
