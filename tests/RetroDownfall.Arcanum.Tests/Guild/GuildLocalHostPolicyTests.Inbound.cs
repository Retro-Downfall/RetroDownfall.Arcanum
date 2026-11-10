using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Peer_root_commits_its_task_binding_and_exact_grant_with_the_canonical_session()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = Assert.IsType<GuildInboundPeer>(f.InboundPeers!.Resolve(Fixture.PeerCredential));

        Result<GuildCallerContext> caller = await f.Policy.CreatePeerCallerAsync(peer, Guid.NewGuid(), default);

        Assert.True(caller.IsSuccess, caller.Error.Message);

        GuildIssueRootRequest request = f.Request with { Grant = peer.Grant };

        GuildInboundIssue input = new(peer, "task-alpha", "message-alpha", "context-alpha", new string('1', 64));

        var accepted = await f.Store.IssueInboundAsync(caller.Value,
            new(f.Generation, null, caller.Value.InvocationId, caller.Value.InvocationId, request.IdempotencyKey), request, input, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_inbound_tasks"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM Sessions"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_local_executions"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_signets WHERE PeerId='alpha'"));

        Assert.True((await f.Store.GetAsync(caller.Value, accepted.Value.Quest.Id, default)).IsSuccess);

        GuildCallerContext forged = new(peer.PeerId, peer.GrantRevision, caller.Value.InvocationId, f.Generation);

        Assert.False(f.Policy.Authenticates(forged));

        Assert.True((await f.Store.GetAsync(forged, accepted.Value.Quest.Id, default)).IsFailure);
    }

    [Fact]
    public async Task Inbound_binding_failure_rolls_back_session_signet_budget_and_local_execution()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = Assert.IsType<GuildInboundPeer>(f.InboundPeers!.Resolve(Fixture.PeerCredential));

        var caller = await f.Policy.CreatePeerCallerAsync(peer, Guid.NewGuid(), default);

        Assert.True(caller.IsSuccess, caller.Error.Message);

        await f.ExecuteAsync("CREATE TRIGGER inbound_fixture_fail BEFORE INSERT ON guild_inbound_tasks BEGIN SELECT RAISE(ABORT,'fixture'); END;");

        var accepted = await f.Store.IssueInboundAsync(caller.Value,
            new(f.Generation, null, caller.Value.InvocationId, caller.Value.InvocationId, f.Request.IdempotencyKey),
            f.Request with { Grant = peer.Grant }, new(peer, "task", "message", "context", new string('1', 64)), default);

        Assert.True(accepted.IsFailure);

        foreach (string table in new[] { "Sessions", "guild_quests", "guild_signets", "guild_budget_accounts", "guild_local_executions", "guild_inbound_tasks" })
            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM " + table));
    }
    [Fact]
    public async Task Inbound_host_replays_one_task_and_conflicts_on_changed_goal_without_new_owner_or_authority()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        InboundScheduler scheduler = new();

        GuildInboundHost host = new(f.Policy, f.Store, new(null!, f.Settings, f.Store, scheduler));

        var first = await host.IssueAsync(peer, "message", "task-one", null, "Compute", default);

        Assert.True(first.IsSuccess, first.Error.Message);

        var replay = await host.IssueAsync(peer, "message", "task-two", null, "Compute", default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(first.Value.Quest.Id, replay.Value.Quest.Id);

        Assert.Equal("task-one", replay.Value.Binding.TaskId);

        Assert.True(replay.Value.Quest.Receipt!.Replayed);

        var conflict = await host.IssueAsync(peer, "message", "task-three", null, "Different goal", default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, conflict.Error.Code);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM Sessions"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_signets"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_local_executions"));
    }

    [Fact]
    public async Task Peer_disable_commits_canonical_cancellation_and_revocation_before_reload_returns_and_reenable_never_revives_root()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        InboundScheduler scheduler = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, scheduler);

        GuildInboundHost host = new(f.Policy, f.Store, coordinator);

        var first = await host.IssueAsync(peer, "message", "task", null, "Compute", default);

        Assert.True(first.IsSuccess, first.Error.Message);

        ServiceCollection services = new();

        services.AddSingleton(f.Store);

        services.AddSingleton(coordinator);

        await using ServiceProvider provider = services.BuildServiceProvider();

        using GuildInboundPeerReloadService listener = new(f.InboundPeers, f.Policy,
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<GuildInboundPeerReloadService>.Instance, f.Gate, f.Settings);

        await listener.StartAsync(default);

        ReloadInbound(f, value => value.Enabled = false);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quest_cancellations"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_signets WHERE Revoked=1 AND CancellationRequested=1"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE State=3"));

        ReloadInbound(f, value => value.Enabled = true);

        GuildInboundPeer enabled = f.InboundPeers.Resolve(Fixture.PeerCredential)!;

        Assert.True((await host.ReadAsync(enabled, "task", false, default)).IsFailure);

        var next = await host.IssueAsync(enabled, "next-message", "next-task", null, "Compute new work", default);

        Assert.True(next.IsSuccess, next.Error.Message);

        Assert.NotEqual(first.Value.Quest.Id, next.Value.Quest.Id);

        await listener.StopAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_rotation_preserves_live_task_ownership_but_old_reference_cannot_read(bool journal)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true, denialJournal: journal);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        GuildInboundHost host = new(f.Policy, f.Store, new(null!, f.Settings, f.Store, new InboundScheduler()));

        var first = await host.IssueAsync(peer, "message", "task", null, "Compute", default);

        Assert.True(first.IsSuccess, first.Error.Message);

        f.PeerCredentialValue = Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        ReloadInbound(f, _ => { });

        GuildInboundPeer rotated = f.InboundPeers.Resolve(f.PeerCredentialValue)!;

        Assert.Null(f.InboundPeers.Resolve(Fixture.PeerCredential));

        Assert.True((await host.ReadAsync(peer, "task", false, default)).IsFailure);

        var current = await host.ReadAsync(rotated, "task", false, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Assert.Equal(first.Value.Quest.Id, current.Value.Quest.Id);

        Assert.True((await f.Policy.ReconcileInboundPeersAsync(default)).IsSuccess);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_signets WHERE Revoked=1"));

        if (journal) Assert.False(Directory.Exists(f.DenialPath));
    }

    [Fact]
    public async Task Inbound_task_listing_rehydrates_durable_bindings_and_advances_with_a_bounded_page()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        GuildInboundHost host = new(f.Policy, f.Store, new(null!, f.Settings, f.Store, new InboundScheduler()));

        Assert.True((await host.IssueAsync(peer, "message-a", "task-a", null, "Compute", default)).IsSuccess);

        Assert.True((await host.IssueAsync(peer, "message-b", "task-b", null, "Compute", default)).IsSuccess);

        var first = await host.ListAsync(peer, null, 1, false, default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal("task-a", Assert.Single(first.Value.Items).Binding.TaskId);

        Assert.Equal("task-a", first.Value.NextTaskId);

        var next = await host.ListAsync(peer, first.Value.NextTaskId, 1, false, default);

        Assert.True(next.IsSuccess, next.Error.Message);

        Assert.Equal("task-b", Assert.Single(next.Value.Items).Binding.TaskId);
    }

    [Fact]
    public async Task Inbound_goal_materialization_retains_parent_authority_instead_of_operator_authority()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        GuildInboundHost host = new(f.Policy, f.Store, new(null!, f.Settings, f.Store, new InboundScheduler()));

        var issue = await host.IssueAsync(peer, "message", "task", null, "Remote untrusted instruction", default);

        Assert.True(issue.IsSuccess, issue.Error.Message);

        GuildCallerContext owner = await f.OperatorAsync();

        var quest = await f.Store.GetAsync(owner, issue.Value.Quest.Id, default);

        Assert.True(quest.IsSuccess, quest.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, quest.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        var materialized = await f.Store.MaterializeProviderInputAsync(execution, input.Value, default);

        Assert.True(materialized.IsSuccess, materialized.Error.Message);

        Assert.Equal(GuildAuthorityKind.Parent, materialized.Value.GoalAuthority);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_changed_or_removed_peer_persists_denial_before_another_restart_can_restore_old_grant(bool remove)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        GuildInboundHost host = new(f.Policy, f.Store, new(null!, f.Settings, f.Store, new InboundScheduler()));

        var issue = await host.IssueAsync(peer, "message", "task", null, "Compute", default);

        Assert.True(issue.IsSuccess, issue.Error.Message);

        ArcanumSettings changed = CloneInboundSettings(f.Settings.CurrentValue);

        if (remove) changed.Integrations.A2A.InboundPeers = [];
        else changed.Integrations.A2A.InboundPeers[0].Grant!.Delegation = [];

        InboundOptionsMonitor restartedSettings = new(changed);

        var cache = new RetroDownfall.Arcanum.Infrastructure.Security.ApiKeyDigestCache(TimeProvider.System);

        cache.StoreDigest(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("master-test-key")), 300);

        using GuildInboundPeerRegistry restartedPeers = new(restartedSettings, cache, _ => Fixture.PeerCredential);

        GuildLocalHostPolicy restarted = new(f.Connections, f.Gate, restartedSettings, f.Opener, TimeProvider.System, restartedPeers);

        Assert.True((await restarted.InitializeIdentityAsync(default)).IsSuccess);

        Assert.True((await restarted.ReconcileInboundPeersAsync(default)).IsSuccess);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_signets WHERE Revoked=1"));

        restartedSettings.CurrentValue = CloneInboundSettings(f.Settings.CurrentValue);

        GuildInboundPeer readded = Assert.IsType<GuildInboundPeer>(restartedPeers.Resolve(Fixture.PeerCredential));

        Assert.True(readded.AuthorityVersion > 0);

        var caller = await restarted.CreatePeerCallerAsync(readded, Guid.NewGuid(), default);

        Assert.True(caller.IsSuccess, caller.Error.Message);

        using SignetAuthority restartedAuthority = new(new(f.Connections), f.Gate, restarted, TimeProvider.System);

        GuildQuestStore restartedStore = new(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority,
            restarted, new GuildBudgetLedger(), TimeProvider.System);

        Assert.True((await restartedStore.GetAsync(caller.Value, issue.Value.Quest.Id, default)).IsFailure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(101)]
    public async Task New_issue_after_grant_change_does_not_hide_old_root_from_durable_cancellation_sweep(int newerRoots)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        GuildInboundHost host = new(f.Policy, f.Store, new(null!, f.Settings, f.Store, new InboundScheduler()));

        var first = await host.IssueAsync(peer, "old-message", "z-old-task", null, "Compute", default);

        Assert.True(first.IsSuccess, first.Error.Message);

        ReloadInbound(f, value => value.Grant!.Delegation = []);

        GuildInboundPeer changed = f.InboundPeers.Resolve(Fixture.PeerCredential)!;

        for (int i = 0; i < newerRoots; i++)
        {
            var next = await host.IssueAsync(changed, "new-message-" + i, "a-new-task-" + i.ToString("D3"), null, "Compute", default);

            Assert.True(next.IsSuccess, next.Error.Message);
        }

        Assert.True((await f.Policy.ReconcileInboundPeersAsync(default)).IsSuccess);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_signets WHERE Revoked=1"));

        Assert.True((await host.ReadAsync(changed, "z-old-task", false, default)).IsFailure);

        Assert.True((await host.ReadAsync(changed, "a-new-task-000", false, default)).IsSuccess);
    }

    [Fact]
    public async Task Guild_disable_preserves_task_classification_but_closes_peer_disclosure()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        GuildInboundHost host = new(f.Policy, f.Store, new(null!, f.Settings, f.Store, new InboundScheduler()));

        Assert.True((await host.IssueAsync(peer, "message", "task", null, "Compute", default)).IsSuccess);

        f.Settings.CurrentValue.Features.Guild = false;

        Assert.True((await host.IsGuildTaskAsync("task", default)).Value);

        Assert.True((await host.ReadAsync(peer, "task", true, default)).IsFailure);
    }

    [Fact]
    public async Task Inbound_cancellation_replay_reuses_the_accepted_CAS_before_reading_current_revision()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        GuildInboundHost host = new(f.Policy, f.Store, new(null!, f.Settings, f.Store, new InboundScheduler()));

        var issued = await host.IssueAsync(peer, "message", "task", null, "Compute", default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        var first = await host.CancelAsync(peer, "task", default);

        Assert.True(first.IsSuccess, first.Error.Message);

        var replay = await host.CancelAsync(peer, "task", default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(first.Value.Quest.Id, replay.Value.Quest.Id);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quest_cancellations"));
    }

    [Fact]
    public async Task Peer_reload_shutdown_joins_a_callback_that_already_admitted_durable_work()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        InboundScheduler scheduler = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, scheduler);

        GuildInboundHost host = new(f.Policy, f.Store, coordinator);

        Assert.True((await host.IssueAsync(peer, "message", "task", null, "Compute", default)).IsSuccess);

        ServiceCollection services = new();

        services.AddSingleton(f.Store);

        services.AddSingleton(coordinator);

        await using ServiceProvider provider = services.BuildServiceProvider();

        var gate = new RetroDownfall.Arcanum.Tests.Support.RecordingGrimoireWorkAdmissionGate(f.Gate);

        using GuildInboundPeerReloadService listener = new(f.InboundPeers, f.Policy,
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<GuildInboundPeerReloadService>.Instance, gate, f.Settings);

        await listener.StartAsync(default);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using ManualResetEventSlim release = new();

        gate.BeforeEffectGroupAdmission = () => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };

        Task callback = Task.Run(() => ReloadInbound(f, value => value.Enabled = false));

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task stopping = listener.StopAsync(default);

        bool returnedBeforeCallback;

        try
        {
            await Task.Delay(30);

            returnedBeforeCallback = stopping.IsCompleted;
        }
        finally
        {
            release.Set();

            await callback;

            await stopping;
        }

        Assert.False(returnedBeforeCallback);

        int admitted = gate.EffectGroupAttempts;

        ReloadInbound(f, value => value.Enabled = true);

        Assert.Equal(admitted, gate.EffectGroupAttempts);
    }

    [Fact]
    public async Task Failed_peer_denial_marker_publication_keeps_the_credential_snapshot_unavailable()
    {
        await using Fixture f = await Fixture.CreateAsync(inboundPeers: true, denialJournal: true, unsafeJournal: true);

        Assert.Null(f.InboundPeers!.Resolve(Fixture.PeerCredential));

        Assert.Equal("operator bytes", File.ReadAllText(f.DenialPath));
    }

    [Fact]
    public async Task Durable_denial_survives_database_failure_crash_and_reverted_peer_grant()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true, denialJournal: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new InboundScheduler());

        GuildInboundHost host = new(f.Policy, f.Store, coordinator);

        var issue = await host.IssueAsync(peer, "message", "task", null, "Compute", default);

        Assert.True(issue.IsSuccess, issue.Error.Message);

        ArcanumSettings original = CloneInboundSettings(f.Settings.CurrentValue);

        ServiceCollection services = new();

        services.AddSingleton(f.Store);

        services.AddSingleton(coordinator);

        await using ServiceProvider provider = services.BuildServiceProvider();

        using GuildInboundPeerReloadService listener = new(f.InboundPeers, f.Policy,
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<GuildInboundPeerReloadService>.Instance, f.Gate, f.Settings);

        await listener.StartAsync(default);

        await f.ExecuteAsync("CREATE TRIGGER peer_denial_database_fault BEFORE UPDATE ON guild_quests BEGIN SELECT RAISE(ABORT,'fixture'); END;");

        ReloadInbound(f, value => value.Enabled = false);

        Assert.True(f.DenialJournal!.IsDenied("alpha"));

        await listener.StopAsync(default);

        f.InboundPeers.Dispose();

        f.DenialJournal.Dispose();

        f.Settings.CurrentValue = original;

        await f.ExecuteAsync("DROP TRIGGER peer_denial_database_fault");

        var cache = new RetroDownfall.Arcanum.Infrastructure.Security.ApiKeyDigestCache(TimeProvider.System);

        cache.StoreDigest(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("master-test-key")), 300);

        using GuildInboundPeerDenialJournal restartedJournal = new(f.DenialPath, f.Gate);

        using GuildInboundPeerRegistry restartedPeers = new(f.Settings, cache, _ => Fixture.PeerCredential, restartedJournal);

        Assert.Null(restartedPeers.Resolve(Fixture.PeerCredential));

        GuildLocalHostPolicy restarted = new(f.Connections, f.Gate, f.Settings, f.Opener, TimeProvider.System, restartedPeers);

        Assert.True((await restarted.InitializeIdentityAsync(default)).IsSuccess);

        Assert.True((await restarted.ReconcileInboundPeersAsync(default)).IsSuccess);

        // A denial discovered during a pass may require a second bounded cleanup pass.
        Assert.True((await restarted.ReconcileInboundPeersAsync(default)).IsSuccess);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_signets WHERE Revoked=1"));

        Assert.False(restartedJournal.HasPending);

        GuildInboundPeer enabled = Assert.IsType<GuildInboundPeer>(restartedPeers.Resolve(Fixture.PeerCredential));

        using SignetAuthority authority = new(new(f.Connections), f.Gate, restarted, TimeProvider.System);

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), authority, restarted,
            new GuildBudgetLedger(), TimeProvider.System);

        var caller = await restarted.CreatePeerCallerAsync(enabled, Guid.NewGuid(), default);

        Assert.True(caller.IsSuccess, caller.Error.Message);

        Assert.True((await store.GetAsync(caller.Value, issue.Value.Quest.Id, default)).IsFailure);
    }

    [Fact]
    public async Task Peer_task_disclosure_rechecks_the_installation_authority_epoch()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        GuildInboundHost host = new(f.Policy, f.Store, new(null!, f.Settings, f.Store, new InboundScheduler()));

        Assert.True((await host.IssueAsync(peer, "message", "task", null, "Compute", default)).IsSuccess);

        await f.ExecuteAsync("UPDATE guild_signet_authority_state SET Epoch='AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA'");

        Assert.True((await host.ReadAsync(peer, "task", false, default)).IsFailure);
    }

    [Fact]
    public async Task Peer_answer_receipt_rehydrates_the_authenticated_parent_attribution()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true, inboundPeers: true);

        GuildInboundPeer peer = f.InboundPeers!.Resolve(Fixture.PeerCredential)!;

        GuildInboundHost host = new(f.Policy, f.Store, new(null!, f.Settings, f.Store, new InboundScheduler()));

        var issue = await host.IssueAsync(peer, "message", "task", null, "Compute", default);

        Assert.True(issue.IsSuccess, issue.Error.Message);

        GuildCallerContext owner = await f.OperatorAsync();

        var stored = await f.Store.GetAsync(owner, issue.Value.Quest.Id, default);

        var execution = await ToolExecutionAsync(f, owner, stored.Value);

        Guid continuation = Guid.NewGuid();

        var waited = await f.Store.WaitForInputAsync(execution.Driver, Guid.NewGuid(), continuation, default);

        Assert.True(waited.IsSuccess, waited.Error.Message);

        var answered = await host.ContinueAsync(peer, "task", "answer-message", "Answer", default);

        Assert.True(answered.IsSuccess, answered.Error.Message);

        var receipt = await f.Store.GetContinuationAsync(owner, issue.Value.Quest.Id, continuation, default);

        Assert.True(receipt.IsSuccess, receipt.Error.Message);

        Assert.Equal(GuildAuthorityKind.Parent, receipt.Value.Authority);

        Assert.Equal(peer.PeerId, receipt.Value.PeerId);

        Assert.Equal(peer.GrantRevision, receipt.Value.PeerGrantRevision);

        Assert.Null(receipt.Value.Producer);
    }

    private static ArcanumSettings CloneInboundSettings(ArcanumSettings current) => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(current, ConfigurationJsonContext.Default.ArcanumSettings),
        ConfigurationJsonContext.Default.ArcanumSettings)!;

    private static void ReloadInbound(Fixture fixture, Action<A2AInboundPeerSettings> change)
    {
        ArcanumSettings clone = JsonSerializer.Deserialize(JsonSerializer.Serialize(fixture.Settings.CurrentValue,
            ConfigurationJsonContext.Default.ArcanumSettings), ConfigurationJsonContext.Default.ArcanumSettings)!;

        change(clone.Integrations.A2A.InboundPeers[0]);

        fixture.Settings.CurrentValue = clone;
    }

    private sealed class InboundScheduler : IGuildLocalQuestScheduler
    {
        public bool IsAvailable => true;

        public void Schedule(GuildQuestDto quest) { }
    }

    private sealed class InboundOptionsMonitor(ArcanumSettings value) : IOptionsMonitor<ArcanumSettings>
    {
        private ArcanumSettings _current = value;

        private event Action<ArcanumSettings, string?>? Changed;

        public ArcanumSettings CurrentValue
        {
            get => _current;

            set { _current = value; Changed?.Invoke(value, null); }
        }

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ArcanumSettings, string?> listener)
        {
            Changed += listener;

            return new Subscription(() => Changed -= listener);
        }

        private sealed class Subscription(Action remove) : IDisposable
        {
            public void Dispose() => remove();
        }
    }

}
