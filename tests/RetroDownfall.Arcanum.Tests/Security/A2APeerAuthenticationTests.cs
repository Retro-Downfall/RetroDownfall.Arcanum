using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using RetroDownfall.Arcanum.Api.A2A;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class A2APeerAuthenticationTests
{
    private static readonly string PeerCredential = Convert.ToBase64String(Enumerable.Repeat((byte)72, 32).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Peer_credential_authenticates_mounted_card_and_work_routes(bool discovery, bool bearer)
    {
        using Fixture fixture = new();
        DefaultHttpContext context = fixture.Context(PeerCredential, discovery, bearer);

        Assert.True(await fixture.Authenticator.IsAuthorizedAsync(context));
        Assert.Equal("alpha", A2APeerAuthenticator.GetPeer(context)?.PeerId);
    }

    [Theory]
    [InlineData("/api/config")]
    [InlineData("/api/guild/quests")]
    [InlineData("/v1/chat/completions")]
    [InlineData("/api/conclave/a2a/callback/guess")]
    public async Task Peer_key_never_authenticates_a_route_without_explicit_A2A_metadata(string path)
    {
        using Fixture fixture = new();
        DefaultHttpContext context = fixture.Context(PeerCredential, false);
        context.SetEndpoint(null);
        context.Request.Path = path;

        Assert.False(await fixture.Authenticator.IsAuthorizedAsync(context));
        Assert.Null(A2APeerAuthenticator.GetPeer(context));
    }

    [Fact]
    public async Task Master_discovers_card_but_cannot_issue_or_disclose_peer_work()
    {
        using Fixture fixture = new();
        Assert.True(await fixture.Authenticator.IsAuthorizedAsync(fixture.Context("master", true)));
        DefaultHttpContext work = fixture.Context("master", false);

        Assert.False(await fixture.Authenticator.IsAuthorizedAsync(work));
        JsonHttpResult<ApiResponse<string>> refusal = Assert.IsType<JsonHttpResult<ApiResponse<string>>>(ApiKeyAuthenticator.Unauthorized(work));
        Assert.Equal(403, refusal.StatusCode);
        Assert.Equal(ErrorCodes.Guild.PeerGrantRequired, refusal.Value!.Error!.Value.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Simultaneous_reusable_key_presentations_refuse_even_if_either_is_valid(bool useMaster)
    {
        using Fixture fixture = new();
        DefaultHttpContext context = fixture.Context(useMaster ? "master" : PeerCredential, true);
        context.Request.Headers.Authorization = "Bearer " + PeerCredential;
        Assert.False(await fixture.Authenticator.IsAuthorizedAsync(context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_header_values_never_select_a_peer(bool bearer)
    {
        using Fixture fixture = new();
        DefaultHttpContext context = fixture.Context(PeerCredential, false, bearer);
        context.Request.Headers[bearer ? "Authorization" : ArcanumApiHeaders.ApiKey] = new StringValues([
            bearer ? "Bearer " + PeerCredential : PeerCredential,
            bearer ? "Bearer " + PeerCredential : PeerCredential]);
        Assert.False(await fixture.Authenticator.IsAuthorizedAsync(context));
    }

    [Fact]
    public async Task Endpoint_filter_enters_verified_request_scope_and_clears_it_after_execution()
    {
        using Fixture fixture = new();
        DefaultHttpContext context = fixture.Context(PeerCredential, false);
        GuildInboundRequestContext request = fixture.Services.GetRequiredService<GuildInboundRequestContext>();
        ApiKeyEndpointFilter filter = new(fixture.Authenticator);
        bool called = false;

        await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(context), _ =>
        {
            called = true;
            Assert.Equal("alpha", request.Peer?.PeerId);
            return ValueTask.FromResult<object?>(Results.Ok());
        });

        Assert.True(called);
        Assert.Null(request.Peer);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ServiceProvider Services;
        internal readonly ApiKeyAuthenticator Authenticator;

        internal Fixture()
        {
            ArcanumSettings settings = new();
            settings.Features.Guild = true;
            settings.Features.A2AServer = true;
            settings.Integrations.A2A.InboundPeers = [new()
            {
                PeerId = "alpha", Enabled = true, CredentialEnvironmentVariable = "PEER_A", CampaignId = null,
                Grant = new() { Workspaces = [], Tools = [], Memory = [], Delegation = [], ExpiresAtUtc = null,
                    Budget = new() { Usd = new() { Mode = GuildBudgetMode.Unlimited }, Tokens = new() { Mode = GuildBudgetMode.Unlimited } } },
            }];
            TestOptionsMonitor<ArcanumSettings> options = new(settings);
            ApiKeyDigestCache cache = new(TimeProvider.System);
            cache.StoreDigest(SHA256.HashData(Encoding.UTF8.GetBytes("master")), 300);
            GuildInboundPeerRegistry registry = new(options, cache, _ => PeerCredential);
            Authenticator = new(new TestApiKeySecretStore("master"), cache, NullLogger<ApiKeyAuthenticator>.Instance);
            ServiceCollection services = new();
            services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(options);
            services.AddSingleton(registry);
            services.AddSingleton(Authenticator);
            services.AddSingleton<GuildInboundRequestContext>();
            services.AddSingleton<A2APeerAuthenticator>();
            Services = services.BuildServiceProvider();
        }

        internal DefaultHttpContext Context(string key, bool discovery, bool bearer = false)
        {
            DefaultHttpContext context = new() { RequestServices = Services };
            context.Request.Path = discovery ? "/api/custom/card" : "/api/custom/rpc";
            context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new A2APeerRouteMetadata(discovery)), "A2A"));
            context.Request.Headers[bearer ? "Authorization" : ArcanumApiHeaders.ApiKey] = bearer ? "Bearer " + key : key;
            return context;
        }

        public void Dispose() => Services.Dispose();
    }
}
