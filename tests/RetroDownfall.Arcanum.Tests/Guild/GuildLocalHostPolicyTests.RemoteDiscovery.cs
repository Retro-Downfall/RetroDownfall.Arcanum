using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using A2A;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Configured_remote_resolution_preserves_stable_identity_and_creates_no_Quest()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        ConfigureRemote(fixture);
        using RemoteCardHandler handler = new();
        GuildConfiguredRemoteResolver resolver = RemoteResolver(fixture, handler);
        GuildCallerContext caller = await fixture.OperatorAsync();

        var first = await resolver.ResolveAsync(caller, RemoteDiscovery, default);
        var replay = await resolver.ResolveAsync(caller, RemoteDiscovery, default);

        Assert.True(first.IsSuccess, first.Error.Message);
        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.NotEqual(Guid.Empty, first.Value.AgentId);
        Assert.Equal(first.Value.AgentId, replay.Value.AgentId);
        Assert.Equal(first.Value.Revision, replay.Value.Revision);
        Assert.Equal(GuildAgentSourceKind.Remote, first.Value.Source.Kind);
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM guild_quests"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM guild_signets"));
    }

    [Fact]
    public async Task Copied_operator_identity_cannot_resolve_remote_cards()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        ConfigureRemote(fixture);
        using RemoteCardHandler handler = new();
        GuildConfiguredRemoteResolver resolver = RemoteResolver(fixture, handler);
        GuildCallerContext original = await fixture.OperatorAsync();
        GuildCallerContext copied = new(GuildCallerKind.Operator, original.PrincipalId!.Value,
            original.InvocationId, original.InstallationGeneration);

        var result = await resolver.ResolveAsync(copied, RemoteDiscovery, default);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Guild.Forbidden, result.Error.Code);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Destination_removed_during_discovery_is_not_persisted_as_available()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        ConfigureRemote(fixture);
        using RemoteCardHandler handler = new() { OnRead = () => fixture.Settings.CurrentValue.Integrations.A2A.AllowedRemoteAgents = [] };
        GuildConfiguredRemoteResolver resolver = RemoteResolver(fixture, handler);

        var result = await resolver.ResolveAsync(await fixture.OperatorAsync(), RemoteDiscovery, default);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Guild.DestinationChanged, result.Error.Code);
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM guild_agent_cards WHERE SourceKindCode=3"));
    }

    private const string RemoteDiscovery = "https://configured-remote.example/";

    private static void ConfigureRemote(Fixture fixture)
    {
        fixture.Settings.CurrentValue.Features.A2AClient = true;
        fixture.Settings.CurrentValue.Integrations.A2A.AllowedRemoteAgents = [RemoteDiscovery];
    }

    private static GuildConfiguredRemoteResolver RemoteResolver(Fixture fixture, RemoteCardHandler handler)
    {
        FakeDnsResolver dns = new();
        dns.Add("configured-remote.example", IPAddress.Parse("93.184.216.34"));
        A2AClientService client = new(new RemoteHttpFactory(handler), fixture.Settings, NullLogger<A2AClientService>.Instance, dns);
        return new(fixture.Policy, new(fixture.Connections, fixture.Gate, TimeProvider.System), client, fixture.Settings, fixture.Authority);
    }

    private sealed class RemoteHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class RemoteCardHandler : HttpMessageHandler
    {
        internal int Requests;
        internal Action? OnRead;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            OnRead?.Invoke();
            AgentCard card = new()
            {
                Name = "Configured remote", Description = "Remote fixture", Version = "1",
                SupportedInterfaces = [new() { Url = RemoteDiscovery + "a2a", ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
                Capabilities = new() { Streaming = true }, Skills = [], DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"],
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(card,
                    (JsonTypeInfo<AgentCard>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentCard)))),
            });
        }
    }
}
