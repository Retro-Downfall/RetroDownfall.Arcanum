using A2A;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Operator_information_refuses_unqualified_remote_recipient_before_creating_an_inbox()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        (GuildCallerContext owner, GuildQuestStored issued) = await IssueRemotePortalRecipientAsync(fixture, qualified: false);

        GuildQuestDto quest = issued.Quest;

        long before = await fixture.ScalarAsync("SELECT count(*) FROM guild_envelopes");

        var result = await NativePortalMessages(fixture).SendAsync(owner, quest.Id,
            new(Guid.NewGuid(), quest.Revision, GuildMessageKind.Message, "text/plain", "Information for the remote task"), default);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guild.RemoteMessagingUnavailable, result.Error.Code);

        Assert.Equal(before, await fixture.ScalarAsync("SELECT count(*) FROM guild_envelopes"));

        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM guild_inbox"));
    }

    [Fact]
    public async Task Lost_remote_portal_acknowledgement_survives_driver_replacement_without_resending_the_body()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        using PortalTestCredential credential = new(fixture);

        (GuildCallerContext owner, GuildQuestStored issued) = await IssueRemotePortalRecipientAsync(fixture, qualified: true);

        GuildQuestDto quest = issued.Quest;

        var opened = await fixture.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, fixture.Connections);

        GuildCallerContext run = await RunCallerAsync(fixture, owner, issued);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var acquired = await operations.TryAcquireLeaseAsync(quest.OperationId, "remote-portal-first", now, now.AddMinutes(5));

        Assert.True(acquired.Acquired);

        GuildQuestDriver driver = new(run.Execution!, "remote-portal-first", acquired.Operation.Revision, run);

        await ConfirmRemotePortalTaskAsync(fixture, driver, quest);

        GuildPortalStore portal = new(fixture.Store, fixture.Authority, fixture.Policy, TimeProvider.System);

        var cursor = await portal.CaptureScopedReplayCursorAsync(owner, quest.Id, quest.Id, null, default);

        Assert.True(cursor.IsSuccess, cursor.Error.Message);

        var latest = await fixture.Store.GetAsync(owner, quest.Id, default);

        Assert.True(latest.IsSuccess, latest.Error.Message);

        GuildSendOperatorMessageRequest publication = new(Guid.NewGuid(), latest.Value.Quest.Revision,
            GuildMessageKind.Message, "text/plain", "exact remote body");

        var published = await NativePortalMessages(fixture).SendAsync(owner, quest.Id, publication, default);

        Assert.True(published.IsSuccess, published.Error.Message);

        var acceptedAgain = await NativePortalMessages(fixture).SendAsync(owner, quest.Id, publication, default);

        Assert.Equal(published.Value.MessageId, acceptedAgain.Value.MessageId);

        Assert.Equal(GuildDeliveryState.Pending, published.Value.DeliveryState);

        var selected = await fixture.Store.ReadRemotePortalWorkAsync(driver, default, GuildRemotePortalWorkKind.Send);

        Assert.True(selected.IsSuccess, selected.Error.Message);

        GuildRemotePortalWork ready = Assert.IsType<GuildRemotePortalWork>(selected.Value);

        var acceptedEvents = await portal.ReadScopedReplayAsync(owner, quest.Id, quest.Id, null, cursor.Value.Cursor, default);

        Assert.True(acceptedEvents.IsSuccess, acceptedEvents.Error.Message);

        Assert.Contains(acceptedEvents.Value.Page.Items, item => item.PortalDelivery is
            { TransportEvidence: GuildPortalTransportEvidence.Ready, DeliveryState: GuildDeliveryState.Pending } evidence
            && evidence.InboxId == ready.InboxId && evidence.RecipientQuestId == quest.Id);

        GuildPortalMessageReceiptQuery? expectedReceipt = null;

        int bodySends = 0;

        Task<Result<GuildPortalMessageDeliveryReceipt>> DropAck(GuildPortalMessageRequest message, CancellationToken _)
        {
            bodySends++;

            Assert.Equal(ready.InboxId, message.MessageId);

            Assert.Equal("exact remote body", message.Body);

            expectedReceipt = message.ReceiptQuery();

            return Task.FromResult(Result<GuildPortalMessageDeliveryReceipt>.Failure(
                new(ErrorCodes.Sending.AgentUnreachable, "Acknowledgement lost.")));
        }

        Assert.True((await fixture.Store.SendRemotePortalMessageAsync(driver, ready, DropAck, default)).IsFailure);

        Assert.Equal(1, bodySends);

        Assert.NotNull(expectedReceipt);

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT TransportStateCode FROM guild_remote_portal_deliveries"));

        Result<bool> swept = await fixture.Authority.InQuestTransactionAsync<bool>(quest.Id,
            async (dbConnection, transaction, _, token) =>
            {
                Result disposition = await GuildPortalCleanupParticipant.WithholdTerminalWithinAsync(dbConnection, transaction,
                    quest.Id, now, token);

                return disposition.IsFailure ? disposition.Error : true;
            }, default);

        Assert.True(swept.IsSuccess, swept.Error.Message);

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT DeliveryStateCode FROM guild_inbox"));

        await fixture.ExecuteAsync($"UPDATE LongRunningOperations SET LeaseExpiresAt='2000-01-01T00:00:00.0000000Z' WHERE Id='{quest.OperationId:N}'");

        var replaced = await operations.TryAcquireLeaseAsync(quest.OperationId, "remote-portal-replaced", now, now.AddMinutes(5));

        Assert.True(replaced.Acquired);

        GuildQuestDriver fresh = new(run.Execution!, "remote-portal-replaced", replaced.Operation.Revision, run);

        Assert.True((await fixture.Store.SendRemotePortalMessageAsync(fresh, ready, DropAck, default)).IsFailure);

        Assert.Equal(1, bodySends);

        Guid peerReceipt = Guid.NewGuid();

        var observation = await fixture.Store.ReadRemotePortalWorkAsync(fresh, default, GuildRemotePortalWorkKind.Observe);

        Assert.True(observation.IsSuccess, observation.Error.Message);

        GuildRemotePortalWork unknown = Assert.IsType<GuildRemotePortalWork>(observation.Value);

        var wrongAck = await fixture.Store.ObserveRemotePortalMessageAsync(fresh, unknown, (query, _) =>
            Task.FromResult(Result<GuildPortalMessageDeliveryReceipt>.Success(new(query.TaskId, "wrong scope", query.MessageId,
                query.IntentDigest, peerReceipt, GuildDeliveryState.Pending, 1, null))), default);

        Assert.True(wrongAck.IsFailure);

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT TransportStateCode FROM guild_remote_portal_deliveries"));

        var acknowledged = await fixture.Store.ObserveRemotePortalMessageAsync(fresh, unknown, (query, _) =>
        {
            Assert.Equal(expectedReceipt, query);

            return Task.FromResult(Result<GuildPortalMessageDeliveryReceipt>.Success(new(query.TaskId, query.ContextId,
                query.MessageId, query.IntentDigest, peerReceipt, GuildDeliveryState.Pending, 1, null)));
        }, default);

        Assert.True(acknowledged.IsSuccess, acknowledged.Error.Message);

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT DeliveryStateCode FROM guild_inbox"));

        var pending = await fixture.Store.ReadRemotePortalWorkAsync(fresh, default, GuildRemotePortalWorkKind.Observe);

        Assert.True(pending.IsSuccess, pending.Error.Message);

        var applied = await fixture.Store.ObserveRemotePortalMessageAsync(fresh, Assert.IsType<GuildRemotePortalWork>(pending.Value), (query, _) =>
            Task.FromResult(Result<GuildPortalMessageDeliveryReceipt>.Success(new(query.TaskId, query.ContextId, query.MessageId,
                query.IntentDigest, peerReceipt, GuildDeliveryState.Applied, 2, Guid.NewGuid()))), default);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(2L, await fixture.ScalarAsync("SELECT DeliveryStateCode FROM guild_inbox"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM guild_portal_receipts"));

        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM guild_portal_inputs"));

        Assert.Equal(1, bodySends);

        var events = await portal.ReadScopedReplayAsync(owner, quest.Id, quest.Id, null, acceptedEvents.Value.ResumeCursor, default);

        Assert.True(events.IsSuccess, events.Error.Message);

        Assert.Contains(events.Value.Page.Items, item => item.PortalDelivery is
            { TransportEvidence: GuildPortalTransportEvidence.PeerAccepted, DeliveryState: GuildDeliveryState.Applied } evidence
            && evidence.PeerReceiptId == peerReceipt && evidence.InboxId == ready.InboxId);
    }

    [Fact]
    public async Task Mixed_cohort_information_preserves_local_application_and_remote_withholding()
    {
        await using Fixture fixture = await Fixture.CreateAsync(localExecution: true);

        using PortalTestCredential credential = new(fixture);

        Guid peerAgent = await RegisterRemotePortalCardAsync(fixture, qualified: true);

        GuildCallerContext owner = await fixture.OperatorAsync();

        GuildToolGrant message = new(GuildToolSourceKind.InternalMcp, "arcanum", "send_agent_message");

        var root = await fixture.IssueAsync(owner, fixture.Request with { Grant = fixture.Request.Grant with
            { Tools = [message], Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Remote, GuildDelegationKind.Cohort] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext issuer = await BindCohortRunAsync(fixture, owner, root.Value);

        GuildCohortStore cohorts = new(new(fixture.Connections, fixture.Gate, TimeProvider.System), fixture.Authority,
            fixture.Store, new(), fixture.Policy, TimeProvider.System);

        Guid rootId = root.Value.Quest.Id;

        var created = await cohorts.CreateAsync(issuer, new(Guid.NewGuid(), rootId, "Mixed audience",
            new(GuildConvergenceKind.All, null), new("text/plain"), null), default);

        Assert.True(created.IsSuccess, created.Error.Message);

        async Task<GuildQuestStored> AddMemberAsync(long revision, string name, Guid? agent = null)
        {
            Guid key = Guid.NewGuid();

            var member = await fixture.Store.IssueCohortMemberAsync(issuer, new(fixture.Generation, rootId, null, key, key),
                created.Value.Id, revision, new(key, root.Value.Quest.Revision, agent, name, "Contribute", new(),
                    agent is null ? null : new(GuildRemoteDispatchMode.Continuable)), default);

            Assert.True(member.IsSuccess, member.Error.Message);

            return member.Value;
        }

        GuildQuestStored sender = await AddMemberAsync(1, "Sender");

        GuildQuestStored local = await AddMemberAsync(2, "Local");

        GuildQuestStored remote = await AddMemberAsync(3, "Remote", peerAgent);

        var opened = await fixture.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, fixture.Connections);

        GuildCallerContext remoteRun = await RunCallerAsync(fixture, owner, remote);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var leased = await operations.TryAcquireLeaseAsync(remote.Quest.OperationId, "mixed-portal", now, now.AddMinutes(5));

        Assert.True(leased.Acquired);

        GuildQuestDriver driver = new(remoteRun.Execution!, "mixed-portal", leased.Operation.Revision, remoteRun);

        await ConfirmRemotePortalTaskAsync(fixture, driver, remote.Quest);

        Guid cohortId = created.Value.Id;

        GuildPortalStore portal = new(fixture.Store, fixture.Authority, fixture.Policy, TimeProvider.System);

        var cursor = await portal.CaptureScopedReplayCursorAsync(owner, rootId, null, cohortId, default);

        Assert.True(cursor.IsSuccess, cursor.Error.Message);

        const string body = "The same information for the local and authenticated peer recipients";

        var senderExecution = await ToolExecutionAsync(fixture, owner, sender);

        var input = await fixture.Store.ReadLocalInputAsync(senderExecution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        ConclaveArchmage coordinator = new(null!, fixture.Settings, fixture.Store, new InboundScheduler(), cohorts: cohorts);

        using GuildLocalToolRegistry registry = new(senderExecution, input.Value, coordinator);

        var context = fixture.Store.CreateToolContext(senderExecution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool(message.Name), message, default);

        Assert.NotNull(function);

        using (registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), message))
        {
            object? published = await context.InvokeBoundAsync(function, new() { ["cohortId"] = cohortId.ToString("D"),
                ["kind"] = "message", ["contentType"] = "text/plain", ["body"] = body }, default);

            Assert.False(published is string, published?.ToString());

            Assert.IsType<GuildLocalChildToolResult>(published);
        }

        Assert.Equal(2L, await fixture.ScalarAsync("SELECT count(*) FROM guild_portal_cohort_audience"));

        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM guild_remote_portal_deliveries WHERE TransportStateCode=0"));

        var localExecution = await ToolExecutionAsync(fixture, owner, local);

        var prepared = await fixture.Store.PrepareCircleInputAsync(localExecution, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        GuildCircleInputBatch batch = Assert.IsType<GuildCircleInputBatch>(prepared.Value);

        Assert.Single(batch.Items);

        Assert.Contains(body, batch.Prompt, StringComparison.Ordinal);

        Assert.Contains("UNTRUSTED DATA", batch.Prompt, StringComparison.Ordinal);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), local.Quest.Id, localExecution.Dispatch.DispatchId,
            local.Quest.RunId, GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

        var applied = await fixture.Store.ExecuteProviderEffectAsync(localExecution, new BudgetReservationService(db, fixture.Settings),
            effect, _ => Task.FromResult(Result<string>.Success("processed")), default, circleInputs: [batch]);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        var withheld = await fixture.Authority.InQuestTransactionAsync<bool>(rootId, async (dbConnection, transaction, _, token) =>
        {
            Result result = await GuildPortalCleanupParticipant.WithholdTerminalWithinAsync(dbConnection, transaction, remote.Quest.Id, now, token);

            return result.IsFailure ? result.Error : true;
        }, default);

        Assert.True(withheld.IsSuccess, withheld.Error.Message);

        var events = await portal.ReadScopedReplayAsync(owner, rootId, null, cohortId, cursor.Value.Cursor, default);

        Assert.True(events.IsSuccess, events.Error.Message);

        Assert.Contains(events.Value.Page.Items, item => item.PortalDelivery is
            { TransportEvidence: null, DeliveryState: GuildDeliveryState.Applied } evidence && evidence.RecipientQuestId == local.Quest.Id);

        Assert.Contains(events.Value.Page.Items, item => item.PortalDelivery is
            { TransportEvidence: GuildPortalTransportEvidence.Ready, DeliveryState: GuildDeliveryState.Withheld, PeerReceiptId: null } evidence
            && evidence.RecipientQuestId == remote.Quest.Id);

        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM guild_continuations"));
    }

    private static async Task ConfirmRemotePortalTaskAsync(Fixture fixture, GuildQuestDriver driver, GuildQuestDto quest)
    {
        var dispatch = await fixture.Store.PrepareDispatchAsync(driver, quest.Id, quest.GoalRef.Digest, quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

        var sent = await fixture.Store.SendRemoteEffectAsync(driver, quest.Id, _ => Task.FromResult(new SendMessageResponse
        {
            Task = new() { Id = "remote-task", ContextId = "remote-context", Status = new() { State = TaskState.Working } },
        }), default);

        Assert.True(sent.IsSuccess, sent.Error.Message);

        var state = await fixture.Store.ReadRemotePortAsync(driver, quest.Id, default);

        Assert.True(state.IsSuccess, state.Error.Message);

        var confirmed = await fixture.Store.RecordDispatchEvidenceAsync(driver,
            new(quest.Id, state.Value.Dispatch!.Revision, GuildDispatchEvidence.Confirmed,
                RemoteTaskId: "remote-task", RemoteContextId: "remote-context"), default);

        Assert.True(confirmed.IsSuccess, confirmed.Error.Message);
    }

    private static GuildNativeMessageService NativePortalMessages(Fixture fixture) => new(fixture.Policy,
        new GuildPortalStore(fixture.Store, fixture.Authority, fixture.Policy, TimeProvider.System), fixture.Store,
        new ConclaveArchmage(null!, fixture.Settings, fixture.Store, new InboundScheduler()));

    private static async Task<(GuildCallerContext Owner, GuildQuestStored Quest)> IssueRemotePortalRecipientAsync(Fixture fixture, bool qualified)
    {
        Guid agent = await RegisterRemotePortalCardAsync(fixture, qualified);

        GuildCallerContext owner = await fixture.OperatorAsync();

        var root = fixture.Request with { AgentId = agent,
            Grant = fixture.Request.Grant with { Delegation = [GuildDelegationKind.Remote] },
            RemoteOptions = new(GuildRemoteDispatchMode.Continuable) };

        var issued = await fixture.Store.IssueAsync(owner,
            new(fixture.Generation, null, owner.InvocationId, owner.InvocationId, root.IdempotencyKey), root, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        return (owner, issued.Value);
    }

    private static async Task<Guid> RegisterRemotePortalCardAsync(Fixture fixture, bool qualified)
    {
        fixture.Settings.CurrentValue.Features.Conclave = true;

        fixture.Settings.CurrentValue.Features.A2AClient = true;

        fixture.Settings.CurrentValue.Cost.Budget.Enabled = false;

        const string discovery = "http://portal-peer.example/";

        fixture.Settings.CurrentValue.Integrations.A2A.AllowedRemoteAgents = [discovery];

        GuildRemoteTransportDescriptor target = new("portal-peer", discovery + "agent", "1.0", ["JSONRPC"], [], null, null, null,
            PortalMessagesV1: qualified);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Remote", "Remote peer", "1", [], ["text/plain"], ["text/plain"],
            new(false, false, false), new(GuildAgentSourceKind.Remote, null, target.PeerId, true, null));

        GuildStore roster = new(fixture.Connections, fixture.Gate, TimeProvider.System);

        var stored = await roster.UpsertCardAsync(new("portal-peer", null, null, null, new(card, "", [], target, discovery),
            new string('A', 64)), default);

        Assert.True(stored.IsSuccess, stored.Error.Message);

        return stored.Value.Payload.Card.AgentId;
    }

    private sealed class PortalTestCredential : IDisposable
    {
        private readonly string _name = $"ARCANUM_TEST_PORTAL_PEER_{Guid.NewGuid():N}";

        internal PortalTestCredential(Fixture fixture)
        {
            System.Environment.SetEnvironmentVariable(_name, Fixture.PeerCredential);

            fixture.Settings.CurrentValue.Integrations.A2A.OutboundCredentialEnvironmentVariable = _name;
        }

        public void Dispose() => System.Environment.SetEnvironmentVariable(_name, null);
    }
}
