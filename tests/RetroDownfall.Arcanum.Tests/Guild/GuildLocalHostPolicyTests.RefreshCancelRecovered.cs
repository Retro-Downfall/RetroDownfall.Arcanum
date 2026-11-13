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

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Api.Operations;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Cancellation_of_recovered_reader_settles_abandoned_only_after_disposal()
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

        CancelProbeStream firstBody = new();

        CancelProbeStream secondBody = new();

        GuildOpenRouterSpendReader reader = new(new RefreshRecoveryDns(), () =>
            new CancelProbeTransport(Interlocked.Increment(ref gets) == 1 ? firstBody : secondBody));

        LongRunningOperationOwnership ownership = new();

        GuildSpendRefreshService service = new(store, config, resolver, reader, readiness, f.Gate, operations, ownership,
            TimeProvider.System, tracker);

        GuildSpendRefreshRecoveryHandler handler = new(f.Policy, service);

        LongRunningOperationReconciler reconciler = new(operations, [handler], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, ownership);

        using IHost server = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();

            web.ConfigureServices(services =>
            {
                services.AddRouting();

                services.AddSingleton<ILongRunningOperationStore>(operations);

                services.AddSingleton<ILongRunningOperationOwnerRecoveryDiagnostics>(operations);

                services.AddSingleton(reconciler);

                services.AddSingleton(new LongRunningOperationReconciliationStatus());

                services.AddSingleton(TimeProvider.System);

                services.AddSingleton(tracker);
            });

            web.Configure(app =>
            {
                app.UseRouting();

                app.UseEndpoints(endpoints => endpoints.MapGroup("/api").MapLongRunningOperationEndpoints());
            });
        }).StartAsync();

        Guid refresh = Guid.NewGuid();

        var admitted = await store.AdmitProviderSpendRefreshAsync(caller, quest.Id, effect.EffectId, refresh, currentness.Value, default);

        Assert.True(admitted.IsSuccess);

        Guid operation = admitted.Value.OperationId!.Value;

        Task<LongRunningOperationReconciliationSummary> recoveryPass = reconciler.ReconcileNowAsync("recovered-refresh", maxConcurrency: 1);

        try
        {
            await firstBody.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            using HttpClient client = server.GetTestClient();

            using HttpResponseMessage response = await client.PostAsync($"/api/operations/{operation:D}/cancel", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.True(firstBody.ReadToken.IsCancellationRequested);

            Assert.True(ownership.IsClaimed(operation));

            firstBody.ReleaseRead.TrySetResult();

            await firstBody.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(ownership.IsClaimed(operation));

            firstBody.ReleaseDispose.TrySetResult();

            await recoveryPass;

            LongRunningOperation final = (await operations.GetAsync(operation))!;

            Assert.Equal(LongRunningOperationState.Abandoned, final.State);

            Assert.Equal(ErrorCodes.Guild.RefreshCancelled, final.TerminalErrorCode);

            Assert.False(ownership.IsClaimed(operation));

            Assert.Equal(1, gets);
        }
        finally
        {
            firstBody.ReleaseRead.TrySetResult();

            firstBody.ReleaseDispose.TrySetResult();

            secondBody.ReleaseRead.TrySetResult();

            secondBody.ReleaseDispose.TrySetResult();

            await recoveryPass;
        }
    }
}
