using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("restart-wait")]
    [InlineData("slow-cancel")]
    [InlineData("disconnect")]
    [InlineData("finite-usd")]
    [InlineData("finite-tokens")]
    public async Task Financial_off_outgoing_remote_lifecycle_enforces_physical_and_financial_boundaries(string scenario)
    {
        bool restartAndContinue = scenario == "restart-wait";

        A2ADispatchMode mode = scenario is "restart-wait" or "slow-cancel" ? A2ADispatchMode.Continuable : A2ADispatchMode.Blocking;

        await using Fixture f = await Fixture.CreateAsync(gateTimeProvider: new ManualTimerTimeProvider());

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Cost.Budget.Enabled = false;

        f.Settings.CurrentValue.Features.A2AClient = true;

        f.Settings.CurrentValue.Integrations ??= new();

        f.Settings.CurrentValue.Integrations.A2A ??= new();

        f.Settings.CurrentValue.Integrations.A2A.AllowedRemoteAgents = ["http://remote-port.example/"];

        GuildRemoteTransportDescriptor target = new("remote-port", "http://remote-port.example/agent", "1.0", ["JSONRPC"], [], null, null, null);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Remote", "Remote peer", "1", [], ["text/plain"], ["text/plain"],
            new(false, false, false), new(GuildAgentSourceKind.Remote, null, target.PeerId, true, null));

        GuildStore roster = new(f.Connections, f.Gate, TimeProvider.System);

        var stored = await roster.UpsertCardAsync(new("remote-port", null, null, null,
            new(card, "", [], target, "http://remote-port.example/"), new string('A', 64)), default);

        Assert.True(stored.IsSuccess, stored.Error.Message);

        var caller = await f.OperatorAsync();

        GuildBudgetGrant budget = scenario switch
        {
            "finite-usd" => new(new(GuildBudgetMode.Finite, 1m), new(GuildBudgetMode.Unlimited)),
            "finite-tokens" => new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Finite, 100_000)),
            _ => new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)),
        };

        var root = f.Request with { AgentId = stored.Value.Payload.Card.AgentId,
            Grant = f.Request.Grant with { Delegation = [GuildDelegationKind.Remote], Budget = budget },
            RemoteOptions = new((GuildRemoteDispatchMode)mode) };

        var issued = await f.Store.IssueAsync(caller,
            new(f.Generation, null, caller.InvocationId, caller.InvocationId, root.IdempotencyKey), root, default);

        if (scenario is "finite-usd" or "finite-tokens")
        {
            Assert.True(issued.IsFailure);

            Assert.Equal(ErrorCodes.Guild.BudgetCapabilityUnavailable, issued.Error.Code);

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects"));

            return;
        }

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestDto quest = issued.Value.Quest;

        GuildPortalStore portal = new(f.Store, f.Authority, f.Policy, TimeProvider.System);

        var snapshot = await portal.CaptureTreeSnapshotAsync(caller, quest.Id, default);

        Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

        var recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, default);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        LongRunningOperation operation = (await operations.GetAsync(quest.OperationId))!;

        RemotePortAgent agent = new(scenario);

        FinancialOffCapturingAgent peer = new(agent);

        CompletedRemoteTaskStore peerTasks = new();

        FakeDnsResolver peerDns = new();

        peerDns.Add("callback.example", IPAddress.Parse("93.184.216.35"));

        ArcanumSettings peerSettings = new()
        {
            Features = new() { Conclave = true, A2AServer = true },
            Integrations = new() { A2A = new() { PushNotifications = true, AllowedRemoteAgents = ["https://callback.example/"] } },
        };

        using IHost host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();

            web.ConfigureServices(services => services.AddRouting().AddAdHocHttpJson());

            web.Configure(app =>
            {
                app.UseRouting();

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapWellKnownAgentCard(new AgentCard { Name = "Remote", Description = "Peer", Version = "1",
                        SupportedInterfaces = [new() { Url = target.Endpoint, ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
                        Capabilities = new() { PushNotifications = false }, DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"] });

                    endpoints.MapA2A(new ArcanumA2AServer(peer, peerTasks, new ChannelEventNotifier(),
                        new A2APushNotificationRegistry(new TestOptionsMonitor<ArcanumSettings>(peerSettings), peerDns,
                            NullLogger<A2APushNotificationRegistry>.Instance), NullLogger<A2AServer>.Instance, new A2AServerOptions()), "/agent");
                });
            });
        }).StartAsync();

        using HttpMessageHandler http = host.GetTestServer().CreateHandler();

        FakeDnsResolver dns = new();

        dns.Add("remote-port.example", IPAddress.Parse("93.184.216.34"));

        using PushRemoteCapture capture = new(http);

        using HttpMessageHandler transport = scenario == "disconnect" ? new DropRemoteAcknowledgement(capture, 1) : capture;

        A2ASendingCallbackRegistry callbacks = new(NullLogger<A2ASendingCallbackRegistry>.Instance);

        A2AClientService client = new(new RemotePortHttp(transport), f.Settings, NullLogger<A2AClientService>.Instance, dns, callbacks: callbacks);

        GuildRemotePort port = new(f.Store, f.Policy, f.Authority, operations, client, f.Gate, new(), TimeProvider.System);

        GuildDispatchRequest request = new(new(root.IdempotencyKey, issued.Value.IntentDigest), quest.Id, quest.OperationId,
            quest.RunId, quest.Id, operation.Revision, quest.GoalRef.Digest);

        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

        var result = await port.DispatchAsync(execution.Value, request, deadline.Token);

        Assert.True(result.IsSuccess, result.Error.Message);

        if (scenario == "disconnect")
        {
            Assert.Equal(QuestState.Working, result.Value.State);

            Assert.True(result.Value.ReconciliationRequired);

            var retry = await port.DispatchAsync(execution.Value, request, deadline.Token);

            Assert.True(retry.IsSuccess, retry.Error.Message);

            Assert.Equal(QuestState.Working, retry.Value.State);

            Assert.Equal(1, agent.Calls);

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=1 AND UnknownUsd=1 AND UnknownTokens=1"));

            return;
        }

        if (scenario == "slow-cancel")
        {
            Assert.Equal(QuestState.InputRequired, result.Value.State);

            var current = await f.Store.GetAsync(caller, quest.Id, deadline.Token);

            Assert.True(current.IsSuccess, current.Error.Message);

            GuildCancelQuestRequest stop = new(Guid.NewGuid(), current.Value.Quest.Revision, GuildCancellationReason.OperatorRequested);

            var digest = GuildQuestIntent.Compute(quest.Id, stop);

            Assert.True(digest.IsSuccess, digest.Error.Message);

            var cancelled = await f.Store.RequestCancellationAsync(caller, new(stop.IdempotencyKey, digest.Value), quest.Id, stop, deadline.Token);

            Assert.True(cancelled.IsSuccess, cancelled.Error.Message);

            var mutation = await f.Store.ReadLocalCancellationAsync(caller, quest.Id, deadline.Token);

            Assert.True(mutation.IsSuccess, mutation.Error.Message);

            var stopped = await port.RequestCancellationAsync(execution.Value, mutation.Value, quest.Id, quest.Id, deadline.Token);

            Assert.True(stopped.IsSuccess, stopped.Error.Message);

            Assert.Equal(QuestState.Canceled, stopped.Value.State);

            Assert.Equal(1, agent.Calls);

            Assert.Equal(1, agent.Cancellations);

            var final = await f.Store.GetAsync(caller, quest.Id, deadline.Token);

            Assert.True(final.IsSuccess, final.Error.Message);

            Assert.Equal(QuestState.Canceled, final.Value.Quest.State);

            Assert.NotNull(final.Value.Quest.ResultReceiptId);

            return;
        }

        if (restartAndContinue)
        {
            Assert.Equal(QuestState.InputRequired, result.Value.State);

            var initialRun = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, quest.Id, deadline.Token);

            Assert.True(initialRun.IsSuccess, initialRun.Error.Message);

            var taskIdentity = await f.Store.ReadRemoteTaskIdentityAsync(new(execution.Value, "observe", 0, initialRun.Value), deadline.Token);

            Assert.True(taskIdentity.IsSuccess, taskIdentity.Error.Message);

            long evidenceBeforeUpdate = await f.ScalarAsync("SELECT count(*) FROM guild_budget_evidence");

            long eventsBeforeUpdate = await f.ScalarAsync("SELECT count(*) FROM guild_events WHERE EventKindCode=" + (int)GuildEventKind.SpendChanged);

            AgentTask peerTask = (await peerTasks.GetTaskAsync(taskIdentity.Value.TaskId, deadline.Token))!;

            Assert.NotNull(peerTask);

            peerTask.Metadata ??= new();

            A2ASendingUsageMetadata.Write(peerTask.Metadata, 20, 1m);

            await peerTasks.SaveTaskAsync(taskIdentity.Value.TaskId, peerTask, deadline.Token);

            int readsBeforeUpdate = capture.TaskReads;

            var newer = await port.DispatchAsync(execution.Value, request, deadline.Token);

            Assert.True(newer.IsSuccess, newer.Error.Message);

            Assert.Equal(QuestState.InputRequired, newer.Value.State);

            Assert.True(capture.TaskReads > readsBeforeUpdate);

            Assert.Equal(1, agent.Calls);

            Assert.Equal(evidenceBeforeUpdate + 1, await f.ScalarAsync("SELECT count(*) FROM guild_budget_evidence"));

            Assert.Equal(eventsBeforeUpdate + 1, await f.ScalarAsync("SELECT count(*) FROM guild_events WHERE EventKindCode=" + (int)GuildEventKind.SpendChanged));

            GuildLocalHostPolicy restartedPolicy = f.NewPolicy();

            using SignetAuthority restartedAuthority = new(new(f.Connections), f.Gate, restartedPolicy, TimeProvider.System);

            var restartedCaller = await restartedPolicy.CreateNativeOperatorAsync(Guid.NewGuid(), deadline.Token);

            Assert.True(restartedCaller.IsSuccess, restartedCaller.Error.Message);

            GuildQuestStore restartedStore = new(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority, restartedPolicy,
                new GuildBudgetLedger(), TimeProvider.System, readiness: f.Readiness);

            var current = await restartedStore.GetAsync(restartedCaller.Value, quest.Id, deadline.Token);

            Assert.True(current.IsSuccess, current.Error.Message);

            var context = await restartedPolicy.CreateRecoveryAsync(restartedCaller.Value, quest.Id, quest.OperationId,
                current.Value.Quest.SignetRevision, deadline.Token);

            Assert.True(context.IsSuccess, context.Error.Message);

            var rebound = await restartedAuthority.RebindAsync(context.Value, deadline.Token);

            Assert.True(rebound.IsSuccess, rebound.Error.Message);

            var restartedRun = await restartedPolicy.CreateRunCallerAsync(restartedAuthority, rebound.Value, quest.Id, deadline.Token);

            Assert.True(restartedRun.IsSuccess, restartedRun.Error.Message);

            var persistedIdentity = await restartedStore.ReadRemoteTaskIdentityAsync(new(rebound.Value, "observe", 0, restartedRun.Value), deadline.Token);

            Assert.True(persistedIdentity.IsSuccess, persistedIdentity.Error.Message);

            Assert.Equal(taskIdentity.Value.TaskId, persistedIdentity.Value.TaskId);

            Assert.Equal(taskIdentity.Value.ContextId, persistedIdentity.Value.ContextId);

            long evidenceBeforeReplay = await f.ScalarAsync("SELECT count(*) FROM guild_budget_evidence");

            long eventsBeforeReplay = await f.ScalarAsync("SELECT count(*) FROM guild_events WHERE EventKindCode=" + (int)GuildEventKind.SpendChanged);

            peerTask.Metadata!.Remove(A2ASendingUsageMetadata.MetadataKey);

            await peerTasks.SaveTaskAsync(taskIdentity.Value.TaskId, peerTask, deadline.Token);

            GuildRemotePort restartedPort = new(restartedStore, restartedPolicy, restartedAuthority, operations, client, f.Gate, new(), TimeProvider.System);

            int readsBeforeReplay = capture.TaskReads;

            var replay = await restartedPort.DispatchAsync(rebound.Value, request, deadline.Token);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.Equal(QuestState.InputRequired, replay.Value.State);

            Assert.True(capture.TaskReads > readsBeforeReplay);

            Assert.Equal(evidenceBeforeReplay, await f.ScalarAsync("SELECT count(*) FROM guild_budget_evidence"));

            Assert.Equal(eventsBeforeReplay, await f.ScalarAsync("SELECT count(*) FROM guild_events WHERE EventKindCode=" + (int)GuildEventKind.SpendChanged));

            var question = await restartedStore.ReadLocalQuestionAsync(restartedCaller.Value, quest.Id, deadline.Token);

            Assert.True(question.IsSuccess, question.Error.Message);

            Assert.Equal("Which answer?", question.Value.Question);

            GuildContinueRequest answer = new(Guid.NewGuid(), question.Value.Revision, question.Value.AwaitingInput.ContinuationId,
                "text/plain", "The first answer");

            var accepted = await restartedStore.AcceptContinuationAsync(restartedCaller.Value, quest.Id, answer, deadline.Token);

            Assert.True(accepted.IsSuccess, accepted.Error.Message);

            operation = (await operations.GetAsync(quest.OperationId))!;

            var continuation = await restartedStore.GetContinuationAsync(restartedCaller.Value, quest.Id, answer.ContinuationId, deadline.Token);

            Assert.True(continuation.IsSuccess, continuation.Error.Message);

            var runCaller = await restartedPolicy.CreateRunCallerAsync(restartedAuthority, rebound.Value, answer.ContinuationId, deadline.Token);

            Assert.True(runCaller.IsSuccess, runCaller.Error.Message);

            var dispatch = await restartedStore.ReadLocalContinuationAsync(new(rebound.Value, "observe", 0, runCaller.Value),
                answer.ContinuationId, deadline.Token, false);

            Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

            request = new(dispatch.Value!.Mutation, quest.Id, quest.OperationId, quest.RunId, answer.ContinuationId,
                operation.Revision, continuation.Value.ContentRef.Digest);

            result = await restartedPort.ContinueAsync(rebound.Value, request, answer, deadline.Token);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal("The first answer", peer.Messages[1]);
        }

        Assert.Equal(QuestState.Completed, result.Value.State);

        var retained = await f.Store.GetAsync(caller, quest.Id, deadline.Token);

        Assert.True(retained.IsSuccess, retained.Error.Message);

        Assert.Equal(GuildAccountingState.Settled, retained.Value.Quest.AccountingState);

        var body = await f.Store.ReadLocalResultAsync(caller, quest.Id, true, deadline.Token);

        Assert.True(body.IsSuccess, body.Error.Message);

        Assert.Equal("Remote answer", body.Value.Body);

        Assert.NotNull(body.Value.Receipt);

        Assert.Equal(restartAndContinue ? 2 : 1, agent.Calls);

        Assert.Single(agent.TaskIds.Distinct());

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=2 AND UnknownUsd=1 AND UnknownTokens=1"));

        if (!restartAndContinue)
        {
            var replay = await port.DispatchAsync(execution.Value, request, deadline.Token);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.Equal(1, agent.Calls);
        }

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox WHERE SourceKindCode=2"));

        var events = await portal.ReadScopedReplayAsync(caller, quest.Id, quest.Id, null, snapshot.Value.ResumeCursor, deadline.Token);

        Assert.True(events.IsSuccess, events.Error.Message);

        var physical = events.Value.Page.Items.First(item => item.EventKind == GuildEventKind.SpendChanged && item.ObjectId != quest.Id);

        GuildSpendSourceDto source = Assert.IsType<GuildSpendSourceDto>(physical.SpendSource);

        var receipt = await f.Store.ReadOperatorSpendReceiptAsync(caller, quest.Id, source.EffectId, source.ReceiptId, deadline.Token);

        Assert.True(receipt.IsSuccess, receipt.Error.Message);

        Assert.Null(receipt.Value.Facts.LocalTokens);
    }
    [Fact]
    public async Task Financial_off_actual_remote_cohort_member_settles_and_converges()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Cost.Budget.Enabled = false;

        f.Settings.CurrentValue.Features.A2AClient = true;

        f.Settings.CurrentValue.Integrations.A2A.AllowedRemoteAgents = ["http://remote-port.example/"];

        GuildRemoteTransportDescriptor target = new("remote-port", "http://remote-port.example/agent", "1.0", ["JSONRPC"], [], null, null, null);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Remote", "Remote peer", "1", [], ["text/plain"], ["text/plain"],
            new(false, false, false), new(GuildAgentSourceKind.Remote, null, target.PeerId, true, null));

        GuildStore roster = new(f.Connections, f.Gate, TimeProvider.System);

        var stored = await roster.UpsertCardAsync(new("remote-port", null, null, null,
            new(card, "", [], target, "http://remote-port.example/"), new string('A', 64)), default);

        Assert.True(stored.IsSuccess, stored.Error.Message);

        GuildCallerContext owner = await f.OperatorAsync();

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with
        {
            Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Remote, GuildDelegationKind.Cohort],
        } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildLocalInferenceExecution parentExecution = await ToolExecutionAsync(f, owner, root.Value);

        GuildCallerContext issuer = parentExecution.Driver.Caller!;

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Store,
            new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        var cohort = await cohorts.CreateAsync(issuer, new(Guid.NewGuid(), root.Value.Quest.Id, "Remote vote",
            new(GuildConvergenceKind.All, null), new("text/plain"), null), default);

        Assert.True(cohort.IsSuccess, cohort.Error.Message);

        Guid key = Guid.NewGuid();

        var member = await f.Store.IssueCohortMemberAsync(issuer, new(f.Generation, root.Value.Quest.Id, null, key, key),
            cohort.Value.Id, cohort.Value.Revision,
            new(key, root.Value.Quest.Revision, stored.Value.Payload.Card.AgentId, "Remote member", "Contribute", new(),
                new(GuildRemoteDispatchMode.Blocking)), default);

        Assert.True(member.IsSuccess, member.Error.Message);

        RemotePortAgent agent = new("complete");

        FakeDnsResolver peerDns = new();

        ArcanumSettings peerSettings = new() { Features = new() { Conclave = true, A2AServer = true } };

        using IHost peer = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();

            web.ConfigureServices(services => services.AddRouting().AddAdHocHttpJson());

            web.Configure(app =>
            {
                app.UseRouting();

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapWellKnownAgentCard(new AgentCard { Name = "Remote", Description = "Peer", Version = "1",
                        SupportedInterfaces = [new() { Url = target.Endpoint, ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
                        Capabilities = new(), DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"] });

                    endpoints.MapA2A(new ArcanumA2AServer(agent, new InMemoryTaskStore(), new ChannelEventNotifier(),
                        new A2APushNotificationRegistry(new TestOptionsMonitor<ArcanumSettings>(peerSettings), peerDns,
                            NullLogger<A2APushNotificationRegistry>.Instance), NullLogger<A2AServer>.Instance, new A2AServerOptions()), "/agent");
                });
            });
        }).StartAsync();

        using HttpMessageHandler handler = peer.GetTestServer().CreateHandler();

        FakeDnsResolver dns = new();

        dns.Add("remote-port.example", IPAddress.Parse("93.184.216.34"));

        A2AClientService client = new(new RemotePortHttp(handler), f.Settings, NullLogger<A2AClientService>.Instance, dns);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        GuildRemotePort port = new(f.Store, f.Policy, f.Authority, operations, client, f.Gate, new(), TimeProvider.System);

        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

        await DriveRemoteToolChildAsync(f, owner, port, member.Value.Quest.Id, deadline.Token);

        var result = await f.Store.ReadLocalResultAsync(owner, member.Value.Quest.Id, true, deadline.Token);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("Remote answer", result.Value.Body);

        Assert.NotNull(result.Value.Receipt);

        var retained = await f.Store.GetAsync(owner, member.Value.Quest.Id, deadline.Token);

        Assert.True(retained.IsSuccess, retained.Error.Message);

        Assert.Equal(GuildAccountingState.Settled, retained.Value.Quest.AccountingState);

        Assert.Equal(1, agent.Calls);

        Assert.Single(agent.TaskIds.Distinct());

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=2 AND UnknownUsd=1 AND UnknownTokens=1"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox WHERE SourceKindCode=2"));

        GuildPortalStore portal = new(f.Store, f.Authority, f.Policy, TimeProvider.System);

        var delivered = await portal.DrainOutboxAsync(owner, root.Value.Quest.Id, deadline.Token);

        Assert.True(delivered.IsSuccess, delivered.Error.Message);

        Assert.True(delivered.Value > 0);

        for (int index = 0; index < 3; index++)
        {
            var claim = await portal.ClaimAsync(parentExecution.Driver, deadline.Token);

            Assert.True(claim.IsSuccess, claim.Error.Message);

            var applied = await portal.ApplyAsync(claim.Value, deadline.Token);

            Assert.True(applied.IsSuccess, applied.Error.Message);
        }

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_result_applications"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_result_inputs"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox o JOIN guild_inbox i ON i.EnvelopeId=o.EnvelopeId WHERE o.SourceKindCode=2 AND o.DeliveryStateCode=2 AND i.DeliveryStateCode=2"));

        Assert.Equal(1L, await f.ScalarAsync("""
            SELECT count(*) FROM guild_cohort_contributions v JOIN guild_content c ON c.ContentId=v.ContentId
            WHERE v.OutcomeCode=1 AND CAST(c.Body AS TEXT)='Remote answer'
            """));

        var current = await cohorts.GetAsync(issuer, cohort.Value.Id, deadline.Token);

        Assert.True(current.IsSuccess, current.Error.Message);

        var closed = await cohorts.CloseAsync(issuer, cohort.Value.Id, new(Guid.NewGuid(), current.Value.Revision), deadline.Token);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        var draining = await WaitForConvergenceAsync(cohorts, owner, cohort.Value.Id, value => value.State == GuildCohortState.Draining);

        Assert.Equal(GuildConvergenceDecision.Success, draining.Decision);

        var stopped = await f.Store.RefreshCancellationConvergenceAsync(owner, member.Value.Quest.Id, deadline.Token);

        Assert.True(stopped.IsSuccess, stopped.Error.Message);

        Assert.True(stopped.Value);

        var converged = await WaitForConvergenceAsync(cohorts, owner, cohort.Value.Id, value => value.State == GuildCohortState.Completed);

        Assert.Equal(GuildConvergenceDecision.Success, converged.Decision);

        Assert.True(converged.Spend.UnknownUsd);

        Assert.True(converged.Spend.UnknownTokens);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

        Assert.Equal(1, agent.Calls);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_outbox WHERE SourceKindCode=2"));
    }

    private sealed class FinancialOffCapturingAgent(RemotePortAgent inner) : IAgentHandler
    {
        internal List<string?> Messages { get; } = [];

        public Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken ct)
        {
            Messages.Add(context.Message?.Parts.FirstOrDefault()?.Text);

            return inner.ExecuteAsync(context, eventQueue, ct);
        }

        public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken ct) =>
            inner.CancelAsync(context, eventQueue, ct);
    }
}
