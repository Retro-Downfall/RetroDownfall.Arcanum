using System.Net;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class PromptCacheIdentityTests
{
    [Fact]
    public void Configuration_noop_is_stable_but_observed_ABA_and_in_place_mutation_revoke_stamp()
    {
        var options = new LongRestConfigurationEpochTests.NotifyingOptions(Settings());

        using PromptCacheConfigurationEpoch epoch = new(options, Guid.NewGuid());

        var first = epoch.Capture();

        Assert.True(epoch.IsCurrent(first.Stamp));

        options.Set(Settings());

        Assert.Equal(first.Stamp, epoch.Capture().Stamp);

        ArcanumSettings changed = Settings();

        changed.Providers[0].Endpoint = "https://b.test/v1";

        options.Set(changed);

        options.Set(Settings());

        Assert.False(epoch.IsCurrent(first.Stamp));

        var last = epoch.Capture();

        Assert.Equal(2, last.Stamp.Epoch);

        Assert.Equal(first.Stamp.ValueDigest, last.Stamp.ValueDigest);

        options.CurrentValue.Providers[0].Models[0].Name = "mutated";

        Assert.False(epoch.IsCurrent(last.Stamp));
    }

    [Fact]
    public void Captured_settings_are_frozen_and_each_read_is_an_isolated_copy()
    {
        var options = new LongRestConfigurationEpochTests.NotifyingOptions(Settings());

        using PromptCacheConfigurationEpoch epoch = new(options, Guid.NewGuid());

        var snapshot = epoch.Capture();

        options.CurrentValue.Providers[0].Models[0].Name = "external";

        snapshot.ReadSettings().Providers[0].Endpoint = "https://caller.test/v1";

        Assert.Equal("model", snapshot.ReadSettings().Providers[0].Models[0].Name);

        Assert.Equal("https://provider.test/v1", snapshot.ReadSettings().Providers[0].Endpoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_uses_bound_combined_credential_and_frozen_settings_only_when_enabled(bool enabled)
    {
        ArcanumSettings settings = Settings();

        settings.Features.DynamicContextInjectionV2 = enabled;

        var options = new LongRestConfigurationEpochTests.NotifyingOptions(settings);

        using PromptCacheConfigurationEpoch epoch = new(options, Guid.NewGuid());

        Handler handler = new();

        Resolver resolver = new() { OnCapture = () => settings.Providers[0].Endpoint = "https://mutated.test/v1" };

        ChatClientFactory factory = new(new HttpFactory(handler), options, resolver, promptCacheConfigurationEpoch: epoch);

        using ChatClientLease lease = await factory.ResolveClientAsync(null, CancellationToken.None);

        await lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "public")]);

        Assert.Equal(enabled ? "combined" : "ordinary", handler.Bearer);

        Assert.Equal("provider.test", handler.Host);

        Assert.Equal(enabled ? 0 : 1, resolver.OrdinaryReads);

        Assert.Equal(enabled ? 1 : 0, resolver.IdentityReads);

        if (enabled)
        {
            Assert.NotNull(lease.PromptCacheIdentity);

            Assert.Same(lease.ChatClient, lease.PromptCacheIdentity.Client);

            Assert.Equal(resolver.Stamp, lease.PromptCacheIdentity.CredentialStamp);

            lease.Provider.Endpoint = "https://caller.test/v1";

            lease.PromptCacheIdentity.Provider.Models[0].Name = "caller";

            Assert.Equal("https://provider.test/v1", lease.PromptCacheIdentity.Provider.Endpoint);

            Assert.Equal("model", lease.PromptCacheIdentity.Provider.Models[0].Name);

            Assert.False(epoch.IsCurrent(lease.PromptCacheIdentity.ConfigurationStamp));
        }
        else
        {
            Assert.Null(lease.PromptCacheIdentity);
        }
    }

    [Fact]
    public async Task Explicit_fallback_binds_actual_frozen_selection_and_legacy_resolver_has_no_proof()
    {
        var options = new LongRestConfigurationEpochTests.NotifyingOptions(Settings());

        using PromptCacheConfigurationEpoch epoch = new(options, Guid.NewGuid());

        Handler handler = new();

        ProviderSettings fallback = new() { Name = "fallback", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://fallback.test/v1", Models = ["fallback-model"] };

        ChatClientFactory factory = new(new HttpFactory(handler), options, new LegacyResolver(), promptCacheConfigurationEpoch: epoch);

        using ChatClientLease lease = await factory.ResolveClientAsync(fallback, "fallback-model", CancellationToken.None);

        fallback.Endpoint = "https://mutated.test/v1";

        await lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "public")]);

        Assert.Equal("fallback.test", handler.Host);

        Assert.NotNull(lease.PromptCacheIdentity);

        Assert.Null(lease.PromptCacheIdentity.CredentialStamp);

        Assert.Equal("https://fallback.test/v1", lease.PromptCacheIdentity.Provider.Endpoint);

        Assert.Equal("fallback-model", lease.PromptCacheIdentity.ResolvedModel);
    }

    private static ArcanumSettings Settings() => new()
    {
        Features = new() { DynamicContextInjectionV2 = true },

        DefaultModel = "model",

        Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://provider.test/v1", Models = ["model"] }],
    };

    private sealed class Resolver : IProviderApiKeyResolver
    {
        public ProviderCredentialStamp Stamp { get; } = new(Guid.NewGuid(), Guid.NewGuid(), 1);

        public int OrdinaryReads { get; private set; }

        public int IdentityReads { get; private set; }

        public Action? OnCapture { get; init; }

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
        {
            OrdinaryReads++;

            return Task.FromResult<string?>("ordinary");
        }

        public Task<ResolvedProviderCredential> ResolveWithIdentityAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
        {
            IdentityReads++;

            OnCapture?.Invoke();

            return Task.FromResult(new ResolvedProviderCredential("combined", Stamp));
        }
    }

    private sealed class LegacyResolver : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>("legacy");
    }

    private sealed class HttpFactory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? Bearer { get; private set; }

        public string? Host { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bearer = request.Headers.Authorization?.Parameter;

            Host = request.RequestUri!.Host;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"chat-test","object":"chat.completion","created":1,"model":"model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
