using System.Security.Cryptography;
using System.Text;
using A2A;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Sdk_create_push_after_Sending_starts_callback_observation()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        await using ServiceProvider services = SdkServices(f);

        PushLifecycleDns dns = new(false);

        PushLifecycleHandler physical = new();

        A2APushNotificationRegistry notifications = new(f.Settings, dns, NullLogger<A2APushNotificationRegistry>.Instance);

        A2APushNotificationDispatcher dispatcher = new(new CallbackClientFactory(physical), f.Settings, dns,
            NullLogger<A2APushNotificationDispatcher>.Instance);

        await using ArcanumA2AAgentHandler handler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            NullLogger<ArcanumA2AAgentHandler>.Instance, notifications, dispatcher);

        await using ArcanumA2AServer server = new(handler, new ArcanumA2ATaskStore(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ArcanumA2ATaskStore>.Instance), new(), notifications, NullLogger<A2AServer>.Instance, new());

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        using var principal = services.GetRequiredService<GuildInboundRequestContext>().Enter(peer);

        SendMessageResponse sent = await server.SendMessageAsync(new()
        {
            Message = new() { MessageId = "late-push", Role = Role.User, Parts = [Part.FromText("Compute")] },
            Configuration = new() { ReturnImmediately = true },
        });

        await CompleteInboundPushTaskAsync(f, services.GetRequiredService<GuildInboundHost>(), peer, sent.Task!.Id);

        try
        {
            await server.CreateTaskPushNotificationConfigAsync(new()
            {
                TaskId = sent.Task.Id, ConfigId = "late-config", Config = new() { Url = "https://callback.example.test/hook" },
            });

            await physical.Delivered.Task;
        }
        finally
        {
            dns.Release.TrySetResult();

            await handler.DisposeAsync();
        }

        Assert.Equal(1, physical.Sends);
    }

    [Theory]
    [InlineData("rotate", 1)]
    [InlineData("disable", 0)]
    [InlineData("grant", 0)]
    [InlineData("disable-reenable", 0)]
    [InlineData("epoch", 0)]
    public async Task Sdk_push_observer_rechecks_standing_authority_after_DNS(string change, int expected)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        await using ServiceProvider services = SdkServices(f);

        PushLifecycleDns dns = new(true);

        PushLifecycleHandler physical = new();

        A2APushNotificationRegistry notifications = new(f.Settings, dns, NullLogger<A2APushNotificationRegistry>.Instance);

        A2APushNotificationDispatcher dispatcher = new(new CallbackClientFactory(physical), f.Settings, dns,
            NullLogger<A2APushNotificationDispatcher>.Instance);

        await using ArcanumA2AAgentHandler handler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            NullLogger<ArcanumA2AAgentHandler>.Instance, notifications, dispatcher);

        await using ArcanumA2AServer server = new(handler, new ArcanumA2ATaskStore(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ArcanumA2ATaskStore>.Instance), new(), notifications, NullLogger<A2AServer>.Instance, new());

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        using var principal = services.GetRequiredService<GuildInboundRequestContext>().Enter(peer);

        try
        {
            SendMessageResponse sent = await server.SendMessageAsync(new()
            {
                Message = new() { MessageId = "push-authority", Role = Role.User, Parts = [Part.FromText("Compute")] },
                Configuration = new()
                {
                    ReturnImmediately = true,
                    PushNotificationConfig = new() { Url = "https://callback.example.test/hook" },
                },
            });

            await CompleteInboundPushTaskAsync(f, services.GetRequiredService<GuildInboundHost>(), peer, sent.Task!.Id);

            await dns.Entered.Task;

            if (change == "rotate")
            {
                f.PeerCredentialValue = Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

                ReloadInbound(f, _ => { });

                Assert.Null(f.InboundPeers.Resolve(Fixture.PeerCredential));
            }
            else if (change == "disable" || change == "disable-reenable")
            {
                ReloadInbound(f, configured => configured.Enabled = false);

                if (change == "disable-reenable") ReloadInbound(f, configured => configured.Enabled = true);
            }
            else if (change == "grant") ReloadInbound(f, configured => configured.Grant!.Delegation = []);
            else await f.ExecuteAsync("UPDATE guild_signet_authority_state SET Epoch='AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA'");

            dns.Release.TrySetResult();

            if (expected != 0) await physical.Delivered.Task;
        }
        finally
        {
            dns.Release.TrySetResult();

            await handler.DisposeAsync();
        }

        Assert.Equal(expected, physical.Sends);
    }

    [Fact]
    public async Task Sdk_callback_watch_requires_a_registered_peer_reference_and_host_issued_capability()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        GuildInboundPeer forged = new(peer.PeerId, peer.GrantRevision, peer.Grant, peer.CampaignId,
            peer.SessionId, peer.WorkspaceId, peer.AuthorityVersion);

        Assert.Null(f.InboundPeers.CaptureWatch(forged));

        Assert.Null(f.InboundPeers.ResolveWatch(new()));

        GuildInboundPeerRegistry.Watch watch = Assert.IsType<GuildInboundPeerRegistry.Watch>(f.InboundPeers.CaptureWatch(peer));

        f.PeerCredentialValue = Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        ReloadInbound(f, _ => { });

        GuildInboundPeer current = f.InboundPeers.Resolve(f.PeerCredentialValue)!;

        Assert.Null(f.InboundPeers.CaptureWatch(peer));

        Assert.Same(current, f.InboundPeers.ResolveWatch(watch));

        Assert.Same(watch, f.InboundPeers.CaptureWatch(current));

        ReloadInbound(f, configured => configured.Grant!.Delegation = []);

        ReloadInbound(f, configured => configured.Grant!.Delegation = [GuildDelegationKind.Local]);

        Assert.Null(f.InboundPeers.ResolveWatch(watch));
    }

    private static async Task CompleteInboundPushTaskAsync(Fixture f, GuildInboundHost host, GuildInboundPeer peer, string taskId)
    {
        var current = await host.ReadAsync(peer, taskId, false, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var owner = await f.OperatorAsync();

        var row = await f.Store.GetAsync(owner, current.Value.Quest.Id, default);

        var execution = await ToolExecutionAsync(f, owner, row.Value);

        byte[] body = Encoding.UTF8.GetBytes("Completed");

        string digest = Convert.ToHexString(SHA256.HashData(body));

        Guid result = Guid.NewGuid();

        GuildDataRef content = new(Guid.NewGuid(), GuildContentKind.QuestResult, 1, digest, "text/plain", body.Length, false);

        var completed = await f.Store.CommitResultAsync(execution.Driver, new(result, digest), row.Value.Quest.Revision,
            new(new(row.Value.Quest.Id, result, digest, content, QuestState.Completed, null), body, NoDispatchProven: true,
                WorkStoppedProven: true), default);

        Assert.True(completed.IsSuccess, completed.Error.Message);
    }

    private sealed class PushLifecycleDns(bool blockDelivery) : RetroDownfall.Arcanum.Core.Security.IDnsResolver
    {
        private int _calls;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<System.Net.IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) > 1 && blockDelivery)
            {
                Entered.TrySetResult();

                await Release.Task.WaitAsync(cancellationToken);
            }

            return [System.Net.IPAddress.Parse("93.184.216.34")];
        }
    }

    private sealed class PushLifecycleHandler : HttpMessageHandler
    {
        internal int Sends;

        internal TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Sends);

            Delivered.TrySetResult();

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
