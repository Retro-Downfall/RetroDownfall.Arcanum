using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
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
    public async Task ResolveClientAsync_OllamaViaOpenAiCompatible_ReturnsLease()
    {
        ArcanumSettings settings = new()
        {
            DefaultModel = "mistral:latest",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "http://127.0.0.1:11434/v1",
                    Models = ["mistral:latest"],
                },
            ],
        };

        ChatClientFactory factory = CreateFactory(settings);

        using ChatClientLease lease = await factory.ResolveClientAsync(null, CancellationToken.None);

        Assert.Equal("mistral:latest", lease.ResolvedModel);

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

        Assert.Equal("gpt-test", lease.ResolvedModel);
    }

    private static ChatClientFactory CreateFactory(ArcanumSettings settings)
    {
        IDataProtectionProvider protection = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector secretProtector = new(protection);

        return new ChatClientFactory(
            new FakeHttpClientFactory(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            secretProtector);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new();

    }

}
