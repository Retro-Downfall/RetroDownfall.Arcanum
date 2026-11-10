using A2A;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Sdk_task_store_checks_peer_binding_before_serving_a_cached_legacy_task()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        await using ServiceProvider services = SdkServices(f);

        ArcanumA2ATaskStore store = new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ArcanumA2ATaskStore>.Instance);

        await store.SaveTaskAsync("other-task", new AgentTask
        {
            Id = "other-task", ContextId = "private-context", Status = new() { State = TaskState.Completed },
            Artifacts = [new Artifact { ArtifactId = "private", Parts = [Part.FromText("private body")] }],
        });

        Assert.Null(await store.GetTaskAsync("other-task"));

        Assert.Empty((await store.ListTasksAsync(new())).Tasks);
    }

    [Fact]
    public async Task Sdk_task_store_rehydrates_only_current_peer_owned_durable_tasks_after_restart()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        await using ServiceProvider services = SdkServices(f);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        var accepted = await services.GetRequiredService<GuildInboundHost>().IssueAsync(peer, "message", "peer-task", null, "Compute", default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        ArcanumA2ATaskStore restarted = new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ArcanumA2ATaskStore>.Instance);

        Assert.Null(await restarted.GetTaskAsync("peer-task"));

        using (services.GetRequiredService<GuildInboundRequestContext>().Enter(peer))
        {
            AgentTask recovered = Assert.IsType<AgentTask>(await restarted.GetTaskAsync("peer-task"));

            Assert.Equal("peer-task", recovered.Id);

            Assert.Equal(accepted.Value.Binding.ContextId, recovered.ContextId);

            Assert.Single((await restarted.ListTasksAsync(new())).Tasks);

            ReloadInbound(f, configured => configured.Enabled = false);

            Assert.Null(await restarted.GetTaskAsync("peer-task"));

            Assert.Empty((await restarted.ListTasksAsync(new())).Tasks);
        }
    }

    [Fact]
    public async Task Sdk_handler_issues_a_canonical_quest_instead_of_rejecting_a_verified_peer()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        await using ServiceProvider services = SdkServices(f);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        using var principal = services.GetRequiredService<GuildInboundRequestContext>().Enter(peer);

        await using ArcanumA2AAgentHandler handler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            NullLogger<ArcanumA2AAgentHandler>.Instance);

        AgentEventQueue queue = new();

        await handler.ExecuteAsync(new RequestContext
        {
            TaskId = "issued-task", ContextId = "context", StreamingResponse = false, Configuration = new() { ReturnImmediately = true },
            Message = new() { MessageId = "request", Role = Role.User, Parts = [Part.FromText("Compute")] },
        }, queue, default);

        queue.Complete();

        List<StreamResponse> frames = [];

        await foreach (StreamResponse frame in queue) frames.Add(frame);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_inbound_tasks"));

        Assert.DoesNotContain(frames, frame => frame.StatusUpdate?.Status.State == TaskState.Rejected);
    }

    [Fact]
    public async Task Sdk_push_list_and_delete_cannot_operate_without_a_peer_task_binding()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        await using ServiceProvider services = SdkServices(f);

        ArcanumA2ATaskStore store = new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ArcanumA2ATaskStore>.Instance);

        A2APushNotificationRegistry notifications = new(f.Settings, new RetroDownfall.Arcanum.Infrastructure.Security.SystemDnsResolver(), NullLogger<A2APushNotificationRegistry>.Instance);

        await using ArcanumA2AAgentHandler handler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            NullLogger<ArcanumA2AAgentHandler>.Instance);

        await using ArcanumA2AServer server = new(handler, store, new(), notifications,
            NullLogger<A2AServer>.Instance, new());

        await Assert.ThrowsAsync<A2AException>(() => server.ListTaskPushNotificationConfigAsync(new() { TaskId = "unowned" }));

        await Assert.ThrowsAsync<A2AException>(() => server.DeleteTaskPushNotificationConfigAsync(new() { TaskId = "unowned", Id = "callback" }));
    }

    [Fact]
    public async Task Sdk_stream_captures_verified_peer_and_stops_before_disclosure_after_revocation()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        await using ServiceProvider services = SdkServices(f);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        var accepted = await services.GetRequiredService<GuildInboundHost>().IssueAsync(peer, "message", "stream-task", null, "Compute", default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        ArcanumA2ATaskStore store = new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ArcanumA2ATaskStore>.Instance);

        A2APushNotificationRegistry notifications = new(f.Settings, new RetroDownfall.Arcanum.Infrastructure.Security.SystemDnsResolver(), NullLogger<A2APushNotificationRegistry>.Instance);

        await using ArcanumA2AAgentHandler handler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            NullLogger<ArcanumA2AAgentHandler>.Instance);

        await using ArcanumA2AServer server = new(handler, store, new(), notifications, NullLogger<A2AServer>.Instance, new());

        IAsyncEnumerable<StreamResponse> stream;

        using (services.GetRequiredService<GuildInboundRequestContext>().Enter(peer))
        {
            stream = server.SubscribeToTaskAsync(new() { Id = "stream-task" });
        }

        await using IAsyncEnumerator<StreamResponse> reader = stream.GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());

        Assert.Equal("stream-task", reader.Current.Task?.Id);

        ReloadInbound(f, configured => configured.Enabled = false);

        await Assert.ThrowsAsync<A2AException>(async () => await reader.MoveNextAsync());
    }

    [Fact]
    public async Task Sdk_duplicate_new_message_returns_canonical_task_without_a_second_quest()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        await using ServiceProvider services = SdkServices(f);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        using var principal = services.GetRequiredService<GuildInboundRequestContext>().Enter(peer);

        ArcanumA2ATaskStore store = new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ArcanumA2ATaskStore>.Instance);

        A2APushNotificationRegistry notifications = new(f.Settings, new RetroDownfall.Arcanum.Infrastructure.Security.SystemDnsResolver(), NullLogger<A2APushNotificationRegistry>.Instance);

        await using ArcanumA2AAgentHandler handler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            NullLogger<ArcanumA2AAgentHandler>.Instance);

        await using ArcanumA2AServer server = new(handler, store, new(), notifications, NullLogger<A2AServer>.Instance, new());

        SendMessageRequest Request() => new()
        {
            Message = new() { MessageId = "stable-message", Role = Role.User, Parts = [Part.FromText("Compute")] },
            Configuration = new() { ReturnImmediately = true },
        };

        SendMessageResponse first = await server.SendMessageAsync(Request());

        SendMessageResponse replay = await server.SendMessageAsync(Request());

        Assert.NotNull(first.Task);

        Assert.Equal(first.Task.Id, replay.Task?.Id);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_inbound_tasks"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));
    }

    [Fact]
    public async Task Sdk_Guild_task_never_becomes_a_legacy_cache_entry_when_feature_is_disabled()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        await using ServiceProvider services = SdkServices(f);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        var accepted = await services.GetRequiredService<GuildInboundHost>().IssueAsync(peer, "message", "owned-task", null, "Compute", default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        ArcanumA2ATaskStore store = new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ArcanumA2ATaskStore>.Instance);

        await store.SaveTaskAsync("owned-task", new AgentTask { Id = "owned-task", ContextId = "secret", Status = new() { State = TaskState.Completed } });

        f.Settings.CurrentValue.Features.Guild = false;

        Assert.Null(await store.GetTaskAsync("owned-task"));

        Assert.Empty((await store.ListTasksAsync(new())).Tasks);
    }

    [Fact]
    public async Task Sdk_three_hop_cycle_keeps_received_chain_in_checkpoint_and_onward_dispatch()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        await using ServiceProvider services = SdkServices(f);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        using var principal = services.GetRequiredService<GuildInboundRequestContext>().Enter(peer);

        await using ArcanumA2AAgentHandler handler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            NullLogger<ArcanumA2AAgentHandler>.Instance);

        Message message = new() { MessageId = "upstream", Role = Role.User, Parts = [Part.FromText("Delegate onward")] };

        ConclaveDelegationChain.Write(message, ["origin-node-a"]);

        AgentEventQueue firstQueue = new();

        await handler.ExecuteAsync(new RequestContext
        {
            TaskId = "middle-hop", ContextId = "context", Message = message,
            Configuration = new() { ReturnImmediately = true }, StreamingResponse = false,
        }, firstQueue, default);

        var opened = await f.Connections.OpenFreshAsync(
            RetroDownfall.Arcanum.Infrastructure.Data.GrimoireOrdinaryFreshConnectionKind.ReadOnly, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        Guid apprentice;

        IReadOnlyList<string> inherited;

        await using (var lease = opened.Value)
        {
            await using var command = lease.Connection.CreateCommand();

            command.CommandText = "SELECT Id,CheckpointData FROM Apprentices";

            await using var reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            apprentice = Guid.Parse(reader.GetString(0));

            inherited = RetroDownfall.Arcanum.Infrastructure.Repositories.ApprenticeRepository
                .DeserializeCheckpoint(reader.GetString(1))?.DelegationChain ?? [];
        }

        Assert.Equal(["origin-node-a", ConclaveDelegationChain.NodeId], inherited);

        IReadOnlyList<string>? forwarded = await RetroDownfall.Arcanum.Tests.A2A.A2ADelegationCycleTests
            .DispatchFromApprenticeAsync(apprentice, inherited);

        Assert.Equal(["origin-node-a", ConclaveDelegationChain.NodeId], forwarded);

        Message repeated = new() { MessageId = "cycled", Role = Role.User, Parts = [Part.FromText("Cycled work")] };

        ConclaveDelegationChain.Write(repeated, [.. forwarded!, "downstream-node-c"]);

        AgentEventQueue refusedQueue = new();

        await handler.ExecuteAsync(new RequestContext
        {
            TaskId = "repeated-hop", ContextId = "context", Message = repeated,
            Configuration = new() { ReturnImmediately = true }, StreamingResponse = false,
        }, refusedQueue, default);

        refusedQueue.Complete();

        List<StreamResponse> refused = [];

        await foreach (StreamResponse frame in refusedQueue) refused.Add(frame);

        Assert.Contains(refused, frame => frame.StatusUpdate?.Status.State == TaskState.Rejected);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sdk_callback_final_handoff_rechecks_peer_after_DNS(bool revoke)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        await using ServiceProvider services = SdkServices(f);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        GuildInboundHost host = services.GetRequiredService<GuildInboundHost>();

        var accepted = await host.IssueAsync(peer, "message", "callback-task", null, "Compute", default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        CallbackBlockedDns dns = new();

        CallbackCountingHandler physical = new();

        A2APushNotificationDispatcher dispatcher = new(new CallbackClientFactory(physical), f.Settings, dns,
            NullLogger<A2APushNotificationDispatcher>.Instance);

        Task delivery = dispatcher.NotifyAsync(new() { Url = "https://callback.example.test/hook" }, "callback-task", "working", default,
            (send, ct) => host.AdmitDisclosureAsync(peer, "callback-task", send, ct));

        await dns.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        if (revoke) ReloadInbound(f, configured => configured.Enabled = false);

        dns.Release.TrySetResult();

        await delivery;

        Assert.Equal(revoke ? 0 : 1, physical.Sends);
    }

    private sealed class CallbackBlockedDns : RetroDownfall.Arcanum.Core.Security.IDnsResolver
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<System.Net.IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();

            await Release.Task.WaitAsync(cancellationToken);

            return [System.Net.IPAddress.Parse("93.184.216.34")];
        }
    }

    private sealed class CallbackCountingHandler : HttpMessageHandler
    {
        internal int Sends;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Sends);

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class CallbackClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task Sdk_rejected_unverified_handler_cannot_bypass_Guild_callback_admission()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        await using ServiceProvider services = SdkServices(f);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        var accepted = await services.GetRequiredService<GuildInboundHost>().IssueAsync(peer, "message", "owned-task", null, "Compute", default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        CallbackBlockedDns dns = new();

        dns.Release.TrySetResult();

        A2APushNotificationRegistry notifications = new(f.Settings, dns, NullLogger<A2APushNotificationRegistry>.Instance);

        Assert.True((await notifications.RegisterAsync("owned-task", new() { Url = "https://callback.example.test/hook" })).IsSuccess);

        CallbackCountingHandler physical = new();

        A2APushNotificationDispatcher dispatcher = new(new CallbackClientFactory(physical), f.Settings, dns,
            NullLogger<A2APushNotificationDispatcher>.Instance);

        await using ArcanumA2AAgentHandler handler = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
            NullLogger<ArcanumA2AAgentHandler>.Instance, notifications, dispatcher);

        await handler.ExecuteAsync(new RequestContext
        {
            TaskId = "owned-task", ContextId = "context", StreamingResponse = false,
            Message = new() { MessageId = "forged", Role = Role.User, Parts = [Part.FromText("No authenticated principal")] },
        }, new AgentEventQueue(), default);

        Assert.Equal(0, physical.Sends);
    }

    private static ServiceProvider SdkServices(Fixture f)
    {
        f.Settings.CurrentValue.Features.A2AServer = true;

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Integrations.A2A.PushNotifications = true;

        ServiceCollection services = new();

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(f.Settings);

        services.AddSingleton(f.InboundPeers!);

        services.AddSingleton<GuildInboundRequestContext>();

        services.AddSingleton(new GuildInboundHost(f.Policy, f.Store, new ConclaveArchmage(null!, f.Settings, f.Store, new InboundScheduler())));

        return services.BuildServiceProvider();
    }
}
