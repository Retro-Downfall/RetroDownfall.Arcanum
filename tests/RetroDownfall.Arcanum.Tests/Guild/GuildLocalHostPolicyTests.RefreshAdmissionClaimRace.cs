using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
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

using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Reconciler_wins_claim_after_committed_admission_without_second_provider_get()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync();

        GuildSpendRefreshTracker tracker = new();

        string lockPath = Path.Combine(f.WorkspaceRoot, "host.lock");

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, lockPath, f.Gate, tracker);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        GuildCallerContext caller = await f.OperatorAsync();

        var issued = await f.IssueAsync(caller);

        Assert.True(issued.IsSuccess, issued.IsFailure ? issued.Error.Message : null);

        GuildQuestDto quest = issued.Value.Quest;

        var recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.IsFailure ? recovery.Error.Message : null);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.IsFailure ? execution.Error.Message : null);

        var run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, Guid.NewGuid(), default);

        Assert.True(run.IsSuccess, run.IsFailure ? run.Error.Message : null);

        await using var opened = (await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default)).Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(opened.Connection);

        using LongRunningOperationStore operations = new(db, f.Connections);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var lease = await operations.TryAcquireLeaseAsync(quest.OperationId, "quest-owner", now, now.AddMinutes(5));

        Assert.True(lease.Acquired);

        GuildQuestDriver driver = new(execution.Value, "quest-owner", lease.Operation.Revision, run.Value);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), new string('A', 64), quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.IsFailure ? dispatch.Error.Message : null);

        ProviderSettings provider = new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = ["provider/model"],
            GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration,
            CredentialEnvironmentVariable = "ARCANUM_TEST_REFRESH_" + Guid.NewGuid().ToString("N") };

        f.Settings.CurrentValue.Providers = [provider];

        BudgetReservationService daily = new(db, f.Settings);

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(daily), TimeProvider.System, readiness: f.Readiness);

        GuildLocalInferenceExecution physical = new(store, driver, dispatch.Value);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Id, dispatch.Value.DispatchId, quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 1000);

        BillableOperationRecord bill = new(quest.RunId, BillableOperationType.Chat, "router", "provider/model", "guild",
            now, now.AddSeconds(1), 100, 20, 5, 40, "{}", 0m, BillableOperationStatus.Completed, "gen-test-123");

        var sent = await store.ExecuteAndSettleProviderResponseAsync(physical, daily, effect,
            _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill), default,
            spendSource: new(provider, "provider/model", "private-provider-key-sentinel"), spendPricing: "{}");

        Assert.True(sent.IsSuccess, sent.IsFailure ? sent.Error.Message : null);

        InMemoryOsCredentialStore os = new();

        os.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("router"), "private-provider-key-sentinel");

        using ProviderCredentialStore credentialStore = new(os, new EphemeralDataProtectionProvider());

        ProviderApiKeyResolver resolver = new(credentialStore);

        using GuildSpendRefreshConfiguration config = new(f.Settings);

        var currentness = await GuildSpendRefreshCurrentness.CaptureAsync(config, resolver, "router", "provider/model", default);

        Assert.True(currentness.IsSuccess, currentness.IsFailure ? currentness.Error.Message : null);

        int gets = 0;

        GuildOpenRouterSpendReader reader = new(new RefreshRecoveryDns(), () =>
        {
            gets++;

            return new RefreshRecoveryTransport();
        });

        LongRunningOperationOwnership ownership = new();

        Guid refreshId = Guid.NewGuid();

        CommitObservedFactory factory = new(f.Connections, refreshId);

        using SignetAuthority raceAuthority = new(new GuildSignetStore(factory), f.Gate, f.Policy, TimeProvider.System);

        GuildQuestStore raceStore = new(new(factory, f.Gate, TimeProvider.System), raceAuthority, f.Policy,
            new GuildBudgetLedger(daily), TimeProvider.System, readiness: f.Readiness);

        GuildSpendRefreshService service = new(raceStore, config, resolver, reader, readiness, f.Gate, operations, ownership,
            TimeProvider.System, tracker);

        var original = service.RefreshAsync(caller, quest.Id, effect.EffectId, refreshId, default);

        Task<LongRunningOperationReconciliationSummary>? recoveryPass = null;

        ClaimObservedHandler handler = new(new GuildSpendRefreshRecoveryHandler(f.Policy, service));

        try
        {
            Guid operationId = await factory.Committed.Task.WaitAsync(TimeSpan.FromSeconds(15));

            LongRunningOperation committed = (await operations.GetAsync(operationId))!;

            Assert.Equal(LongRunningOperationState.Pending, committed.State);

            Assert.Equal(0, committed.AttemptCount);

            LongRunningOperationReconciler reconciler = new(operations, [handler], TimeProvider.System,
                NullLogger<LongRunningOperationReconciler>.Instance, ownership);

            recoveryPass = reconciler.ReconcileNowAsync("claim-first", maxConcurrency: 1);

            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(ownership.IsClaimed(operationId));

            factory.Release.TrySetResult();

            var first = await original.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

            Assert.Equal(operationId, first.Value.OperationId);

            Assert.True(first.Value.InFlight);

            Assert.Equal(0, gets);

            handler.Release.TrySetResult();

            await recoveryPass;

            Assert.Equal(LongRunningOperationState.Completed, (await operations.GetAsync(operationId))!.State);

            Assert.Equal(1, gets);
        }
        finally
        {
            factory.Release.TrySetResult();

            handler.Release.TrySetResult();

            await original;

            if (recoveryPass is not null) await recoveryPass;
        }
    }

    private sealed class ClaimObservedHandler(GuildSpendRefreshRecoveryHandler inner) : ILongRunningOperationRecoveryHandler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => inner.Kind;

        public int SupportedCheckpointVersion => inner.SupportedCheckpointVersion;

        public async Task<LongRunningOperationRecoveryResult> RecoverAsync(LongRunningOperation operation, CancellationToken ct)
        {
            Entered.TrySetResult();

            await Release.Task.WaitAsync(ct);

            return await inner.RecoverAsync(operation, ct);
        }
    }

    private sealed class CommitObservedFactory(IGrimoireOrdinaryConnectionFactory inner, Guid refreshId) : IGrimoireOrdinaryConnectionFactory
    {
        private readonly Guid _refreshId = refreshId;

        private int _observed;

        internal TaskCompletionSource<Guid> Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection,
            CovenantSqliteConnectionMode mode, CancellationToken ct) => inner.AcquireScopedAsync(connection, mode, ct);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind kind, CancellationToken ct)
        {
            Result<IGrimoireOrdinaryConnectionLease> opened = await inner.OpenFreshAsync(kind, ct);

            return opened.IsFailure ? opened.Error : new ObservedLease(this, opened.Value, kind);
        }

        private sealed class ObservedLease(CommitObservedFactory owner, IGrimoireOrdinaryConnectionLease inner,
            GrimoireOrdinaryFreshConnectionKind kind) : IGrimoireOrdinaryConnectionLease
        {
            public SqliteConnection Connection => inner.Connection;

            public void Dispose() => inner.Dispose();

            public async ValueTask DisposeAsync()
            {
                Guid operation = Guid.Empty;

                if (kind == GrimoireOrdinaryFreshConnectionKind.ReadWrite && Volatile.Read(ref owner._observed) == 0)
                {
                    await using SqliteCommand command = Connection.CreateCommand();

                    command.CommandText = "SELECT OperationId FROM guild_spend_refreshes WHERE RefreshId=$refresh";

                    command.Parameters.AddWithValue("$refresh", owner._refreshId.ToString("D").ToUpperInvariant());

                    if (await command.ExecuteScalarAsync() is string raw) Guid.TryParseExact(raw, "N", out operation);
                }

                await inner.DisposeAsync();

                if (operation != Guid.Empty && Interlocked.Exchange(ref owner._observed, 1) == 0)
                {
                    owner.Committed.TrySetResult(operation);

                    await owner.Release.Task;
                }
            }
        }
    }
}
