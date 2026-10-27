using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Infrastructure.Familiars;
using RetroDownfall.Arcanum.Tests.Familiars;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TapestryProviderSendTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task EveryPhysicalAttempt_RequiresCurrentSourceProof(bool embedding, int refuseAt)
    {
        var proof = new RefusingValidator(refuseAt);

        using var handler = new RetryHandler();

        Exception? refusal = await SendAsync(embedding, handler, proof, sdkRetries: refuseAt > 1);

        Assert.NotNull(refusal);

        Assert.Equal(refuseAt - 1, handler.Sends);

        Assert.Equal(refuseAt, proof.Calls);

        Assert.All(proof.Identities, identity => Assert.Equal(("provider", embedding ? "embed" : "model", embedding), identity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSourceValidator_CannotSendTapestryContent(bool embedding)
    {
        using var handler = new RetryHandler();

        Assert.NotNull(await SendAsync(embedding, handler, null));

        Assert.Equal(0, handler.Sends);
    }

    [Fact]
    public async Task ProductionChatRegistration_PassesTheTypedSourceValidatorToThePhysicalPolicy()
    {
        ServiceCollection production = [];

        production.AddArcanumApiServices(new ConfigurationBuilder().Build());

        ServiceCollection services = [];

        var settings = new ArcanumSettings
        {
            DefaultModel = "model",
            Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://provider.test/v1", Models = ["model"] }],
        };

        var monitor = new TestOptionsMonitor<ArcanumSettings>(settings);

        using var handler = new RetryHandler();

        var validator = new RefusingValidator(1);

        services.AddSingleton<IHttpClientFactory>(new HttpFactory(handler));

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(monitor);

        services.AddSingleton<IProviderApiKeyResolver>(new Keys());

        services.AddSingleton<IFamiliarProcessRunner>(new RecordingFamiliarProcessRunner());

        services.AddLogging();

        services.AddSingleton(new PromptCacheConfigurationEpoch(monitor, Guid.NewGuid()));

        services.AddSingleton<ITapestryProviderValidator>(validator);

        ((IServiceCollection)services).Add(production.Single(d => d.ServiceType == typeof(IChatClientFactory)));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using IDisposable observation = TapestryBuildContext.Enter(new(
            new(new(TapestryScopeKind.Workspace, "/repo"), 1, 0, new(12, "source", false)),
            "provider", "model", "provider", "embed"));

        using var chat = await provider.GetRequiredService<IChatClientFactory>().ResolveClientAsync("model", default);

        Assert.NotNull(await Record.ExceptionAsync(async () => await chat.ChatClient.GetResponseAsync("source")));

        Assert.Equal(1, validator.Calls);

        Assert.Equal(0, handler.Sends);
    }

    [Fact]
    public void ProductionInfrastructure_RegistersTheSourceValidatorAndRaptorClusterer()
    {
        ServiceCollection services = [];

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        Assert.Contains(services, service => service.ServiceType == typeof(ITapestryProviderValidator));

        Assert.Contains(services, service => service.ServiceType == typeof(RetroDownfall.Arcanum.Core.Weave.Tapestry.ITapestryClusterer));
    }

    private static async Task<Exception?> SendAsync(bool embedding, RetryHandler handler, ITapestryProviderValidator? validator, bool sdkRetries = false)
    {
        var settings = new ArcanumSettings
        {
            DefaultModel = "model",
            Features = new() { Tapestry = true },
            Integrations = new() { Embeddings = new() { Provider = "provider", Model = "embed" } },
            Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://provider.test/v1", Models = ["model", "embed"] }],
        };

        var monitor = new TestOptionsMonitor<ArcanumSettings>(settings);

        using IDisposable observation = TapestryBuildContext.Enter(new(
            new(new(TapestryScopeKind.Workspace, "/repo"), 1, 0, new(12, "source", false)),
            "provider", "model", "provider", "embed"));

        if (embedding)
        {
            var factory = new EmbeddingGeneratorFactory(new HttpFactory(handler), monitor, new Keys(), validator);

            using EmbeddingGeneratorLease lease = await factory.ResolveGeneratorAsync(CancellationToken.None);

            if (!sdkRetries) return await Record.ExceptionAsync(async () => await lease.Generator.GenerateAsync(["full source"]));

            var retry = ProviderSdkTestTransport.RetryEmbedding(lease, settings.Providers[0], handler, validator);

            using HttpClient transport = retry.Transport;

            using EmbeddingGeneratorLease retryLease = retry.Lease;

            return await Record.ExceptionAsync(async () => await retryLease.Generator.GenerateAsync(["full source"]));
        }

        var chats = new ChatClientFactory(new HttpFactory(handler), monitor, new Keys(), null, null, null, validator);

        using ChatClientLease chat = await chats.ResolveClientAsync("model", CancellationToken.None);

        using ChatClientLease? retried = sdkRetries ? ProviderSdkTestTransport.RetryChat(chat, handler, validator) : null;

        return await Record.ExceptionAsync(async () => await (retried ?? chat).ChatClient.GetResponseAsync("summary source"));
    }

    private sealed class RefusingValidator(int refuseAt) : ITapestryProviderValidator
    {
        public int Calls { get; private set; }

        public List<(string Provider, string Model, bool Embedding)> Identities { get; } = [];

        public Task<Result> ValidateAsync(string provider, string model, bool embedding, CancellationToken token)
        {
            Calls++;

            Identities.Add((provider, model, embedding));

            return Task.FromResult(Calls >= refuseAt
                ? Result.Failure(new Error(ErrorCodes.Hub.InvalidAdmission, "Source changed."))
                : Result.Success());
        }
    }

    private sealed class Keys : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RetryHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++;

            if (Sends == 1)
            {
                var retry = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = ProviderSdkTestTransport.Content("{}", "application/json") };

                retry.Headers.Add("retry-after-ms", "1");

                return Task.FromResult(retry);
            }

            string json = request.RequestUri!.AbsolutePath.EndsWith("embeddings", StringComparison.Ordinal)
                ? """{"object":"list","data":[{"object":"embedding","index":0,"embedding":[1,0,0]}],"model":"embed","usage":{"prompt_tokens":1,"total_tokens":1}}"""
                : """{"id":"chat-test","object":"chat.completion","created":1,"model":"model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = ProviderSdkTestTransport.Content(json, "application/json") });
        }
    }
}
