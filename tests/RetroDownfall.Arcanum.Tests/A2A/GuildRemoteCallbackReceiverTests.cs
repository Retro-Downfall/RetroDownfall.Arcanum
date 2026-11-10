using A2A;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Remote_callback_receipt_wakes_same_durable_quest_without_settlement_or_lease_takeover()
    {
        await using Fixture f = await Fixture.CreateAsync();

        RemoteCallbackSetup bound = await PrepareRemoteCallbackAsync(f);

        RemoteCallbackScheduler scheduler = new();

        GuildRemoteCallbackReceiver receiver = new(f.Store, f.Policy, scheduler);

        var accepted = await receiver.AcceptAsync(bound.ConfigId, bound.Token, default);

        Assert.True(accepted.IsSuccess && accepted.Value, accepted.Error.Message);

        var replay = await receiver.AcceptAsync(bound.ConfigId, bound.Token, default);

        Assert.True(replay.IsSuccess && replay.Value, replay.Error.Message);

        Assert.Single(scheduler.Wakes.Distinct());

        Assert.All(scheduler.Wakes, id => Assert.Equal(bound.Quest.Id, id));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_remote_callbacks WHERE CallbackReceivedAtUtc IS NOT NULL"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE LeaseOwner='callback-receiver-test'"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_dispatches"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-secret")]
    public async Task Remote_callback_wrong_token_never_wakes_or_records_receipt(string? token)
    {
        await using Fixture f = await Fixture.CreateAsync();

        RemoteCallbackSetup bound = await PrepareRemoteCallbackAsync(f);

        RemoteCallbackScheduler scheduler = new();

        var result = await new GuildRemoteCallbackReceiver(f.Store, f.Policy, scheduler).AcceptAsync(bound.ConfigId, token, default);

        Assert.True(result.IsFailure);

        Assert.Empty(scheduler.Wakes);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_remote_callbacks WHERE CallbackReceivedAtUtc IS NOT NULL"));
    }

    [Fact]
    public async Task Remote_callback_unknown_config_allows_independent_legacy_lookup()
    {
        await using Fixture f = await Fixture.CreateAsync();

        RemoteCallbackScheduler scheduler = new();

        var result = await new GuildRemoteCallbackReceiver(f.Store, f.Policy, scheduler)
            .AcceptAsync(Guid.NewGuid().ToString("N"), A2ACallbackToken.Mint(), default);

        Assert.True(result.IsSuccess);

        Assert.False(result.Value);

        Assert.Empty(scheduler.Wakes);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("restored")]
    [InlineData("erased")]
    [InlineData("owner-deleted")]
    public async Task Remote_callback_current_authority_and_source_close_before_receipt(string boundary)
    {
        await using Fixture f = await Fixture.CreateAsync();

        RemoteCallbackSetup bound = await PrepareRemoteCallbackAsync(f);

        if (boundary == "disabled") f.Settings.CurrentValue.Features.Guild = false;
        else if (boundary == "restored")
            await f.ExecuteAsync("UPDATE guild_signets SET RestoredInert=1,Revision=Revision+1");
        else if (boundary == "erased")
            await f.ExecuteAsync("INSERT INTO guild_content_erasure_receipts(ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,ByteLength,ErasedAtUtc) SELECT c.ContentId,c.SessionId,c.CampaignId,c.ContentKindCode,c.SourceRecordId,c.Revision,c.Digest,c.MimeType,length(c.Body),'2026-11-10T00:00:00.0000000Z' FROM guild_content c JOIN guild_remote_executions r ON r.InputContentId=c.ContentId");
        else
        {
            var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

            Assert.True(opened.IsSuccess, opened.Error.Message);

            await using var lease = opened.Value;

            using var deletion = RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteConnectionInitializer.Instance
                .Authorize(lease.Connection, RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteAuthorizationKind.SessionRetention);

            await using var command = lease.Connection.CreateCommand();

            command.CommandText = "DELETE FROM Sessions";

            _ = await command.ExecuteNonQueryAsync();
        }

        RemoteCallbackScheduler scheduler = new();

        var result = await new GuildRemoteCallbackReceiver(f.Store, f.Policy, scheduler).AcceptAsync(bound.ConfigId, bound.Token, default);

        Assert.True(result.IsFailure);

        Assert.Empty(scheduler.Wakes);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_remote_callbacks WHERE CallbackReceivedAtUtc IS NOT NULL"));
    }

    [Fact]
    public async Task Remote_callback_without_optional_Guild_schema_allows_legacy_fallback()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using (var connection = await file.OpenAsync(default))
        {
            await using var command = connection.CreateCommand();

            command.CommandText = "CREATE TABLE legacy_callback_fixture(Id INTEGER PRIMARY KEY)";

            _ = await command.ExecuteNonQueryAsync();
        }

        var connections = new RetroDownfall.Arcanum.Tests.Support.FixtureOrdinaryConnectionFactory(file.ConnectionString);

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        InboundOptionsMonitor settings = new(new());

        var policy = new GuildLocalHostPolicy(connections, gate, settings,
            new RetroDownfall.Arcanum.Infrastructure.Tower.PhysicalCampaignRootOpener(new KeySource()), TimeProvider.System);

        using SignetAuthority authority = new(new(connections), gate, policy, TimeProvider.System);

        GuildQuestStore quests = new(new(connections, gate, TimeProvider.System), authority, policy, new GuildBudgetLedger(), TimeProvider.System);

        var result = await new GuildRemoteCallbackReceiver(quests, policy, new RemoteCallbackScheduler())
            .AcceptAsync(Guid.NewGuid().ToString("N"), A2ACallbackToken.Mint(), default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value);
    }

    [Fact]
    public async Task Remote_callback_missing_table_in_declared_Guild_schema_refuses_legacy_fallback()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync("DROP TABLE guild_remote_effects");

        var result = await new GuildRemoteCallbackReceiver(f.Store, f.Policy, new RemoteCallbackScheduler())
            .AcceptAsync(Guid.NewGuid().ToString("N"), A2ACallbackToken.Mint(), default);

        Assert.True(result.IsFailure);
    }

    private sealed record RemoteCallbackSetup(GuildQuestDto Quest, string ConfigId, string Token);

    private sealed class RemoteCallbackScheduler : IGuildLocalQuestScheduler
    {
        internal List<Guid> Wakes { get; } = [];

        public bool IsAvailable => true;

        public void Schedule(GuildQuestDto quest) => Wakes.Add(quest.Id);
    }

    private static async Task<RemoteCallbackSetup> PrepareRemoteCallbackAsync(Fixture f)
    {
        f.Settings.CurrentValue.Features.A2AClient = true;

        f.Settings.CurrentValue.Features.Conclave = true;

        GuildRemoteTransportDescriptor target = new("callback-peer", "https://callback-peer.example/agent", "1.0", ["JSONRPC"], [], null, null, null);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Callback peer", "Remote callback fixture", "1", [], ["text/plain"], ["text/plain"],
            new(false, true, false), new(GuildAgentSourceKind.Remote, null, target.PeerId, true, null));

        GuildStore roster = new(f.Connections, f.Gate, TimeProvider.System);

        var stored = await roster.UpsertCardAsync(new("callback-peer", null, null, null,
            new(card, "", [], target, "https://callback-peer.example/"), new string('A', 64)), default);

        Assert.True(stored.IsSuccess, stored.Error.Message);

        var caller = await f.OperatorAsync();

        var request = f.Request with { AgentId = stored.Value.Payload.Card.AgentId,
            Grant = f.Request.Grant with { Delegation = [GuildDelegationKind.Remote] },
            RemoteOptions = new(GuildRemoteDispatchMode.Callback) };

        var issued = await f.Store.IssueAsync(caller,
            new(f.Generation, null, caller.InvocationId, caller.InvocationId, request.IdempotencyKey), request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestDto quest = issued.Value.Quest;

        var recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        var run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, Guid.NewGuid(), default);

        Assert.True(run.IsSuccess, run.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var leased = await operations.TryAcquireLeaseAsync(quest.OperationId, "callback-receiver-test", now, now.AddMinutes(5));

        Assert.True(leased.Acquired);

        GuildQuestDriver driver = new(execution.Value, "callback-receiver-test", leased.Operation.Revision, run.Value);

        var prepared = await f.Store.PrepareDispatchAsync(driver, quest.Id, quest.GoalRef.Digest, quest.RunId, false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        var sent = await f.Store.SendRemoteEffectAsync(driver, quest.Id, _ => Task.FromResult(new SendMessageResponse
        {
            Task = new() { Id = "remote-task", ContextId = "remote-context", Status = new() { State = TaskState.Working } },
        }), default);

        Assert.True(sent.IsSuccess, sent.Error.Message);

        var state = await f.Store.ReadRemotePortAsync(driver, quest.Id, default);

        Assert.True(state.IsSuccess, state.Error.Message);

        var evidence = await f.Store.RecordDispatchEvidenceAsync(driver,
            new(quest.Id, state.Value.Dispatch!.Revision, GuildDispatchEvidence.Confirmed, RemoteTaskId: "remote-task", RemoteContextId: "remote-context"), default);

        Assert.True(evidence.IsSuccess, evidence.Error.Message);

        string configId = Guid.NewGuid().ToString("N");

        string token = A2ACallbackToken.Mint();

        var registered = await f.Store.RecordRemoteCallbackAsync(driver, configId, A2ACallbackToken.Hash(token), default);

        Assert.True(registered.IsSuccess, registered.Error.Message);

        return new(quest, configId, token);
    }
}
