using System.Net;
using System.Security.Cryptography;
using System.Text;
using A2A;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("get", false)]
    [InlineData("get", true)]
    [InlineData("continue", false)]
    [InlineData("continue", true)]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    [InlineData("subscribe", false)]
    [InlineData("subscribe", true)]
    [InlineData("push-set", false)]
    [InlineData("push-set", true)]
    [InlineData("push-get", false)]
    [InlineData("push-get", true)]
    [InlineData("push-list", false)]
    [InlineData("push-list", true)]
    [InlineData("push-delete", false)]
    [InlineData("push-delete", true)]
    public async Task Sdk_two_peer_task_guessing_matches_absence_before_and_after_restart(string operation, bool restart)
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Settings.CurrentValue.Features.A2AServer = true;

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Integrations.A2A.PushNotifications = true;

        f.Settings.CurrentValue.Integrations.A2A.InboundPeers =
            [IsolationPeer("alpha", "ISOLATION_ALPHA"), IsolationPeer("beta", "ISOLATION_BETA")];

        await using PeerIsolationRuntime initial = await PeerIsolationRuntime.CreateAsync(f);

        var first = await initial.Host.IssueAsync(initial.Alpha, "alpha-message", "alpha-private-task", "alpha-private-context", "Alpha private goal", default);

        var second = await initial.Host.IssueAsync(initial.Beta, "beta-message", "beta-own-task", "beta-own-context", "Beta own goal", default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.True(second.IsSuccess, second.Error.Message);

        if (restart)
        {
            await initial.DisposeAsync();

            await using PeerIsolationRuntime restarted = await PeerIsolationRuntime.CreateAsync(f);

            await AssertPeerIsolationAsync(f, restarted, operation);
        }
        else
        {
            await AssertPeerIsolationAsync(f, initial, operation);
        }
    }

    private static async Task AssertPeerIsolationAsync(Fixture f, PeerIsolationRuntime runtime, string operation)
    {
        const string known = "alpha-private-task";

        // This proves authority, not throughput. The suite watchdog bounds hangs without
        // injecting runner-speed cancellation into the authenticated database operations.

        using (runtime.Request.Enter(runtime.Alpha))
        {
            Assert.Equal(known, (await runtime.Server.GetTaskAsync(new() { Id = known }, CancellationToken.None)).Id);

            await using var stream = runtime.Server.SubscribeToTaskAsync(new() { Id = known }, CancellationToken.None).GetAsyncEnumerator(CancellationToken.None);

            Assert.True(await stream.MoveNextAsync());

            Assert.Equal(known, stream.Current.Task?.Id);
        }

        Assert.True((await runtime.Notifications.RegisterAsync(known,
            new() { Id = "private-config", Url = "https://callback.example.test/private", Token = "private-token" }, CancellationToken.None)).IsSuccess);

        using (runtime.Request.Enter(runtime.Alpha))
        {
            Assert.Equal("private-config", (await runtime.Server.GetTaskPushNotificationConfigAsync(new()
                { TaskId = known, Id = "private-config" }, CancellationToken.None)).Id);

            Assert.Single((await runtime.Server.ListTaskPushNotificationConfigAsync(new() { TaskId = known }, CancellationToken.None)).Configs!);
        }

        int resolutions = runtime.Dns.Requests;

        using (runtime.Request.Enter(runtime.Beta))
        {
            Assert.Equal("beta-own-task", (await runtime.Server.GetTaskAsync(new() { Id = "beta-own-task" }, CancellationToken.None)).Id);

            Assert.Equal("beta-own-task", Assert.Single((await runtime.Server.ListTasksAsync(new(), CancellationToken.None)).Tasks).Id);

            A2AException foreign = await Assert.ThrowsAsync<A2AException>(() => ProbePeerTaskAsync(runtime.Server, operation, known, CancellationToken.None));

            A2AException absent = await Assert.ThrowsAsync<A2AException>(() => ProbePeerTaskAsync(runtime.Server, operation, "absent-task", CancellationToken.None));

            Assert.Equal(absent.ErrorCode, foreign.ErrorCode);

            Assert.Equal(absent.Message, foreign.Message);

            Assert.DoesNotContain("alpha-private", foreign.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("private-token", foreign.Message, StringComparison.Ordinal);
        }

        Assert.Equal(resolutions, runtime.Dns.Requests);

        Assert.Equal("private-config", runtime.Notifications.Resolve(known)?.Id);

        Assert.Equal("private-token", runtime.Notifications.Resolve(known)?.Token);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_continuations"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_quest_cancellations"));
    }

    private static async Task ProbePeerTaskAsync(ArcanumA2AServer server, string operation, string task, CancellationToken ct)
    {
        switch (operation)
        {
            case "get":
                _ = await server.GetTaskAsync(new() { Id = task }, ct);

                break;

            case "continue":
                _ = await server.SendMessageAsync(new()
                {
                    Message = new() { TaskId = task, MessageId = "probe-continuation", Role = Role.User, Parts = [Part.FromText("Foreign answer")] },
                    Configuration = new() { ReturnImmediately = true },
                }, ct);

                break;

            case "cancel":
                _ = await server.CancelTaskAsync(new() { Id = task }, ct);

                break;

            case "subscribe":
                await using (var reader = server.SubscribeToTaskAsync(new() { Id = task }, ct).GetAsyncEnumerator(ct))
                {
                    _ = await reader.MoveNextAsync();
                }

                break;

            case "push-set":
                _ = await server.CreateTaskPushNotificationConfigAsync(new()
                    { TaskId = task, ConfigId = "attacker", Config = new() { Url = "https://callback.example.test/attacker" } }, ct);

                break;

            case "push-get":
                _ = await server.GetTaskPushNotificationConfigAsync(new() { TaskId = task, Id = "private-config" }, ct);

                break;

            case "push-list":
                _ = await server.ListTaskPushNotificationConfigAsync(new() { TaskId = task }, ct);

                break;

            case "push-delete":
                await server.DeleteTaskPushNotificationConfigAsync(new() { TaskId = task, Id = "private-config" }, ct);

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static A2AInboundPeerSettings IsolationPeer(string id, string environment) => new()
    {
        PeerId = id, Enabled = true, CredentialEnvironmentVariable = environment, CampaignId = null,
        Grant = new() { Workspaces = [], Tools = [], Memory = [], Budget = new()
            { Usd = new() { Mode = GuildBudgetMode.Unlimited }, Tokens = new() { Mode = GuildBudgetMode.Unlimited } },
            Delegation = [GuildDelegationKind.Local], ExpiresAtUtc = null },
    };

    private sealed class PeerIsolationAdmission : IGuildLocalExecutionAdmission
    {
        public Task<Result<GuildLocalExecutionInput>> ResolveWithinAsync(GuildCallerContext caller, GuildQuestStored quest,
            SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct) =>
            Task.FromResult(Result<GuildLocalExecutionInput>.Success(new(1, GuildLocalExecutionMode.Apprentice, quest.Quest.GoalRef,
                "accepted-provider", "accepted-model", [], null)));
    }

    private sealed class PeerIsolationDns : IDnsResolver
    {
        internal int Requests;

        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
        {
            Requests++;

            return Task.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34")]);
        }
    }

    private sealed class PeerIsolationRuntime : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private readonly SignetAuthority _authority;

        private readonly GuildInboundPeerRegistry _peers;

        private readonly ArcanumA2AAgentHandler _handler;

        private bool _disposed;

        internal GuildInboundHost Host { get; }

        internal GuildInboundPeer Alpha { get; }

        internal GuildInboundPeer Beta { get; }

        internal GuildInboundRequestContext Request => _services.GetRequiredService<GuildInboundRequestContext>();

        internal ArcanumA2AServer Server { get; }

        internal PeerIsolationDns Dns { get; } = new();

        internal A2APushNotificationRegistry Notifications { get; }

        private PeerIsolationRuntime(Fixture f, GuildInboundPeerRegistry peers, GuildLocalHostPolicy policy)
        {
            _peers = peers;

            Alpha = Assert.IsType<GuildInboundPeer>(peers.Resolve(IsolationAlphaCredential));

            Beta = Assert.IsType<GuildInboundPeer>(peers.Resolve(IsolationBetaCredential));

            _authority = new(new(f.Connections), f.Gate, policy, TimeProvider.System);

            GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), _authority, policy,
                new GuildBudgetLedger(), TimeProvider.System, new PeerIsolationAdmission(), f.Readiness);

            Host = new(policy, store, new ConclaveArchmage(null!, f.Settings, store, new InboundScheduler()));

            ServiceCollection services = new();

            services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(f.Settings);

            services.AddSingleton(peers);

            services.AddSingleton<GuildInboundRequestContext>();

            services.AddSingleton(Host);

            _services = services.BuildServiceProvider();

            var scopes = _services.GetRequiredService<IServiceScopeFactory>();

            Notifications = new(f.Settings, Dns, NullLogger<A2APushNotificationRegistry>.Instance);

            _handler = new(scopes, f.Settings, NullLogger<ArcanumA2AAgentHandler>.Instance);

            Server = new(_handler, new ArcanumA2ATaskStore(scopes, NullLogger<ArcanumA2ATaskStore>.Instance), new(),
                Notifications, NullLogger<A2AServer>.Instance, new(), scopes, f.Settings);
        }

        private const string IsolationAlphaCredential = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        private static readonly string IsolationBetaCredential = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static async Task<PeerIsolationRuntime> CreateAsync(Fixture f)
        {
            ApiKeyDigestCache cache = new(TimeProvider.System);

            cache.StoreDigest(SHA256.HashData(Encoding.UTF8.GetBytes("isolation-master")), 300);

            GuildInboundPeerRegistry peers = new(f.Settings, cache,
                name => name == "ISOLATION_ALPHA" ? IsolationAlphaCredential : name == "ISOLATION_BETA" ? IsolationBetaCredential : null);

            GuildLocalHostPolicy policy = new(f.Connections, f.Gate, f.Settings, f.Opener, TimeProvider.System, peers);

            Assert.True((await policy.InitializeIdentityAsync(default)).IsSuccess);

            Assert.True((await policy.ReconcileInboundPeersAsync(default)).IsSuccess);

            return new(f, peers, policy);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            _disposed = true;

            await Server.DisposeAsync();

            await _handler.DisposeAsync();

            await _services.DisposeAsync();

            _authority.Dispose();

            _peers.Dispose();
        }
    }
}
