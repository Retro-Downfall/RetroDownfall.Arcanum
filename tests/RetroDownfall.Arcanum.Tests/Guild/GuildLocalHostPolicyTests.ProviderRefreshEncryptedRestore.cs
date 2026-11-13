using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    internal static async Task<Guid> AdmitRealRefreshForEncryptedRestoreAsync(string connectionString, Guid sessionId)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        string workspace = Directory.CreateTempSubdirectory("source-refresh-").FullName;

        try
        {
            FixtureOrdinaryConnectionFactory connections = new(connectionString);

            GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

            InboundOptionsMonitor settings = new(new() { Features = new() { Guild = true } });

            ProviderSettings provider = new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = ["provider/model"], GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration };

            settings.CurrentValue.Providers = [provider];

            settings.CurrentValue.Cost.Budget.Enabled = true;

            settings.CurrentValue.Cost.Budget.DailyLimitUsd = 10m;

            settings.CurrentValue.Cost.Pricing.DefaultPricing = new() { InputPer1M = 1m, OutputPer1M = 1m };

            GuildLocalHostPolicy policy = new(connections, gate, settings, new(new KeySource()), TimeProvider.System, null);

            using SignetAuthority authority = new(new(connections), gate, policy, TimeProvider.System);

            GuildSpendRefreshTracker tracker = new();

            await using GuildLocalHostReadiness readiness = new(settings, connections, policy, authority, TimeProvider.System, Path.Combine(workspace, "host.lock"), gate, tracker);

            var ready = await readiness.EnsureReadyAsync(default);

            Assert.True(ready.IsSuccess, ready.IsFailure ? ready.Error.Message : null);

            var callerResult = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

            Assert.True(callerResult.IsSuccess, callerResult.IsFailure ? callerResult.Error.Message : null);

            GuildCallerContext caller = callerResult.Value;

            await using var opened = (await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default)).Value;

            await using ArcanumDbContext db = GrimoireFixture.CreateContext(opened.Connection);

            using LongRunningOperationStore operations = new(db, connections);

            BudgetReservationService daily = new(db, settings);

            GuildQuestStore store = new(new(connections, gate, TimeProvider.System), authority, policy, new GuildBudgetLedger(daily), TimeProvider.System, readiness: readiness);

            Guid key = Guid.NewGuid();

            GuildIssueRootRequest request = new(key, null, "Local", "Compute a result", new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [GuildDelegationKind.Local], null), sessionId);

            var issued = await store.IssueAsync(caller, new(caller.InstallationGeneration, null, caller.InvocationId, key, key), request, default);

            Assert.True(issued.IsSuccess, issued.IsFailure ? issued.Error.Message : null);

            GuildQuestDto quest = issued.Value.Quest;

            var recovery = await policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, default);

            Assert.True(recovery.IsSuccess, recovery.IsFailure ? recovery.Error.Message : null);

            var execution = await authority.RebindAsync(recovery.Value, default);

            Assert.True(execution.IsSuccess, execution.IsFailure ? execution.Error.Message : null);

            var run = await policy.CreateRunCallerAsync(authority, execution.Value, Guid.NewGuid(), default);

            Assert.True(run.IsSuccess, run.IsFailure ? run.Error.Message : null);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            var questLease = await operations.TryAcquireLeaseAsync(quest.OperationId, "quest-owner", now, now.AddMinutes(5));

            Assert.True(questLease.Acquired);

            GuildQuestDriver driver = new(execution.Value, "quest-owner", questLease.Operation.Revision, run.Value);

            var dispatch = await store.PrepareDispatchAsync(driver, Guid.NewGuid(), new string('A', 64), quest.RunId, false, default);

            Assert.True(dispatch.IsSuccess, dispatch.IsFailure ? dispatch.Error.Message : null);

            GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Id, dispatch.Value.DispatchId, quest.RunId, GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 1000);

            BillableOperationRecord bill = new(quest.RunId, BillableOperationType.Chat, "router", "provider/model", "guild", now, now.AddSeconds(1), 100, 20, 5, 40, "{}", 0m, BillableOperationStatus.Failed, "gen-test-123");

            var sent = await store.ExecuteAndSettleProviderResponseAsync(new(store, driver, dispatch.Value), daily, effect,
                _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill), default,
                spendSource: new(provider, "provider/model", "private-provider-key-sentinel"), spendPricing: "{}");

            Assert.True(sent.IsSuccess, sent.IsFailure ? sent.Error.Message : null);

            InMemoryOsCredentialStore os = new();

            os.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("router"), "private-provider-key-sentinel");

            using ProviderCredentialStore credentials = new(os, new EphemeralDataProtectionProvider());

            ProviderApiKeyResolver resolver = new(credentials);

            using GuildSpendRefreshConfiguration config = new(settings);

            var currentness = await GuildSpendRefreshCurrentness.CaptureAsync(config, resolver, "router", "provider/model", default);

            Assert.True(currentness.IsSuccess, currentness.IsFailure ? currentness.Error.Message : null);

            int gets = 0;

            GuildOpenRouterSpendReader reader = new(new RefreshRecoveryDns(), () => { gets++; return new RefreshRecoveryTransport(); });

            LongRunningOperationOwnership ownership = new();

            GuildSpendRefreshService service = new(store, config, resolver, reader, readiness, gate, operations, ownership, TimeProvider.System, tracker);

            GuildSpendRefreshRecoveryHandler handler = new(policy, service);

            LongRunningOperationReconciler reconciler = new(operations, [handler], TimeProvider.System, NullLogger<LongRunningOperationReconciler>.Instance, ownership);

            var first = await store.AdmitProviderSpendRefreshAsync(caller, quest.Id, effect.EffectId, Guid.NewGuid(), currentness.Value, default);

            Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

            Guid firstId = first.Value.OperationId!.Value;

            Assert.True((await operations.TryAcquireLeaseAsync(firstId, "crashed", now, now.AddMinutes(1))).Acquired);

            await using (SqliteCommand expire = opened.Connection.CreateCommand())
            {
                expire.CommandText = "UPDATE LongRunningOperations SET LeaseExpiresAt='2000-01-01T00:00:00.0000000Z' WHERE Id=$id";

                expire.Parameters.AddWithValue("$id", firstId.ToString("N"));

                await expire.ExecuteNonQueryAsync();
            }

            await reconciler.ReconcileNowAsync("source-reconciler", maxConcurrency: 1);

            LongRunningOperation completed = (await operations.GetAsync(firstId))!;

            Assert.True(completed.State == LongRunningOperationState.Completed, $"State={completed.State} error={completed.TerminalErrorCode} GETs={gets}");

            Assert.Equal(1, gets);

            var second = await store.AdmitProviderSpendRefreshAsync(caller, quest.Id, effect.EffectId, Guid.NewGuid(), currentness.Value, default);

            Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : null);

            Guid secondId = second.Value.OperationId!.Value;

            Assert.True((await operations.TryAcquireLeaseAsync(secondId, "crashed", now, now.AddMinutes(1))).Acquired);

            return secondId;
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    internal static async Task<(LongRunningOperation Operation, int Gets)> RecoverRestoredRefreshAsync(string connectionString, Guid operationId)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        string workspace = Directory.CreateTempSubdirectory("restored-refresh-").FullName;

        try
        {
            FixtureOrdinaryConnectionFactory connections = new(connectionString);

            GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

            InboundOptionsMonitor settings = new(new() { Features = new() { Guild = true } });

            settings.CurrentValue.Providers = [new ProviderSettings { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = ["provider/model"], GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration }];

            GuildLocalHostPolicy policy = new(connections, gate, settings, new(new KeySource()), TimeProvider.System, null);

            using SignetAuthority authority = new(new(connections), gate, policy, TimeProvider.System);

            GuildSpendRefreshTracker tracker = new();

            await using GuildLocalHostReadiness readiness = new(settings, connections, policy, authority, TimeProvider.System, Path.Combine(workspace, "host.lock"), gate, tracker);

            var ready = await readiness.EnsureReadyAsync(default);

            Assert.True(ready.IsSuccess, ready.IsFailure ? ready.Error.Message : null);

            await using var opened = (await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default)).Value;

            await using ArcanumDbContext db = GrimoireFixture.CreateContext(opened.Connection);

            using LongRunningOperationStore operations = new(db, connections);

            GuildQuestStore store = new(new(connections, gate, TimeProvider.System), authority, policy, new GuildBudgetLedger(), TimeProvider.System, readiness: readiness);

            InMemoryOsCredentialStore os = new();

            os.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("router"), "private-provider-key-sentinel");

            using ProviderCredentialStore credentials = new(os, new EphemeralDataProtectionProvider());

            using GuildSpendRefreshConfiguration config = new(settings);

            int gets = 0;

            GuildOpenRouterSpendReader reader = new(new RefreshRecoveryDns(), () => { gets++; return new RefreshRecoveryTransport(); });

            LongRunningOperationOwnership ownership = new();

            GuildSpendRefreshService service = new(store, config, new ProviderApiKeyResolver(credentials), reader, readiness, gate, operations, ownership, TimeProvider.System, tracker);

            GuildSpendRefreshRecoveryHandler handler = new(policy, service);

            LongRunningOperationReconciler reconciler = new(operations, [handler], TimeProvider.System, NullLogger<LongRunningOperationReconciler>.Instance, ownership);

            await reconciler.ReconcileNowAsync("restored-reconciler", maxConcurrency: 1);

            LongRunningOperation result = (await operations.GetAsync(operationId))!;

            Assert.False(ownership.IsClaimed(operationId));

            return (result, gets);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
