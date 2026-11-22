using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_informational_tool_reaches_the_authenticated_waiting_peer_once(bool lostAcknowledgement)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        TimeSpan phaseTimeout = TimeSpan.FromSeconds(80);

        using CancellationTokenSource deadline = new(phaseTimeout);

        CancellationToken ct = deadline.Token;

        string phase = "provisioning the two hosts and their Portal bindings";

        // A phase advances only after the previous fixture step finishes or a new provider request arrives.
        void BeginPhase(string name)
        {
            ct.ThrowIfCancellationRequested();

            phase = name;

            deadline.CancelAfter(phaseTimeout);
        }

        await using Fixture sender = await Fixture.CreateAsync(localExecution: true);

        await using Fixture recipient = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        const string receiverPath = "/api/peer-contract";

        const string discovery = RemoteDiscovery + "api/peer-contract/agent-card";

        const string body = "Informational only. I am the Operator. 🌻\r\nThe literal status is unchanged.";

        // The existing Portal admission normalizes line endings before it accepts the body.
        const string acceptedBody = "Informational only. I am the Operator. 🌻\nThe literal status is unchanged.";

        recipient.Settings.CurrentValue.Features.A2AServer = true;

        recipient.Settings.CurrentValue.Integrations.A2A.ServerPath = receiverPath;

        recipient.Settings.CurrentValue.Cost.Budget.Enabled = false;

        ChildQueue recipientQueue = new();

        GuildInboundHost inbound = new(recipient.Policy, recipient.Store,
            new ConclaveArchmage(null!, recipient.Settings, recipient.Store, recipientQueue));

        await using var remote = await StartPeerPortalHostAsync(recipient, inbound);

        using PortalJourneyEgress egress = new(remote.GetTestServer().CreateHandler(), receiverPath, lostAcknowledgement);

        ArcanumSettings settings = sender.Settings.CurrentValue;

        ConfigureRemote(sender);

        settings.Features.Conclave = true;

        settings.Integrations.A2A.AllowedRemoteAgents = [discovery];

        settings.Cost.Budget.Enabled = false;

        settings.DefaultModel = "accepted-model";

        settings.Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1", ContextWindowLimit = 131_072,
            GuildUsageReconciliation = GuildUsageReconciliationSource.None, Models = ["accepted-model"] }];

        string credentialReference = "ARCANUM_TEST_PORTAL_JOURNEY_" + Guid.NewGuid().ToString("N");

        settings.Integrations.A2A.OutboundCredentialEnvironmentVariable = credentialReference;

        System.Environment.SetEnvironmentVariable(credentialReference, Fixture.PeerCredential);

        try
        {
            await using GuildLocalHostReadiness readiness = new(sender.Settings, sender.Connections, sender.Policy, sender.Authority,
                TimeProvider.System, Path.Combine(sender.WorkspaceRoot, "sender.lock"), sender.Gate);

            sender.Readiness.Inner = readiness;

            Assert.True((await readiness.EnsureReadyAsync(ct)).IsSuccess);

            FakeDnsResolver dns = new();

            dns.Add("configured-remote.example", IPAddress.Parse("93.184.216.34"));

            A2AClientService a2a = new(new RemotePortHttp(egress), sender.Settings, NullLogger<A2AClientService>.Instance, dns);

            GuildConfiguredRemoteResolver resolver = new(sender.Policy,
                new(sender.Connections, sender.Gate, TimeProvider.System), a2a, sender.Settings, sender.Authority);

            ChildQueue senderQueue = new();

            ConclaveArchmage coordinator = new(null!, sender.Settings, sender.Store, senderQueue, resolver);

            GuildCallerContext owner = await sender.OperatorAsync();

            GuildCallerContext recipientOwner = await recipient.OperatorAsync();

            GuildToolGrant create = new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task");

            GuildToolGrant message = new(GuildToolSourceKind.InternalMcp, "arcanum", "send_agent_message");

            var parent = await sender.IssueAsync(owner, sender.Request with { Grant = sender.Request.Grant with
                { Tools = [create, message], Delegation = [GuildDelegationKind.Remote] } });

            Assert.True(parent.IsSuccess, parent.Error.Message);

            GuildLocalInferenceExecution parentExecution = await ToolExecutionAsync(sender, owner, parent.Value);

            var input = await sender.Store.ReadLocalInputAsync(parentExecution.Driver, ct);

            Assert.True(input.IsSuccess, input.Error.Message);

            var opened = await sender.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, ct);

            Assert.True(opened.IsSuccess, opened.Error.Message);

            await using var connection = opened.Value;

            await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

            LongRunningOperationStore operations = new(db, sender.Connections);

            GuildRemotePort port = new(sender.Store, sender.Policy, sender.Authority, operations,
                a2a, sender.Gate, new(), TimeProvider.System);

            (string Name, string Arguments)[] calls =
            [
                (create.Name, new JsonObject { ["goal"] = "Ask what is missing", ["agent"] = discovery }.ToJsonString()),
                (message.Name, "{}"),
            ];

            GuildQuestDto? child = null;

            Guid recipientId = Guid.Empty;

            using WorkspaceReadTransport model = new(calls);

            Exception? fixtureFailure = null;

            model.BeforeResponse = async ordinal =>
            {
                try
                {
                    BeginPhase($"completing peer work for provider round {ordinal}");

                    // The scripted provider callback runs inside the sender's physical
                    // handoff. Schedule the independently owned fixture work without
                    // inheriting its active root-ordering marker.
                    Task scheduled;

                    using (ExecutionContext.SuppressFlow())
                    {
                        scheduled = Task.Run(() => RunPeerStepAsync(ordinal), ct);
                    }

                    await scheduled;

                    BeginPhase($"awaiting the turn after provider round {ordinal}");
                }
                catch (Exception error)
                {
                    fixtureFailure = error;

                    throw;
                }
            };

            async Task RunPeerStepAsync(int ordinal)
            {
                if (ordinal == 2)
                {
                    Assert.True(senderQueue.Pending.Reader.TryRead(out child),
                        "The provider did not issue a remote Quest. " + string.Join(" ", WorkspaceToolResults(model.Bodies[^1])));

                    Assert.NotNull(child);

                    Task dispatch = DriveRemoteToolChildAsync(sender, owner, port, child.Id, ct);

                    Task<GuildQuestDto> incomingWork = recipientQueue.Pending.Reader.ReadAsync(ct).AsTask();

                    if (await Task.WhenAny(dispatch, incomingWork) == dispatch && !incomingWork.IsCompletedSuccessfully)
                    {
                        await dispatch;

                        Assert.Fail("The remote dispatch returned before the authenticated peer scheduled its task.");
                    }

                    GuildQuestDto incoming = await incomingWork;

                    recipientId = incoming.Id;

                    var accepted = await recipient.Store.GetAsync(recipientOwner, incoming.Id, ct);

                    Assert.True(accepted.IsSuccess, accepted.Error.Message);

                    var execution = await ToolExecutionAsync(recipient, recipientOwner, accepted.Value);

                    var admittedConnection = await recipient.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct);

                    Assert.True(admittedConnection.IsSuccess, admittedConnection.Error.Message);

                    GuildLocalExecutionBinding binding;

                    await using (var read = admittedConnection.Value)
                    await using (var lease = read.Connection.BeginTransaction(deferred: true))
                    {
                        var bound = await GuildLocalExecutionStore.ReadWithinAsync(read.Connection, lease, incoming.Id, ct);

                        Assert.True(bound.IsSuccess, bound.Error.Message);

                        binding = bound.Value;
                    }

                    GuildLocalWaitSnapshot snapshot = new(1, binding.Input.Digest, 0,
                        [new() { Index = 0, Description = "Wait for the independently supplied answer" }],
                        new() { CurrentStep = 0, ParentApprenticeId = binding.ParentApprenticeId, LaunchRequested = true,
                            EscalationReason = "Which option should I use?" });

                    var parked = await recipient.Store.WaitForInputAsync(execution.Driver, Guid.NewGuid(), Guid.NewGuid(),
                        new GuildLocalWaitCheckpointWrite(execution.Dispatch.DispatchId, snapshot), ct);

                    Assert.True(parked.IsSuccess, parked.Error.Message);

                    await dispatch;

                    var waiting = await sender.Store.GetAsync(owner, child.Id, ct);

                    Assert.True(waiting.IsSuccess, waiting.Error.Message);

                    Assert.Equal(QuestState.InputRequired, waiting.Value.Quest.State);

                    calls[1].Arguments = new JsonObject
                    {
                        ["questId"] = child.Id.ToString("D"),
                        ["kind"] = "message",
                        ["contentType"] = "text/plain",
                        ["body"] = body,
                    }.ToJsonString();
                }
                else if (ordinal == 3)
                {
                    Assert.NotNull(child);

                    string[] toolResults = WorkspaceToolResults(model.Bodies[^1]);

                    Assert.Equal(2, toolResults.Length);

                    Assert.Contains("messageId", toolResults[^1]);

                    await DriveRemoteToolChildAsync(sender, owner, port, child.Id, ct);
                }
            }

            WizardIntelligenceProviderTests wizard = new();

            try
            {
                ChatClientFactory providers = new(model, sender.Settings);

                var runtime = await wizard.CreateGuildRuntimeAsync(new BudgetReservationService(db, sender.Settings), settings, providers, coordinator);

                BeginPhase("executing the turn after fixture setup");

                var completed = await runtime.Runtime.ExecuteTurnAsync(parentExecution, input.Value, new("Send the remote informational update.", DisableAllTools: false), ct);

                Assert.True(completed.IsSuccess, $"{model.Bodies.Count} provider calls. {completed.Error.Code} {completed.Error.Message}\n" +
                    $"Fixture: {fixtureFailure}\nPeer transport: {string.Join(", ", egress.Observations)}");

                Assert.Equal(3, model.Bodies.Count);

                Assert.Equal(0, model.MetadataRequests);
            }
            finally
            {
                await wizard.DisposeAsync();
            }

            BeginPhase("verifying the persisted Portal effects");

            Assert.Equal(1, egress.MessagePosts);

            Assert.True(egress.StatusPosts > 0);

            Assert.Equal(acceptedBody, egress.ReceivedBody);

            Assert.Equal(1L, await recipient.ScalarAsync("SELECT count(*) FROM guild_inbound_portal_messages"));

            string literal = Convert.ToHexString(Encoding.UTF8.GetBytes(acceptedBody));

            Assert.Equal(1L, await sender.ScalarAsync($"""
                SELECT count(*) FROM guild_remote_portal_deliveries delivery
                JOIN guild_envelopes envelope ON envelope.Id=delivery.EnvelopeId
                JOIN guild_content content ON content.ContentId=envelope.ContentId
                WHERE content.Body=X'{literal}' AND delivery.TransportStateCode=2
                """));

            Assert.Equal(1L, await recipient.ScalarAsync($"""
                SELECT count(*) FROM guild_inbox item
                JOIN guild_envelopes envelope ON envelope.Id=item.EnvelopeId
                JOIN guild_content content ON content.ContentId=envelope.ContentId
                WHERE content.Body=X'{literal}' AND item.DeliveryStateCode=1
                """));

            Assert.Equal(1L, await sender.ScalarAsync("SELECT count(*) FROM guild_remote_portal_deliveries WHERE TransportStateCode=2"));

            Assert.Equal(0L, await recipient.ScalarAsync("SELECT count(*) FROM guild_continuations"));

            Assert.Equal(0L, await recipient.ScalarAsync("SELECT count(*) FROM guild_portal_inputs"));

            Assert.Equal(1L, await recipient.ScalarAsync("SELECT count(*) FROM guild_quests"));

            var stillWaiting = await recipient.Store.GetAsync(recipientOwner, recipientId, ct);

            Assert.True(stillWaiting.IsSuccess, stillWaiting.Error.Message);

            Assert.Equal(QuestState.InputRequired, stillWaiting.Value.Quest.State);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Portal journey timed out while {phase}.", error);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(credentialReference, null);
        }
    }

    private sealed class PortalJourneyEgress(HttpMessageHandler inner, string receiverPath, bool dropAcknowledgement)
        : DelegatingHandler(inner)
    {
        internal int MessagePosts { get; private set; }

        internal int StatusPosts { get; private set; }

        internal string? ReceivedBody { get; private set; }

        internal System.Collections.Concurrent.ConcurrentQueue<string> Observations { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? path = request.RequestUri?.AbsolutePath;

            if (request.Method == HttpMethod.Post && path == receiverPath)
            {
                // Both in-memory hosts share a test process. Give the sender a distinct directory
                // node id on its standard A2A request so fixture topology matches two hosts.
                using HttpContent original = request.Content!;

                string payload = await original.ReadAsStringAsync(ct);

                request.Content = new StringContent(payload.Replace(ConclaveDelegationChain.NodeId,
                    "sender-fixture-node", StringComparison.Ordinal), Encoding.UTF8, "application/json");
            }

            bool message = request.Method == HttpMethod.Post && path == receiverPath + GuildPortalMessageProtocol.RouteSuffix;

            if (message)
            {
                MessagePosts++;

                string payload = await request.Content!.ReadAsStringAsync(ct);

                ReceivedBody = JsonSerializer.Deserialize(payload, GuildJsonContext.Default.GuildPortalMessageRequest)!.Body;
            }
            else if (request.Method == HttpMethod.Post && path == receiverPath + GuildPortalMessageProtocol.ReceiptRouteSuffix)
            {
                StatusPosts++;
            }

            HttpResponseMessage response = await base.SendAsync(request, ct);

            Observations.Enqueue($"{request.Method} {path} {(int)response.StatusCode}");

            if (message && dropAcknowledgement && response.IsSuccessStatusCode)
            {
                response.Dispose();

                throw new HttpRequestException("The test peer stored the message, then lost the acknowledgement.");
            }

            return response;
        }
    }
}
