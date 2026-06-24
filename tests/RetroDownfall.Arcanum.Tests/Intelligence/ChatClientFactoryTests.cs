using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ChatClientFactoryTests
{

    [Fact]
    public async Task ResolveClientAsync_UnknownModel_Throws()
    {
        ChatClientFactory factory = CreateFactory(new ArcanumSettings());

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.ResolveClientAsync("missing-model", CancellationToken.None));

        Assert.Contains("No AI model could be resolved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveClientAsync_OllamaProvider_ReturnsLease()
    {
        ArcanumSettings settings = new()
        {
            DefaultModel = "mistral:latest",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.Ollama,
                    Endpoint = "http://127.0.0.1:11434",
                    Models = ["mistral:latest"],
                },
            ],
        };

        ChatClientFactory factory = CreateFactory(settings);

        using ChatClientLease lease = await factory.ResolveClientAsync(null, CancellationToken.None);

        Assert.Equal("mistral:latest", lease.ResolvedModel);

        Assert.True(lease.IsOllama);

        Assert.NotNull(lease.ChatClient);
    }

    [Fact]
    public async Task ResolveClientAsync_OpenAiCompatible_ReturnsLease()
    {
        ArcanumSettings settings = new()
        {
            DefaultModel = "gpt-test",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "compat",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://example.test/v1",
                    ApiKey = "sk-test",
                    Models = ["gpt-test"],
                },
            ],
        };

        ChatClientFactory factory = CreateFactory(settings);

        using ChatClientLease lease = await factory.ResolveClientAsync("gpt-test", CancellationToken.None);

        Assert.False(lease.IsOllama);

        Assert.Equal("gpt-test", lease.ResolvedModel);
    }

    [Fact]
    public async Task ResolveClientAsync_LlamaCppFailure_Throws()
    {
        ArcanumSettings settings = new()
        {
            DefaultModel = "local.gguf",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "llama",
                    Type = AiProviderKind.LlamaCppServer,
                    Endpoint = "http://127.0.0.1:8080",
                    Models = ["local.gguf"],
                },
            ],
        };

        ChatClientFactory factory = CreateFactory(settings, new FailingLlamaServerManager());

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.ResolveClientAsync("local.gguf", CancellationToken.None));

        Assert.Equal("server unavailable", ex.Message);
    }

    private static ChatClientFactory CreateFactory(
        ArcanumSettings settings,
        ILlamaServerManager? llama = null)
    {
        IDataProtectionProvider protection = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector secretProtector = new(protection);

        return new ChatClientFactory(
            new FakeHttpClientFactory(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            llama ?? new FakeLlamaServerManager(),
            secretProtector);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new();

    }

    private sealed class NoopDisposable : IDisposable
    {

        public void Dispose()
        {
        }

    }

    private sealed class FakeLlamaServerManager : ILlamaServerManager
    {

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
            Task.FromResult<IDisposable>(new NoopDisposable());

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
            Task.FromResult<IDisposable>(new NoopDisposable());

        public bool IsModelInUse(string cacheKey) => false;

        public bool IsLlamaServerAvailable() => false;

        public LlamaServerInfo? TryGetRunningServer(string cacheKey) => null;

        public Task<Result> StopAsync(string cacheKey, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<LlamaServerInfo> ListServers() => [];

    }

}
