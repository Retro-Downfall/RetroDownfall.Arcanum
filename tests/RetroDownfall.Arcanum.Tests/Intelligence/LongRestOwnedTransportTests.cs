using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class LongRestOwnedTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_resolution_does_not_execute_configured_factory_or_hooks(bool embedding)
    {
        using Setup setup = new("success");

        using IServiceScope scope = setup.Services.CreateScope();

        LongRestProviderClientFactory factory = scope.ServiceProvider.GetRequiredService<LongRestProviderClientFactory>();

        if (embedding)
        {
            var result = await factory.ResolveEmbeddingAsync(Selection(true), CancellationToken.None);

            Assert.True(result.IsSuccess);

            using EmbeddingGeneratorLease lease = result.Value;
        }
        else
        {
            var result = await factory.ResolveChatAsync(Selection(false), CancellationToken.None);

            Assert.True(result.IsSuccess);

            using ChatClientLease lease = result.Value;
        }

        Assert.Equal(1, setup.Keys.Calls);

        Assert.Equal(0, setup.Factory.Calls);

        Assert.Equal(0, setup.ClientActions);

        Assert.Equal(0, setup.HandlerActions);

        Assert.Equal(0, setup.Handler.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_failure_precedes_any_configured_transport_work(bool embedding)
    {
        using Setup setup = new("failure");

        using IServiceScope scope = setup.Services.CreateScope();

        LongRestProviderClientFactory factory = scope.ServiceProvider.GetRequiredService<LongRestProviderClientFactory>();

        if (embedding)
        {
            var result = await factory.ResolveEmbeddingAsync(Selection(true), CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.DoesNotContain("protected-key-body", result.Error.Message, StringComparison.Ordinal);
        }
        else
        {
            var result = await factory.ResolveChatAsync(Selection(false), CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.DoesNotContain("protected-key-body", result.Error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1, setup.Keys.Calls);

        Assert.Equal(0, setup.Factory.Calls);

        Assert.Equal(0, setup.ClientActions);

        Assert.Equal(0, setup.HandlerActions);

        Assert.Equal(0, setup.Handler.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_from_credential_resolution_precedes_configured_transport_work(bool embedding)
    {
        using CancellationTokenSource cancellation = new();

        using Setup setup = new("cancel", cancellation);

        using IServiceScope scope = setup.Services.CreateScope();

        LongRestProviderClientFactory factory = scope.ServiceProvider.GetRequiredService<LongRestProviderClientFactory>();

        if (embedding)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                _ = await factory.ResolveEmbeddingAsync(Selection(true), cancellation.Token);
            });
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                _ = await factory.ResolveChatAsync(Selection(false), cancellation.Token);
            });
        }

        Assert.Equal(1, setup.Keys.Calls);

        Assert.True(cancellation.IsCancellationRequested);

        Assert.Equal(0, setup.Factory.Calls);

        Assert.Equal(0, setup.ClientActions);

        Assert.Equal(0, setup.HandlerActions);

        Assert.Equal(0, setup.Handler.Sends);
    }

    private static LongRestProviderSelection Selection(bool embedding)
    {
        using LongRestConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(
            LongRestConfigurationEpochTests.Settings()), Guid.NewGuid());

        return embedding ? epoch.Capture().ResolveEmbedding().Value : epoch.Capture().ResolveChat().Value;
    }

    private sealed class Setup : IDisposable
    {
        private readonly ServiceProvider _configured;

        internal readonly NeverSendHandler Handler = new();

        internal readonly KeyResolver Keys;

        internal readonly EffectFactory Factory;

        internal readonly ServiceProvider Services;

        internal int ClientActions;

        internal int HandlerActions;

        internal Setup(string mode, CancellationTokenSource? cancellation = null)
        {
            ServiceCollection configured = new();

            configured.AddHttpClient("LongRestProvider", _ => ClientActions++)
                .ConfigurePrimaryHttpMessageHandler(() => { HandlerActions++; return Handler; });

            _configured = configured.BuildServiceProvider();

            Factory = new(_configured.GetRequiredService<IHttpClientFactory>());

            Keys = new(mode, cancellation);

            ServiceCollection services = new();

            services.AddSingleton<IHttpClientFactory>(Factory);

            services.AddSingleton<IProviderApiKeyResolver>(Keys);

            services.AddLogging();

            services.AddScoped<LongRestProviderClientFactory>();

            Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        public void Dispose()
        {
            Services.Dispose();

            Factory.Dispose();

            _configured.Dispose();

            Handler.Dispose();
        }
    }

    private sealed class EffectFactory(IHttpClientFactory configured) : IHttpClientFactory, IDisposable
    {
        private readonly List<HttpClient> _clients = [];

        internal int Calls;

        public HttpClient CreateClient(string name)
        {
            Calls++;

            HttpClient client = configured.CreateClient(name);

            _clients.Add(client);

            return client;
        }

        public void Dispose()
        {
            foreach (HttpClient client in _clients) client.Dispose();
        }
    }

    private sealed class KeyResolver(string mode, CancellationTokenSource? cancellation) : IProviderApiKeyResolver
    {
        internal int Calls;

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken)
        {
            Calls++;

            if (mode == "failure") return Task.FromException<string?>(new InvalidOperationException("protected-key-body"));

            if (mode == "cancel")
            {
                cancellation!.Cancel();

                return Task.FromCanceled<string?>(cancellationToken);
            }

            return Task.FromResult<string?>("test-key");
        }
    }

    private sealed class NeverSendHandler : HttpMessageHandler
    {
        internal int Sends;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }
    }
}
