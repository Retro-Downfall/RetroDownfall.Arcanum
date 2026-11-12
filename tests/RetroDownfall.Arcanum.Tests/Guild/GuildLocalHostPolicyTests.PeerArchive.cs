using System.Security.Cryptography;
using System.Text;
using A2A;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Backup;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Encrypted_restore_makes_real_peer_task_inert_and_keeps_uncertain_evidence()
    {
        using BackupRestoreServiceTests installation = new();

        BackupRestoreServiceTests.Fixture backup = await installation.CreateFixtureAsync();

        string connectionString;

        await using (SqliteConnection db = await BackupRestoreDatabaseWorker.OpenAsync(backup.DatabasePath, backup.GrimoireSecret, false, default))
        {
            connectionString = db.ConnectionString;

            await using SqliteCommand command = db.CreateCommand();

            command.CommandText = "UPDATE covenant_authority_state SET InstallationIdentity='12345678-1234-4234-8234-123456789ABC' WHERE StateKey=1";

            await command.ExecuteNonQueryAsync();
        }

        InboundOptionsMonitor settings = new(new() { Features = new() { Guild = true } });

        settings.CurrentValue.Features.A2AServer = true;

        settings.CurrentValue.Features.Conclave = true;

        settings.CurrentValue.Integrations.A2A.PushNotifications = true;

        settings.CurrentValue.Integrations.A2A.InboundPeers = [new()
        {
            PeerId = "alpha", Enabled = true, CredentialEnvironmentVariable = "INBOUND_ALPHA", CampaignId = null,
            Grant = new() { Workspaces = [], Tools = [], Memory = [], Budget = new()
                { Usd = new() { Mode = GuildBudgetMode.Unlimited }, Tokens = new() { Mode = GuildBudgetMode.Unlimited } },
                Delegation = [GuildDelegationKind.Local], ExpiresAtUtc = null }
        }];

        var cache = new ApiKeyDigestCache(TimeProvider.System);

        cache.StoreDigest(SHA256.HashData(Encoding.UTF8.GetBytes("master-test-key")), 300);

        using GuildInboundPeerRegistry peers = new(settings, cache, _ => Fixture.PeerCredential);

        GuildInboundPeer peer = Assert.IsType<GuildInboundPeer>(peers.Resolve(Fixture.PeerCredential));

        FixtureOrdinaryConnectionFactory connections = new(connectionString);

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        GuildLocalHostPolicy policy = new(connections, gate, settings, new(new KeySource()), TimeProvider.System, peers);

        using SignetAuthority peerAuthority = new(new(connections), gate, policy, TimeProvider.System);

        GuildQuestStore store = new(new(connections, gate, TimeProvider.System), peerAuthority, policy,
            new GuildBudgetLedger(), TimeProvider.System, new PeerIsolationAdmission(), new TestGuildLocalHostReadiness());

        GuildInboundHost host = new(policy, store, new ConclaveArchmage(null!, settings, store, new InboundScheduler()));

        var issued = await host.IssueAsync(peer, "peer-message", "peer-task", null, "peer private goal", default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        Assert.True((await host.ReadAsync(peer, "peer-task", true, default)).IsSuccess);

        await using (SqliteConnection db = await BackupRestoreDatabaseWorker.OpenAsync(backup.DatabasePath, backup.GrimoireSecret, false, default))
        {
            await using SqliteCommand command = db.CreateCommand();

            command.CommandText = """
                UPDATE guild_quests SET AccountingStateCode=2,Revision=Revision+1 WHERE Id=$quest;
                INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,EvidenceCode,Revision,CreatedAtUtc,UpdatedAtUtc)
                VALUES('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',$quest,zeroblob(32),0,'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,1,'2026-11-06T00:00:00.0000000Z','2026-11-06T00:00:00.0000000Z');
                """;

            command.Parameters.AddWithValue("$quest", issued.Value.Quest.Id.ToString("D").ToUpperInvariant());

            await command.ExecuteNonQueryAsync();
        }

        string archive = await backup.CreateBackupAsync("peer-archive.arcbackup");

        InMemoryOsCredentialStore credentials = new();

        BackupRestoreServiceOptions options = new()
        {
            RestoreStaging = new CovenantRestoreStagingServices(
                new CovenantRestoreStagingTests.RecordingExclusiveGate(), new CovenantRestoreStagingTests.RecordingRestoreMarkerLifecycle(),
                new BackupRestoreJournalAnchorStore(credentials, new BackupRestoreJournalKeyProvider(credentials),
                    new BackupRestoreJournalInstallationIdentityProvider(credentials)),
                new BackupRestoreJournalInstallationIdentityProvider(credentials), new BackupRestoreJournalKeyProvider(credentials),
                new BackupRestoreEffectDigestCalculator()),
        };

        BackupRestoreResult result = await installation.Restore(new BackupRestoreServiceTests.RecordingSecretStore { GrimoireSecret = backup.GrimoireSecret }, options)
            .RestoreAsync(new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false,
                    ProtectedStateMode: BackupProtectedStateMode.PurgeProtectedState, ProtectedStateConfirmed: true),
                BackupRestoreServiceTests.Passphrase.AsMemory(), default);

        Assert.True(result.Status == BackupRestoreStatus.Completed,
            $"Restore status {result.Status}: {string.Join("; ", result.Issues.Select(issue => issue.Code))}");

        GuildLocalHostPolicy restartedPolicy = new(connections, gate, settings, new(new KeySource()), TimeProvider.System, peers);

        using SignetAuthority restartedAuthority = new(new(connections), gate, restartedPolicy, TimeProvider.System);

        GuildQuestStore restartedStore = new(new(connections, gate, TimeProvider.System), restartedAuthority, restartedPolicy,
            new GuildBudgetLedger(), TimeProvider.System, new PeerIsolationAdmission(), new TestGuildLocalHostReadiness());

        ArchiveScheduler scheduler = new();

        GuildInboundHost restartedHost = new(restartedPolicy, restartedStore,
            new ConclaveArchmage(null!, settings, restartedStore, scheduler));

        Assert.True((await restartedHost.ReadAsync(peer, "peer-task", true, default)).IsFailure);

        Assert.True((await restartedHost.ContinueAsync(peer, "peer-task", "answer", "private answer", default)).IsFailure);

        bool disclosed = false;

        Assert.True((await restartedHost.AdmitDisclosureAsync(peer, "peer-task", _ => { disclosed = true; return Task.FromResult(true); }, default)).IsFailure);

        Assert.False(disclosed);

        ServiceCollection services = new();

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(settings);

        services.AddSingleton(peers);

        services.AddSingleton<GuildInboundRequestContext>();

        services.AddSingleton(restartedHost);

        await using ServiceProvider provider = services.BuildServiceProvider();

        var scopes = provider.GetRequiredService<IServiceScopeFactory>();

        PeerIsolationDns dns = new();

        A2APushNotificationRegistry notifications = new(settings, dns, NullLogger<A2APushNotificationRegistry>.Instance);

        await using ArcanumA2AAgentHandler handler = new(scopes, settings, NullLogger<ArcanumA2AAgentHandler>.Instance, notifications);

        ArcanumA2AServer server = new(handler, new ArcanumA2ATaskStore(scopes, NullLogger<ArcanumA2ATaskStore>.Instance), new(),
            notifications, NullLogger<A2AServer>.Instance, new(), scopes, settings);

        using (provider.GetRequiredService<GuildInboundRequestContext>().Enter(peer))
        {
            await Assert.ThrowsAsync<A2AException>(() => ProbePeerTaskAsync(server, "push-set", "peer-task", default));

            await Assert.ThrowsAsync<A2AException>(() => ProbePeerTaskAsync(server, "get", "peer-task", default));
        }

        Assert.Equal(0, dns.Requests);

        Assert.Null(notifications.Resolve("peer-task"));

        var caller = await restartedPolicy.CreatePeerCallerAsync(peer, Guid.NewGuid(), default);

        Assert.True(caller.IsSuccess, caller.Error.Message);

        var recovery = await restartedPolicy.CreateRecoveryAsync(caller.Value, issued.Value.Quest.Id,
            issued.Value.Quest.OperationId, issued.Value.Quest.SignetRevision, default);

        Assert.True(recovery.IsFailure || (await restartedAuthority.RebindAsync(recovery.Value, default)).IsFailure);

        Assert.Equal(0, scheduler.Calls);

        var fresh = await restartedHost.IssueAsync(peer, "fresh-message", "fresh-task", null, "fresh goal", default);

        Assert.True(fresh.IsSuccess, fresh.Error.Message);

        Assert.True((await restartedHost.ReadAsync(peer, "fresh-task", true, default)).IsSuccess);

        using (provider.GetRequiredService<GuildInboundRequestContext>().Enter(peer))
        {
            await ProbePeerTaskAsync(server, "push-set", "fresh-task", default);
        }

        Assert.True(dns.Requests > 0);

        Assert.NotNull(notifications.Resolve("fresh-task"));

        await using SqliteConnection restored = await BackupRestoreDatabaseWorker.OpenAsync(backup.DatabasePath, backup.GrimoireSecret, false, default);

        await using SqliteCommand probe = restored.CreateCommand();

        probe.CommandText = "SELECT count(*) FROM guild_signets WHERE PeerId='alpha' AND RestoredInert=1";

        Assert.Equal(1L, (long)(await probe.ExecuteScalarAsync())!);

        probe.CommandText = "SELECT count(*) FROM guild_dispatches WHERE DispatchId='EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE' AND EvidenceCode=2";

        Assert.Equal(1L, (long)(await probe.ExecuteScalarAsync())!);

        probe.CommandText = "SELECT count(*) FROM guild_quests WHERE Id=$quest AND AccountingStateCode=2";

        probe.Parameters.AddWithValue("$quest", issued.Value.Quest.Id.ToString("D").ToUpperInvariant());

        Assert.Equal(1L, (long)(await probe.ExecuteScalarAsync())!);
    }
    private sealed class ArchiveScheduler : IGuildLocalQuestScheduler
    {
        internal int Calls;

        public bool IsAvailable => true;

        public void Schedule(GuildQuestDto quest) => Calls++;
    }
}
