using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
    public async Task Financial_off_model_cohort_contains_actual_local_and_authenticated_inbound_remote_members()
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

            GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, root.Value);

            var input = await f.Store.ReadLocalInputAsync(execution.Driver, deadline.Token);

            Assert.True(input.IsSuccess, input.Error.Message);

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

            using MixedParentTransport parentTransport = new();

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

            Task<RetroDownfall.Arcanum.Core.Primitives.Result<PromptTurnResult>>? parentTask = null;

            try
            {
                var parentRuntime = await parentWizard.CreateGuildRuntimeAsync(new BudgetReservationService(parentDb, new TestOptionsMonitor<ArcanumSettings>(settings)), settings,
                    new ChatClientFactory(parentTransport, new TestOptionsMonitor<ArcanumSettings>(settings)), coordinator);

                var localRuntime = await localWizard.CreateGuildRuntimeAsync(new BudgetReservationService(localDb, new TestOptionsMonitor<ArcanumSettings>(settings)), settings,
                    new ChatClientFactory(localTransport, new TestOptionsMonitor<ArcanumSettings>(settings)), coordinator);

                parentTask = parentRuntime.Runtime.ExecuteTurnAsync(execution, input.Value,
                    new("Create a panel with local and remote members and use both answers.", DisableAllTools: false), deadline.Token);

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

                        throw new InvalidOperationException("Parent completed before scheduling both Cohort members: " + early.Value.Text);
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

                using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

                using ApprenticeService engine = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings, new ChronicleHub(),
                    NullLogger<ApprenticeService>.Instance, f.Gate);

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

                var remoteResult = await f.Store.ReadLocalResultAsync(owner, remote.Id, true, deadline.Token);

                Assert.True(remoteResult.IsSuccess, remoteResult.Error.Message);

                Assert.True(remoteResult.Value.Quest.State == QuestState.Completed, $"Remote state={remoteResult.Value.Quest.State} reason={remoteResult.Value.Quest.ReasonCode} peerPosts={peer.ProviderPosts} statuses={string.Join(",", routing.Statuses)}");

                Assert.Equal("remote actual answer", remoteResult.Value.Body);

                GuildPortalStore portal = new(f.Store, f.Authority, f.Policy, TimeProvider.System);

                var drained = await portal.DrainOutboxAsync(owner, root.Value.Quest.Id, deadline.Token);

                Assert.True(drained.IsSuccess, drained.Error.Message);

                parentTransport.Release.TrySetResult();

                var parentResult = await parentTask.WaitAsync(deadline.Token);

                await queueCompletion;

                Assert.True(parentResult.IsSuccess, parentResult.Error.Message);

                Assert.Contains("actual cohort member answer", parentTransport.Bodies[^1], StringComparison.Ordinal);

                Assert.Contains("remote actual answer", parentTransport.Bodies[^1], StringComparison.Ordinal);

                Assert.Equal("both answers received", parentResult.Value.Text);

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

                Assert.True(converged.Spend.UnknownUsd);

                Assert.True(converged.Spend.UnknownTokens);

                Assert.Equal(2L, await f.ScalarAsync($"SELECT count(*) FROM guild_cohort_members WHERE CohortId='{parentTransport.CohortId:D}' COLLATE NOCASE AND PrimaryVotingQuestId COLLATE NOCASE IN ('{local.Id:D}','{remote.Id:D}')"));

                Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox WHERE SourceKindCode=2"));

                Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

                Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_result_applications"));

                Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox o JOIN guild_inbox i ON i.EnvelopeId=o.EnvelopeId WHERE o.SourceKindCode=2 AND o.DeliveryStateCode=2 AND i.DeliveryStateCode=2"));

                int peerPostsBeforeReplay = peer.ProviderPosts;

                int wireCallsBeforeReplay = routing.Statuses.Count;

                var replay = await remotePort.DispatchAsync(remoteHandle.Value, remotePending.Value.Request!, deadline.Token);

                Assert.True(replay.IsFailure);

                Assert.Equal(RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Guild.AuthorityRevoked, replay.Error.Code);

                Assert.Equal(wireCallsBeforeReplay, routing.Statuses.Count);

                Assert.Equal(peerPostsBeforeReplay, peer.ProviderPosts);

                Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_contributions WHERE OutcomeCode=1"));

                Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

                Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox WHERE SourceKindCode=2"));

                Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

                Assert.Equal(0, parentTransport.MetadataGets);

                Assert.Equal(0, localTransport.MetadataGets);

                Assert.All(peer.GetPaths, path => Assert.Equal("/v1/models", path));

                Assert.True(peer.ProviderPosts > 0);

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

    private sealed class MixedParentTransport : HttpMessageHandler, IHttpClientFactory
    {
        internal readonly TaskCompletionSource AdmissionsComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly List<string> Bodies = [];

        internal Guid CohortId;

        internal Guid MemberId;

        internal int MetadataGets;

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

            int ordinal = Bodies.Count;

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
                Assert.Contains("actual cohort member answer", body, StringComparison.Ordinal);

                Assert.Contains("remote actual answer", body, StringComparison.Ordinal);

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

        Assert.Equal(2, peer.StartupAttempts);

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

    private sealed class MixedPeer(IHost provider, System.Diagnostics.Process process, string home, int port) : IAsyncDisposable
    {
        internal int Port { get; } = port;

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

                (process, int port, string key, int attempts) = await StartChildAsync(home, modelUrl, ct, firstPort);

                using (HttpClient admin = new())
                {
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
                MixedPeer result = new(provider, process, home, port) { StartupAttempts = attempts };

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

        internal static bool IsAddressInUse(string output) => output.Split('\n').Any(line =>
            line.Contains("Failed to bind to address http://", StringComparison.OrdinalIgnoreCase)
            && line.Contains(": address already in use.", StringComparison.OrdinalIgnoreCase));

        private static async Task<(System.Diagnostics.Process Process, int Port, string Key, int Attempts)> StartChildAsync(
            string home, string modelUrl, CancellationToken ct, int? firstPort)
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
                    Task<string?> masterKey = child.StandardOutput.ReadLineAsync(ct).AsTask();

                    StringBuilder stderr = new();

                    while (true)
                    {
                        string? line = await child.StandardError.ReadLineAsync(ct);

                        if (line is null) break;

                        stderr.AppendLine(line);

                        if (line.Contains("Arcanum DevHost listening on", StringComparison.Ordinal))
                        {
                            string key = await masterKey.WaitAsync(ct)
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
                Directory.Delete(home, true);

                Directory.CreateDirectory(Path.Combine(home, ".config", "arcanum"));
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
