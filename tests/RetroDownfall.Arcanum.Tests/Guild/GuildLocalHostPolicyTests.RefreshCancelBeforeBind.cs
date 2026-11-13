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
    public async Task Durable_cancel_before_operation_binding_refuses_provider_get()
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

        await using var opened = (await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default)).Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(opened.Connection);

        using LongRunningOperationStore operations = new(db, f.Connections);

        var (_, store, effect, daily) = await CaptureCancellationRefreshAsync(f, caller, quest, db, operations);

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

        var original = service.RefreshAsync(caller, quest.Id, effect.EffectId, refreshId, default);

        try
        {
            Guid operationId = await factory.Committed.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(ownership.IsClaimed(operationId));

            Assert.Equal(LongRunningOperationState.Pending, (await operations.GetAsync(operationId))!.State);

            using HttpClient client = server.GetTestClient();

            using HttpResponseMessage response = await client.PostAsync($"/api/operations/{operationId:D}/cancel", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.Equal(LongRunningOperationState.Cancelling, (await operations.GetAsync(operationId))!.State);

            factory.Release.TrySetResult();

            var cancelled = await original.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(cancelled.IsSuccess, cancelled.IsFailure ? cancelled.Error.Message : null);

            Assert.Equal(operationId, cancelled.Value.OperationId);

            Assert.Equal(0, gets);

            Assert.True(cancelled.Value.Cancelled);

            Assert.Equal(LongRunningOperationState.Abandoned, (await operations.GetAsync(operationId))!.State);
        }
        finally
        {
            factory.Release.TrySetResult();

            await original;
        }
    }
}
