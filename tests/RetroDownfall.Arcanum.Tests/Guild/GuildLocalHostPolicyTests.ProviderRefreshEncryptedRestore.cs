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

            ProviderSettings provider = new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = ["provider/model"] };

            settings.CurrentValue.Providers = [provider];



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

            BudgetReservationService daily = new(db);

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

            BudgetReservation legacy = await LegacyBudgetFixture.SeedAsync(db,
                new(quest.RunId, 1m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

            GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Id, dispatch.Value.DispatchId, quest.RunId,
                GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), legacy.Id, 1m, 1000);

            BillableOperationRecord bill = new(quest.RunId, BillableOperationType.Chat, "router", "provider/model", "guild", now, now.AddSeconds(1), 100, 20, 5, 40, "{}", 0m, BillableOperationStatus.Failed, "gen-test-123");

            var sent = await store.ExecuteAndSettleProviderResponseAsync(new(store, driver, dispatch.Value), daily, effect,
                _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill), default,
                spendSource: new(provider, "provider/model", "private-provider-key-sentinel"), spendPricing: "{}");

            Assert.True(sent.IsSuccess, sent.IsFailure ? sent.Error.Message : null);

            Guid secondId = await HistoricalGuildRefreshFixture.SeedAsync(opened.Connection, caller, quest.Id, effect.EffectId, Guid.NewGuid());

            Assert.True((await operations.TryAcquireLeaseAsync(secondId, "crashed", now, now.AddMinutes(1))).Acquired);

            return secondId;
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    internal static async Task<LongRunningOperation> RecoverRestoredRefreshAsync(string connectionString, Guid operationId)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        string workspace = Directory.CreateTempSubdirectory("restored-refresh-").FullName;

        try
        {
            FixtureOrdinaryConnectionFactory connections = new(connectionString);

            GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

            InboundOptionsMonitor settings = new(new() { Features = new() { Guild = true } });

            settings.CurrentValue.Providers = [new ProviderSettings { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = ["provider/model"] }];

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

            LongRunningOperationOwnership ownership = new();

            GuildSpendRefreshService service = new(store, ownership, TimeProvider.System);

            GuildSpendRefreshRecoveryHandler handler = new(policy, service);

            LongRunningOperationReconciler reconciler = new(operations, [handler], TimeProvider.System, NullLogger<LongRunningOperationReconciler>.Instance, ownership);

            await reconciler.ReconcileNowAsync("restored-reconciler", maxConcurrency: 1);

            LongRunningOperation result = (await operations.GetAsync(operationId))!;

            Assert.False(ownership.IsClaimed(operationId));

            return result;
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
