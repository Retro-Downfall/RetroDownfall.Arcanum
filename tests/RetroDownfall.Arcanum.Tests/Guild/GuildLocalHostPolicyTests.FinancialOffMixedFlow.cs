using System.Diagnostics;
using System.Security.Cryptography;
using A2A;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Financial_off_durable_parent_mixed_cohort_survives_authenticated_peer_restart()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        ConfigureRemote(f);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1", ContextWindowLimit = 131072, Models = ["accepted-model"] }];

        f.Settings.CurrentValue.Cost.Budget.Enabled = false;

        f.Settings.CurrentValue.Integrations.A2A.OutboundCredentialEnvironmentVariable = "TASK19_MIXED_PEER_KEY";

        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(3));

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(deadline.Token)).IsSuccess);

        string? previousCredential = global::System.Environment.GetEnvironmentVariable("TASK19_MIXED_PEER_KEY");

        global::System.Environment.SetEnvironmentVariable("TASK19_MIXED_PEER_KEY", Fixture.PeerCredential);

        try
        {
            await using MixedPeer peer = await MixedPeer.StartAsync(deadline.Token);

            GuildCallerContext owner = await f.OperatorAsync();

            var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
                { Tools = [.. CohortTools(), new(GuildToolSourceKind.InternalMcp, "arcanum", "get_agent_task")], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Remote, GuildDelegationKind.Cohort] } });

            Assert.True(root.IsSuccess, root.Error.Message);

            Assert.Equal(GuildBudgetMode.Unlimited, root.Value.Quest.ResolvedGrant.Budget.Usd.Mode);

            Assert.Equal(GuildBudgetMode.Unlimited, root.Value.Quest.ResolvedGrant.Budget.Tokens.Mode);

            var rootRecovery = await f.Policy.CreateRecoveryAsync(owner, root.Value.Quest.Id, root.Value.Quest.OperationId,
                root.Value.Quest.SignetRevision, deadline.Token);

            Assert.True(rootRecovery.IsSuccess, rootRecovery.Error.Message);

            var rootHandle = await f.Authority.RebindAsync(rootRecovery.Value, deadline.Token);

            Assert.True(rootHandle.IsSuccess, rootHandle.Error.Message);

            var rootPending = await f.Store.ReadLocalPendingAsync(owner, root.Value.Quest.Id, deadline.Token);

            Assert.True(rootPending.IsSuccess, rootPending.Error.Message);

            GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store,
                new GuildBudgetLedger(), f.Policy, TimeProvider.System);

            ChildQueue queue = new();

            using RemoteCardHandler discovery = new();

            ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, queue, RemoteResolver(f, discovery), cohorts: cohorts);

            ArcanumSettings settings = new()
            {
                Features = new() { Guild = true }, DefaultModel = "accepted-model",
                Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://example.test/v1", ContextWindowLimit = 131072,
                    GuildUsageReconciliation = GuildUsageReconciliationSource.None, Models = ["accepted-model"] }],
            };

            settings.Cost.Budget.Enabled = false;

            using MixedParentTransport parentTransport = new() { FullParent = true };

            using FinancialOffMemberTransport localTransport = new();

            var parentOpen = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, deadline.Token);

            var localOpen = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, deadline.Token);

            var remoteOpen = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, deadline.Token);

            Assert.True(parentOpen.IsSuccess, parentOpen.Error.Message);

            Assert.True(localOpen.IsSuccess, localOpen.Error.Message);

            Assert.True(remoteOpen.IsSuccess, remoteOpen.Error.Message);

            await using var parentConnection = parentOpen.Value;

            await using var localConnection = localOpen.Value;

            await using var remoteConnection = remoteOpen.Value;

            await using ArcanumDbContext parentDb = GrimoireFixture.CreateContext(parentConnection.Connection);

            await using ArcanumDbContext localDb = GrimoireFixture.CreateContext(localConnection.Connection);

            await using ArcanumDbContext remoteDb = GrimoireFixture.CreateContext(remoteConnection.Connection);

            WizardIntelligenceProviderTests parentWizard = new();

            WizardIntelligenceProviderTests localWizard = new();

            Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildDispatchObservation>>? parentTask = null;

            try
            {
                var parentRuntime = await parentWizard.CreateGuildRuntimeAsync(new BudgetReservationService(parentDb, new TestOptionsMonitor<ArcanumSettings>(settings)), settings,
                    new ChatClientFactory(parentTransport, new TestOptionsMonitor<ArcanumSettings>(settings)), coordinator);

                var localRuntime = await localWizard.CreateGuildRuntimeAsync(new BudgetReservationService(localDb, new TestOptionsMonitor<ArcanumSettings>(settings)), settings,
                    new ChatClientFactory(localTransport, new TestOptionsMonitor<ArcanumSettings>(settings)), coordinator);

                using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

                using ApprenticeService engine = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings, new ChronicleHub(),
                    NullLogger<ApprenticeService>.Instance, f.Gate);

                GuildLocalPort parentPort = new(f.Store, f.Policy, f.Authority, new LongRunningOperationStore(parentDb, f.Connections),
                    engine, parentRuntime.Runtime, queue, new(), TimeProvider.System);

                parentTask = parentPort.DispatchAsync(rootHandle.Value, rootPending.Value.Request!, deadline.Token);

                async Task CompleteQueueWhenParentEndsAsync()
                {
                    try
                    {
                        await parentTask;
                    }
                    catch (Exception)
                    {
                        // The reader reports the actual parent result or exception after the channel drains.
                    }
                    finally
                    {
                        queue.Pending.Writer.TryComplete();
                    }
                }

                Task queueCompletion = CompleteQueueWhenParentEndsAsync();

                async Task<GuildQuestDto> NextMemberAsync()
                {
                    try
                    {
                        return await queue.Pending.Reader.ReadAsync(deadline.Token);
                    }
                    catch (System.Threading.Channels.ChannelClosedException)
                    {
                        var early = await parentTask;

                        Assert.True(early.IsSuccess, early.Error.Message);

                        throw new InvalidOperationException("Parent completed before scheduling both Cohort members: " + early.Value.State);
                    }
                }

                GuildQuestDto first = await NextMemberAsync();

                GuildQuestDto second = await NextMemberAsync();

                await parentTransport.AdmissionsComplete.Task.WaitAsync(deadline.Token);

                var firstPending = await f.Store.ReadLocalPendingAsync(owner, first.Id, deadline.Token);

                var secondPending = await f.Store.ReadLocalPendingAsync(owner, second.Id, deadline.Token);

                Assert.True(firstPending.IsSuccess, firstPending.Error.Message);

                Assert.True(secondPending.IsSuccess, secondPending.Error.Message);

                GuildQuestDto local = !firstPending.Value.IsRemote ? first : second;

                GuildQuestDto remote = local.Id == first.Id ? second : first;

                foreach (GuildQuestDto member in new[] { local, remote })
                {
                    Assert.Equal(GuildBudgetMode.Unlimited, member.ResolvedGrant.Budget.Usd.Mode);

                    Assert.Equal(GuildBudgetMode.Unlimited, member.ResolvedGrant.Budget.Tokens.Mode);
                }

                parentTransport.MemberId = local.Id;

                var localPending = local.Id == first.Id ? firstPending : secondPending;

                var localRecovery = await f.Policy.CreateRecoveryAsync(owner, local.Id, local.OperationId, local.SignetRevision, deadline.Token);

                Assert.True(localRecovery.IsSuccess, localRecovery.Error.Message);

                var localHandle = await f.Authority.RebindAsync(localRecovery.Value, deadline.Token);

                Assert.True(localHandle.IsSuccess, localHandle.Error.Message);

                GuildLocalPort localPort = new(f.Store, f.Policy, f.Authority, new LongRunningOperationStore(localDb, f.Connections),
                    engine, localRuntime.Runtime, queue, new(), TimeProvider.System);

                using MixedRemoteRouting routing = new(peer.Port);

                FakeDnsResolver dns = new();

                dns.Add("configured-remote.example", IPAddress.Parse("93.184.216.34"));

                A2AClientService client = new(new RemotePortHttp(routing), f.Settings, NullLogger<A2AClientService>.Instance, dns);

                GuildRemotePort remotePort = new(f.Store, f.Policy, f.Authority, new LongRunningOperationStore(remoteDb, f.Connections),
                    client, f.Gate, new(), TimeProvider.System);

                var localResult = await localPort.DispatchAsync(localHandle.Value, localPending.Value.Request!, deadline.Token);

                Assert.True(localResult.IsSuccess, localResult.Error.Message);

                var remotePending = remote.Id == first.Id ? firstPending : secondPending;

                var remoteRecovery = await f.Policy.CreateRecoveryAsync(owner, remote.Id, remote.OperationId, remote.SignetRevision, deadline.Token);

                Assert.True(remoteRecovery.IsSuccess, remoteRecovery.Error.Message);

                var remoteHandle = await f.Authority.RebindAsync(remoteRecovery.Value, deadline.Token);

                Assert.True(remoteHandle.IsSuccess, remoteHandle.Error.Message);

                var dispatched = await remotePort.DispatchAsync(remoteHandle.Value, remotePending.Value.Request!, deadline.Token);

                Assert.True(dispatched.IsSuccess, dispatched.Error.Message);

                Assert.NotNull(dispatched.Value.RemoteTaskId);

                Assert.NotNull(dispatched.Value.RemoteContextId);

                var remoteResult = await f.Store.ReadLocalResultAsync(owner, remote.Id, true, deadline.Token);

                Assert.True(remoteResult.IsSuccess, remoteResult.Error.Message);

                Assert.True(remoteResult.Value.Quest.State == QuestState.Completed, $"Remote state={remoteResult.Value.Quest.State} reason={remoteResult.Value.Quest.ReasonCode} peerPosts={peer.ProviderPosts} statuses={string.Join(",", routing.Statuses)}");

                Assert.Equal("remote actual answer", remoteResult.Value.Body);

                GuildPortalStore portal = new(f.Store, f.Authority, f.Policy, TimeProvider.System);

                var drained = await portal.DrainOutboxAsync(owner, root.Value.Quest.Id, deadline.Token);

                Assert.True(drained.IsSuccess, drained.Error.Message);

                Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions WHERE OutcomeCode=1"));

                var draining = await WaitForConvergenceAsync(cohorts, owner, parentTransport.CohortId, value => value.State == GuildCohortState.Draining);

                Assert.Equal(GuildConvergenceDecision.Success, draining.Decision);

                foreach (GuildQuestDto member in new[] { local, remote })
                {
                    var stopped = await f.Store.RefreshCancellationConvergenceAsync(owner, member.Id, deadline.Token);

                    Assert.True(stopped.IsSuccess, stopped.Error.Message);

                    Assert.True(stopped.Value);
                }

                var converged = await WaitForConvergenceAsync(cohorts, owner, parentTransport.CohortId, value => value.State == GuildCohortState.Completed);

                Assert.Equal(GuildConvergenceDecision.Success, converged.Decision);

                parentTransport.Release.TrySetResult();

                var parentResult = await parentTask.WaitAsync(deadline.Token);

                await queueCompletion;

                Assert.True(parentResult.IsSuccess, parentResult.Error.Message);

                Assert.Equal(QuestState.Completed, parentResult.Value.State);

                Assert.True(parentTransport.SawBothAnswers);

                var durableParent = await f.Store.ReadLocalResultAsync(owner, root.Value.Quest.Id, true, deadline.Token);

                Assert.True(durableParent.IsSuccess, durableParent.Error.Message);

                Assert.NotNull(durableParent.Value.Receipt);

                Assert.Equal("both answers received", durableParent.Value.Body);

                Assert.True(converged.Spend.UnknownUsd);

                Assert.True(converged.Spend.UnknownTokens);

                Assert.Equal(2L, await f.ScalarAsync($"SELECT count(*) FROM guild_cohort_members WHERE CohortId='{parentTransport.CohortId:D}' COLLATE NOCASE AND PrimaryVotingQuestId COLLATE NOCASE IN ('{local.Id:D}','{remote.Id:D}')"));

                Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox WHERE SourceKindCode=2"));

                Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

                Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_result_applications"));

                Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox o JOIN guild_inbox i ON i.EnvelopeId=o.EnvelopeId WHERE o.SourceKindCode=2 AND o.DeliveryStateCode=2 AND i.DeliveryStateCode=2"));

                string originFactsBeforeReplay = await ReadMixedOriginFactsAsync(f, deadline.Token);

                int peerPostsBeforeReplay = peer.ProviderPosts;

                int wireCallsBeforeReplay = routing.Statuses.Count;

                var replay = await remotePort.DispatchAsync(remoteHandle.Value, remotePending.Value.Request!, deadline.Token);

                Assert.True(replay.IsFailure);

                Assert.Equal(RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Guild.AuthorityRevoked, replay.Error.Code);

                Assert.Equal(wireCallsBeforeReplay, routing.Statuses.Count);

                Assert.Equal(peerPostsBeforeReplay, peer.ProviderPosts);

                Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions WHERE OutcomeCode=1"));

                Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

                Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox WHERE SourceKindCode=2"));

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

                Assert.Equal(0, parentTransport.MetadataGets);

                Assert.Equal(0, localTransport.MetadataGets);

                Assert.All(peer.GetPaths, path => Assert.Equal("/v1/models", path));

                Assert.True(peer.ProviderPosts > 0);

                string peerTaskId = dispatched.Value.RemoteTaskId!;

                string peerContextId = dispatched.Value.RemoteContextId!;

                int peerPostsBeforeRestart = peer.ProviderPosts;

                async Task<AgentTask> ReadPeerTaskAsync()
                {
                    using HttpClient authenticated = new();

                    authenticated.DefaultRequestHeaders.Add("X-Arcanum-Key", Fixture.PeerCredential);

                    A2AClient sdk = new(new Uri($"http://localhost:{peer.Port}/api/conclave/a2a"), authenticated);

                    return await sdk.GetTaskAsync(new GetTaskRequest { Id = peerTaskId }, deadline.Token);
                }

                AgentTask beforePeerTask = await ReadPeerTaskAsync();

                Assert.Equal(TaskState.Completed, beforePeerTask.Status.State);

                string beforeArtifact = Assert.Single(beforePeerTask.Artifacts!).ArtifactId;

                await peer.StopAsync();

                PeerPersistedSnapshot beforeRestart = await peer.ReadStoppedSnapshotAsync(peerTaskId, deadline.Token);

                Assert.Equal(beforeRestart.Installation, beforeRestart.CurrentInstallation);

                Assert.True(beforeRestart.UnknownEffects > 0);

                Assert.Equal("alpha", beforeRestart.PeerId);

                Assert.Equal(peerTaskId, beforeRestart.TaskId);

                Assert.Equal(peerContextId, beforeRestart.ContextId);

                Assert.Equal(0, beforeRestart.Reservations);

                Assert.Equal(1, beforeRestart.Results);

                await peer.RestartAsync(deadline.Token);

                AgentTask retained = await ReadPeerTaskAsync();

                Assert.Equal(peerTaskId, retained.Id);

                Assert.Equal(peerContextId, retained.ContextId);

                Assert.Equal(TaskState.Completed, retained.Status.State);

                Assert.Equal(beforeArtifact, Assert.Single(retained.Artifacts!).ArtifactId);

                string projected = JsonSerializer.Serialize(retained,
                    (JsonTypeInfo<AgentTask>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentTask)));

                Assert.Contains("remote actual answer", projected, StringComparison.Ordinal);

                Assert.Equal(peerPostsBeforeRestart, peer.ProviderPosts);

                await peer.StopAsync();

                PeerPersistedSnapshot afterRestart = await peer.ReadStoppedSnapshotAsync(peerTaskId, deadline.Token);

                Assert.Equal(beforeRestart, afterRestart);

                GuildLocalHostPolicy restartedPolicy = f.NewPolicy();

                using SignetAuthority restartedAuthority = new(new(f.Connections), f.Gate, restartedPolicy, TimeProvider.System);

                GuildQuestStore restartedStore = new(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority,
                    restartedPolicy, new GuildBudgetLedger(), TimeProvider.System, readiness: f.Readiness);

                var restartedOwner = await restartedPolicy.CreateNativeOperatorAsync(Guid.NewGuid(), deadline.Token);

                Assert.True(restartedOwner.IsSuccess, restartedOwner.Error.Message);

                var retainedParent = await restartedStore.ReadLocalResultAsync(restartedOwner.Value, root.Value.Quest.Id, true, deadline.Token);

                Assert.True(retainedParent.IsSuccess, retainedParent.Error.Message);

                Assert.Equal(durableParent.Value.Receipt!.ResultId, retainedParent.Value.Receipt!.ResultId);

                Assert.Equal("both answers received", retainedParent.Value.Body);

                var restartedRoot = await restartedStore.GetAsync(restartedOwner.Value, root.Value.Quest.Id, deadline.Token);

                Assert.True(restartedRoot.IsSuccess, restartedRoot.Error.Message);

                var restartRecovery = await restartedPolicy.CreateRecoveryAsync(restartedOwner.Value, root.Value.Quest.Id,
                    root.Value.Quest.OperationId, restartedRoot.Value.Quest.SignetRevision, deadline.Token);

                Assert.True(restartRecovery.IsSuccess, restartRecovery.Error.Message);

                var restartedHandle = await restartedAuthority.RebindAsync(restartRecovery.Value, deadline.Token);

                Assert.True(restartedHandle.IsSuccess, restartedHandle.Error.Message);

                var replayOpen = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, deadline.Token);

                Assert.True(replayOpen.IsSuccess, replayOpen.Error.Message);

                await using var replayConnection = replayOpen.Value;

                await using ArcanumDbContext replayDb = GrimoireFixture.CreateContext(replayConnection.Connection);

                GuildLocalPort restartedPort = new(restartedStore, restartedPolicy, restartedAuthority,
                    new LongRunningOperationStore(replayDb, f.Connections), engine, parentRuntime.Runtime, queue, new(), TimeProvider.System);

                int parentPostsBeforeRecovery = parentTransport.Bodies.Count;

                int localPostsBeforeRecovery = localTransport.Posts;

                var recoveredReplay = await restartedPort.DispatchAsync(restartedHandle.Value, rootPending.Value.Request!, deadline.Token);

                Assert.True(recoveredReplay.IsSuccess, recoveredReplay.Error.Message);

                Assert.Equal(QuestState.Completed, recoveredReplay.Value.State);

                Assert.Equal(parentPostsBeforeRecovery, parentTransport.Bodies.Count);

                Assert.Equal(localPostsBeforeRecovery, localTransport.Posts);

                Assert.Equal(peerPostsBeforeRestart, peer.ProviderPosts);

                Assert.Equal(wireCallsBeforeReplay, routing.Statuses.Count);

                Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

                Assert.Equal(3L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox WHERE SourceKindCode=2"));

                Assert.Equal(originFactsBeforeReplay, await ReadMixedOriginFactsAsync(f, deadline.Token));

                await peer.AssertCleanShutdownAsync();
            }
            finally
            {
                deadline.Cancel();

                parentTransport.Release.TrySetResult();

                if (parentTask is not null)
                {
                    try
                    {
                        await parentTask.WaitAsync(TimeSpan.FromSeconds(15));
                    }
                    catch (Exception error)
                    {
                        TestDiagnostics.Report($"Mixed parent task cleanup failed: {error.GetType().Name}: {error.Message}");
                    }
                }

                await parentWizard.DisposeAsync();

                await localWizard.DisposeAsync();
            }
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable("TASK19_MIXED_PEER_KEY", previousCredential);
        }
    }

    private static async Task<string> ReadMixedOriginFactsAsync(Fixture fixture, CancellationToken ct)
    {
        await using SqliteConnection connection = await fixture.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT (SELECT group_concat(value,'|') FROM (SELECT QuestId || ':' || ResultId AS value FROM guild_result_receipts ORDER BY QuestId)),
                (SELECT group_concat(value,'|') FROM (SELECT QuestId || ':' || ReceiptId AS value FROM guild_portal_outbox WHERE SourceKindCode=2 ORDER BY QuestId)),
                (SELECT group_concat(value,'|') FROM (SELECT CohortId || ':' || MemberId || ':' || PrimaryVotingQuestId || ':' || ResultId AS value FROM guild_cohort_contributions ORDER BY MemberId))
            """;

        await using var reader = await command.ExecuteReaderAsync(ct);

        Assert.True(await reader.ReadAsync(ct));

        Assert.False(reader.IsDBNull(0));

        Assert.False(reader.IsDBNull(1));

        Assert.False(reader.IsDBNull(2));

        return string.Join(";", reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private sealed class MixedParentTransport : HttpMessageHandler, IHttpClientFactory
    {
        internal readonly TaskCompletionSource AdmissionsComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly List<string> Bodies = [];

        internal Guid CohortId;

        internal Guid MemberId;

        internal int MetadataGets;

        internal bool FullParent;

        internal bool SawBothAnswers;

        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref MetadataGets);

                throw new InvalidOperationException("Unexpected provider metadata GET");
            }

            string body = await request.Content!.ReadAsStringAsync(ct);

            Bodies.Add(body);

            int ordinal = Bodies.Count - (FullParent ? 1 : 0);

            if (ordinal == 0)
            {
                string planner = """{"id":"mixed-plan","object":"chat.completion","created":1700000000,"model":"accepted-model","choices":[{"index":0,"message":{"role":"assistant","content":"[{\"index\":1,\"description\":\"Coordinate a mixed cohort\"}]"},"finish_reason":"stop"}]}""";

                return new(HttpStatusCode.OK) { Content = new StringContent(planner, Encoding.UTF8, "application/json") };
            }

            if (ordinal == 2)
            {
                using JsonDocument payload = JsonDocument.Parse(body);

                string result = payload.RootElement.GetProperty("messages").EnumerateArray()
                    .Single(message => message.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!;

                var match = System.Text.RegularExpressions.Regex.Match(result, "\\\"id\\\"\\s*:\\s*\\\"([0-9a-fA-F-]{36})\\\"");

                Assert.True(match.Success);

                CohortId = Guid.Parse(match.Groups[1].Value);
            }
            if (ordinal == 5)
            {
                AdmissionsComplete.TrySetResult();

                await Release.Task.WaitAsync(ct);
            }
            string? tool = ordinal switch { 1 => "create_cohort", 2 or 3 => "admit_cohort_member", 4 => "close_cohort", 5 => "get_agent_task", _ => null };

            string arguments = ordinal switch
            {
                1 => "{\"name\":\"Mixed panel\",\"policy\":\"all\",\"resultType\":\"text/plain\"}",
                2 => $"{{\"cohortId\":\"{CohortId:D}\",\"expectedRevision\":1,\"goal\":\"local contribution\"}}",
                3 => $"{{\"cohortId\":\"{CohortId:D}\",\"expectedRevision\":2,\"goal\":\"remote contribution\",\"agent\":\"{RemoteDiscovery}\"}}",
                4 => $"{{\"cohortId\":\"{CohortId:D}\",\"expectedRevision\":3}}",
                5 => $"{{\"questId\":\"{MemberId:D}\"}}",
                _ => "",
            };

            if (ordinal == 5) Assert.Contains(MemberId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);

            string message;

            if (tool is not null)
                message = $$$"""{"role":"assistant","content":null,"tool_calls":[{"id":"call-{{{ordinal}}}","type":"function","function":{"name":"{{{tool}}}","arguments":"{{{JsonEncodedText.Encode(arguments)}}}"}}]}""";
            else
            {
                if (ordinal == 6)
                {
                    Assert.Contains("actual cohort member answer", body, StringComparison.Ordinal);

                    Assert.Contains("remote actual answer", body, StringComparison.Ordinal);

                    SawBothAnswers = true;
                }

                message = """{"role":"assistant","content":"both answers received"}""";
            }
            string json = $$$"""{"id":"mixed-{{{ordinal}}}","object":"chat.completion","created":1700000000,"model":"accepted-model","choices":[{"index":0,"message":{{{message}}},"finish_reason":"{{{(tool is null ? "stop" : "tool_calls")}}}"}]}""";

            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class MixedRemoteRouting(int port) : DelegatingHandler(new HttpClientHandler())
    {
        internal List<int> Statuses { get; } = [];

        private readonly HttpMessageInvoker discovery = new(new RemoteCardHandler());

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return await discovery.SendAsync(request, ct);

            request.RequestUri = new Uri($"http://localhost:{port}/api/conclave/a2a");

            var response = await base.SendAsync(request, ct);

            Statuses.Add((int)response.StatusCode);

            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) discovery.Dispose();

            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData("Unhandled exception. System.IO.IOException: Failed to bind to address http://127.0.0.1:1234: address already in use.", true)]
    [InlineData("System.Net.Sockets.SocketException: Address already in use", false)]
    [InlineData("Failed to bind to address http://127.0.0.1:1234: permission denied.", false)]
    [InlineData("Peer credential unavailable", false)]
    public void Mixed_peer_retries_only_explicit_address_in_use_startup_failure(string output, bool expected)
    {
        Assert.Equal(expected, MixedPeer.IsAddressInUse(output));
    }

    [Fact]
    public async Task Mixed_peer_retries_a_real_occupied_port_after_joining_the_failed_process()
    {
        using TcpListener occupied = new(IPAddress.Loopback, 0);

        occupied.Start();

        int firstPort = ((IPEndPoint)occupied.LocalEndpoint).Port;

        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

        await using MixedPeer peer = await MixedPeer.StartAsync(deadline.Token, firstPort);

        Assert.InRange(peer.StartupAttempts, 2, 5);

        Assert.NotEqual(firstPort, peer.Port);

        await peer.AssertCleanShutdownAsync();
    }

    [Fact]
    public async Task Mixed_peer_cleanup_accepts_an_already_exited_owned_process()
    {
        ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, UseShellExecute = false };

        start.ArgumentList.Add("--version");

        System.Diagnostics.Process child = System.Diagnostics.Process.Start(start)!;

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));

        await child.WaitForExitAsync(deadline.Token);

        await MixedPeer.StopProcessAsync(child);
    }

    private sealed record PeerPersistedSnapshot(string Installation, string CurrentInstallation, string PeerId, string GrantRevision, string AuthorityEpoch,
        string TaskId, string ContextId, string QuestId, string ResultId, long Reservations, long Effects, long UnknownEffects, long Results, long Settlements);

    private sealed class MixedPeer(IHost provider, System.Diagnostics.Process? process, string home, int port, string modelUrl, string masterKey) : IAsyncDisposable
    {
        internal int Port { get; private set; } = port;

        internal int StartupAttempts { get; private set; }

        internal static async Task<MixedPeer> StartAsync(CancellationToken ct, int? firstPort = null)
        {
            int posts = 0;

            int gets = 0;

            System.Collections.Concurrent.ConcurrentQueue<string> getPaths = new();

            IHost? provider = null;

            System.Diagnostics.Process? process = null;

            string root = Path.Combine(Path.GetTempPath(), "arcanum-tests");

            Directory.CreateDirectory(root);

            string home = Path.Combine(root, "mixed-peer-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Path.Combine(home, ".config", "arcanum"));

            try
            {
                provider = await new HostBuilder().ConfigureWebHost(web =>
                {
                    web.UseKestrel().UseUrls("http://127.0.0.1:0");

                    web.Configure(app => app.Run(async context =>
                    {
                        if (context.Request.Method == "GET")
                        {
                            Interlocked.Increment(ref gets);

                            getPaths.Enqueue(context.Request.Path.ToString());

                            if (context.Request.Path == "/v1/models")
                            {
                                context.Response.ContentType = "application/json";

                                await context.Response.WriteAsync("""{"object":"list","data":[{"id":"accepted-model","object":"model"}]}""");
                            }
                            else context.Response.StatusCode = 500;

                            return;
                        }

                        int ordinal = Interlocked.Increment(ref posts);

                        string message = ordinal == 1
                            ? """{"role":"assistant","content":"[{\"index\":1,\"description\":\"Produce remote answer\"}]"}"""
                            : """{"role":"assistant","content":"remote actual answer"}""";

                        context.Response.ContentType = "application/json";

                        await context.Response.WriteAsync($$$"""{"id":"remote-{{{ordinal}}}","object":"chat.completion","created":1700000000,"model":"accepted-model","choices":[{"index":0,"message":{{{message}}},"finish_reason":"stop"}]}""");
                    }));

                }).StartAsync(ct);

                string modelUrl = provider.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

                (process, int port, string key, int attempts) = await StartChildAsync(home, modelUrl, ct, firstPort, null);

                await VerifyEffectiveConfigurationAsync(port, key, ct);

                MixedPeer result = new(provider, process, home, port, modelUrl, key) { StartupAttempts = attempts };

                result.GetCounts = () => (Volatile.Read(ref posts), Volatile.Read(ref gets));

                result.GetPaths = getPaths;

                return result;
            }
            catch
            {
                await CleanupAsync(process, provider, home);

                throw;
            }
        }

        private static async Task VerifyEffectiveConfigurationAsync(int port, string key, CancellationToken ct)
        {
            using HttpClient admin = new();

            admin.DefaultRequestHeaders.Add("X-Arcanum-Key", key);

            using HttpResponseMessage response = await admin.GetAsync($"http://localhost:{port}/api/config", ct);

            response.EnsureSuccessStatusCode();

            using JsonDocument effective = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            JsonElement effectiveConfig = effective.RootElement.GetProperty("data");

            Assert.False(effectiveConfig.GetProperty("cost").GetProperty("budget").GetProperty("enabled").GetBoolean());

            Assert.Equal("None", effectiveConfig.GetProperty("providers")[0].GetProperty("guildUsageReconciliation").GetString(), ignoreCase: true);

            JsonElement peerGrant = effectiveConfig.GetProperty("integrations").GetProperty("a2A").GetProperty("inboundPeers")[0].GetProperty("grant");

            Assert.Equal("unlimited", peerGrant.GetProperty("budget").GetProperty("usd").GetProperty("mode").GetString(), ignoreCase: true);

            Assert.Equal("unlimited", peerGrant.GetProperty("budget").GetProperty("tokens").GetProperty("mode").GetString(), ignoreCase: true);
        }

        internal static bool IsAddressInUse(string output) => output.Split('\n').Any(line =>
            line.Contains("Failed to bind to address http://", StringComparison.OrdinalIgnoreCase)
            && line.Contains(": address already in use.", StringComparison.OrdinalIgnoreCase));

        private static async Task<(System.Diagnostics.Process Process, int Port, string Key, int Attempts)> StartChildAsync(
            string home, string modelUrl, CancellationToken ct, int? firstPort, string? existingMasterKey)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                using TcpListener reservation = new(IPAddress.Loopback, 0);

                reservation.Start();

                int port = attempt == 0 && firstPort is { } selected ? selected : ((IPEndPoint)reservation.LocalEndpoint).Port;

                reservation.Stop();

                string config = """{"Arcanum":{"Host":{"Port":PORT},"DefaultModel":"accepted-model","Providers":[{"Name":"accepted-provider","Type":"OpenAICompatible","Endpoint":"MODEL_URL/v1","ContextWindowLimit":131072,"Models":["accepted-model"],"GuildUsageReconciliation":"None"}],"Features":{"Guild":true,"Apprentices":true,"Conclave":true,"A2AServer":true},"Cost":{"Budget":{"Enabled":false}},"Integrations":{"A2A":{"InboundPeers":[{"PeerId":"alpha","Enabled":true,"CredentialEnvironmentVariable":"TASK19_CHILD_PEER","CampaignId":null,"Grant":{"Workspaces":[],"Tools":[],"Memory":[],"Budget":{"Usd":{"Mode":"unlimited"},"Tokens":{"Mode":"unlimited"}},"Delegation":["local"],"ExpiresAtUtc":null}}]}}}}"""
                    .Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("MODEL_URL", modelUrl, StringComparison.Ordinal);

                await File.WriteAllTextAsync(Path.Combine(home, ".config", "arcanum", "arcanum.json"), config, ct);

                ProcessStartInfo start = new("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };

                start.ArgumentList.Add(typeof(global::Program).Assembly.Location);

                start.Environment["DOTNET_ENVIRONMENT"] = "Testing";

                start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";

                start.Environment["ARCANUM_TEST_HOME"] = home;

                start.Environment["ARCANUM_TEST_IN_MEMORY_CREDENTIALS"] = "1";

                start.Environment["TASK19_CHILD_PEER"] = Fixture.PeerCredential;

                System.Diagnostics.Process child = System.Diagnostics.Process.Start(start)
                    ?? throw new InvalidOperationException("Peer process failed to start.");

                bool handedOff = false;

                try
                {
                    Task<string?> issuedKey = existingMasterKey is null
                        ? child.StandardOutput.ReadLineAsync(ct).AsTask() : Task.FromResult<string?>(existingMasterKey);

                    StringBuilder stderr = new();

                    while (true)
                    {
                        string? line = await child.StandardError.ReadLineAsync(ct);

                        if (line is null) break;

                        stderr.AppendLine(line);

                        if (line.Contains("Arcanum DevHost listening on", StringComparison.Ordinal))
                        {
                            string key = await issuedKey.WaitAsync(ct)
                                ?? throw new InvalidOperationException("Peer did not publish its isolated test key.");

                            _ = child.StandardError.ReadToEndAsync();

                            _ = child.StandardOutput.ReadToEndAsync();

                            handedOff = true;

                            return (child, port, key, attempt + 1);
                        }
                    }
                    await child.WaitForExitAsync(ct);

                    if (!IsAddressInUse(stderr.ToString()) || attempt == 4)
                        throw new InvalidOperationException("Peer exited before readiness: " + stderr);
                }
                finally
                {
                    if (!handedOff)
                    {
                        try
                        {
                            await StopProcessAsync(child);
                        }
                        catch (Exception error)
                        {
                            TestDiagnostics.Report($"Mixed startup process cleanup failed: {error.GetType().Name}: {error.Message}");
                        }
                    }
                }
                // A failed bind may have bootstrapped keys before Kestrel failed. Retry with a fresh
                // owned profile so the next successful process can expose its newly generated key.
                if (existingMasterKey is null)
                {
                    Directory.Delete(home, true);

                    Directory.CreateDirectory(Path.Combine(home, ".config", "arcanum"));
                }
            }
            throw new InvalidOperationException("Peer address retry limit was exhausted.");
        }

        internal static async Task StopProcessAsync(System.Diagnostics.Process process)
        {
            try
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process exited between the status check and Kill.
                }
                catch (System.ComponentModel.Win32Exception) when (process.HasExited)
                {
                    // The operating system reaped the process between those operations.
                }
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                process.Dispose();
            }
        }

        private static async Task<List<Exception>> CleanupAsync(System.Diagnostics.Process? process, IHost? provider, string home)
        {
            List<Exception> failures = [];

            try
            {
                if (process is not null) await StopProcessAsync(process);
            }
            catch (Exception error)
            {
                failures.Add(error);

                TestDiagnostics.Report($"Mixed peer process cleanup failed: {error.GetType().Name}: {error.Message}");
            }
            finally
            {
                try
                {
                    if (provider is not null) await provider.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (Exception error)
                {
                    failures.Add(error);

                    TestDiagnostics.Report($"Mixed provider stop failed: {error.GetType().Name}: {error.Message}");
                }
                finally
                {
                    try
                    {
                        provider?.Dispose();
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);

                        TestDiagnostics.Report($"Mixed provider disposal failed: {error.GetType().Name}: {error.Message}");
                    }
                    finally
                    {
                        try
                        {
                            if (Directory.Exists(home)) Directory.Delete(home, true);
                        }
                        catch (Exception error)
                        {
                            failures.Add(error);

                            TestDiagnostics.Report($"Mixed peer profile cleanup failed: {error.GetType().Name}: {error.Message}");
                        }
                    }
                }
            }

            return failures;
        }

        internal async Task StopAsync()
        {
            System.Diagnostics.Process stopped = Assert.IsType<System.Diagnostics.Process>(process);

            process = null!;

            await StopProcessAsync(stopped);
        }

        internal async Task RestartAsync(CancellationToken ct)
        {
            Assert.Null(process);

            (process, int restartedPort, _, _) = await StartChildAsync(home, modelUrl, ct, null, masterKey);

            Port = restartedPort;

            await VerifyEffectiveConfigurationAsync(Port, masterKey, ct);
        }

        internal async Task<PeerPersistedSnapshot> ReadStoppedSnapshotAsync(string taskId, CancellationToken ct)
        {
            Assert.Null(process);

            string profile = Path.Combine(home, ".config", "arcanum");

            IDataProtectionProvider protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(profile, "keys")),
                builder => builder.SetApplicationName("ArcanumCore"));

            var secret = await ProtectedCredentialFile.ReadAsync(Path.Combine(profile, "grimoire-key.dat"),
                protection.CreateProtector("Arcanum.Core.GrimoireEncryption"), "Test peer secret unavailable.", ct);

            Assert.Equal(SecretStoreReadStatus.Ok, secret.Status);

            string database = Path.Combine(profile, "arcanum.db");

            GrimoireKdfSidecar sidecar = GrimoireKdfSidecarFile.Read(database);

            Assert.Equal(GrimoireKeyDerivation.KdfVersion2, sidecar.Version);

            byte[] salt = sidecar.GetSaltBytes();

            string passphrase;

            try
            {
                passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(secret.Value!, salt);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
            }

            SqliteNativeRuntime.Instance.Initialize();

            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, Password = passphrase,
            }.ToString());

            await connection.OpenAsync(ct);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = """
                SELECT q.InstallationGeneration,(SELECT InstallationIdentity FROM covenant_authority_state WHERE StateKey=1),
                    i.PeerId,i.GrantRevision,i.AuthorityEpoch,i.TaskId,i.ContextId,i.QuestId,r.ResultId,
                    (SELECT count(*) FROM BudgetReservations), (SELECT count(*) FROM guild_budget_effects),
                    (SELECT count(*) FROM guild_budget_effects WHERE UnknownUsd=1 AND UnknownTokens=1 AND StateCode=2),
                    (SELECT count(*) FROM guild_result_receipts), (SELECT count(*) FROM guild_portal_outbox WHERE SourceKindCode=2)
                FROM guild_inbound_tasks i JOIN guild_quests q ON q.Id=i.QuestId
                JOIN guild_result_receipts r ON r.QuestId=q.Id WHERE i.TaskId=$task
                """;

            command.Parameters.AddWithValue("$task", taskId);

            await using var reader = await command.ExecuteReaderAsync(ct);

            Assert.True(await reader.ReadAsync(ct));

            PeerPersistedSnapshot snapshot = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetInt64(9),
                reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13));

            Assert.False(await reader.ReadAsync(ct));

            return snapshot;
        }

        private bool _cleaned;

        internal async Task AssertCleanShutdownAsync()
        {
            _cleaned = true;

            List<Exception> failures = await CleanupAsync(process, provider, home);

            if (failures.Count != 0) throw new AggregateException("Mixed peer cleanup failed.", failures);
        }

        private Func<(int Posts, int Gets)> GetCounts = null!;

        internal int ProviderPosts => GetCounts().Posts;

        internal int ProviderGets => GetCounts().Gets;

        internal System.Collections.Concurrent.ConcurrentQueue<string> GetPaths = new();

        public async ValueTask DisposeAsync()
        {
            if (!_cleaned)
            {
                _cleaned = true;

                _ = await CleanupAsync(process, provider, home);
            }
        }
    }
}
