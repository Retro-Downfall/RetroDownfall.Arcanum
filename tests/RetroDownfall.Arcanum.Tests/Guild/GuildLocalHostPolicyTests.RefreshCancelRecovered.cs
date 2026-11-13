using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
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
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Cancellation_of_recovered_reader_settles_abandoned_only_after_disposal(bool cancelAfterHandler, bool expireBeforeSettlement,
        bool eraseAncestor)
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

        GuildQuestStored root = issued.Value;

        GuildQuestStored selected = eraseAncestor ? await IssueChildAsync(f, await RunCallerAsync(f, caller, root), root) : root;

        GuildQuestDto quest = selected.Quest;

        if (eraseAncestor) Assert.NotEqual(root.Quest.SessionId, quest.SessionId);

        await using var opened = (await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default)).Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(opened.Connection);

        using LongRunningOperationStore operations = new(db, f.Connections);

        var (now, store, effect, _) = await CaptureCancellationRefreshAsync(f, caller, quest, db, operations);

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

        RefreshManualClock clock = new(now);

        GuildSpendRefreshService service = new(store, config, resolver, reader, readiness, f.Gate, operations, ownership,
            clock, tracker);

        GuildSpendRefreshRecoveryHandler handler = new(f.Policy, service);

        RefreshResultBarrier barrier = new(handler, cancelAfterHandler || expireBeforeSettlement);

        LongRunningOperationReconciler reconciler = new(operations, [barrier], clock,
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

        if (eraseAncestor)
        {
            LongRunningOperation pending = (await operations.GetAsync(operation))!;

            Assert.True(await operations.RequestCancellationAsync(operation, pending.Revision, now));

            using (CovenantSqliteConnectionInitializer.Instance.Authorize(opened.Connection, CovenantSqliteAuthorizationKind.SessionRetention))
            await using (SqliteCommand delete = opened.Connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM Sessions WHERE Id=$id";

                delete.Parameters.AddWithValue("$id", root.Quest.SessionId!.Value.ToString("D").ToUpperInvariant());

                Assert.Equal(1, await delete.ExecuteNonQueryAsync());
            }

            await reconciler.ReconcileNowAsync("erased-ancestor", maxConcurrency: 1);

            Assert.Equal(LongRunningOperationState.Abandoned, barrier.Result!.State);

            LongRunningOperation settled = (await operations.GetAsync(operation))!;

            Assert.Equal(operation, settled.Id);

            Assert.Equal(LongRunningOperationState.Abandoned, settled.State);

            Assert.Equal(ErrorCodes.Guild.RefreshCancelled, settled.TerminalErrorCode);

            Assert.Equal(0, gets);

            return;
        }

        Task<LongRunningOperationReconciliationSummary> recoveryPass = reconciler.ReconcileNowAsync("recovered-refresh", maxConcurrency: 1);

        try
        {
            await firstBody.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            using HttpClient client = server.GetTestClient();

            if (cancelAfterHandler)
            {
                firstBody.ReleaseRead.TrySetResult();

                await firstBody.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

                firstBody.ReleaseDispose.TrySetResult();

                await barrier.Finished.Task.WaitAsync(TimeSpan.FromSeconds(15));

                Assert.Equal(LongRunningOperationState.ReconciliationRequired, barrier.Result!.State);
            }

            using HttpResponseMessage response = await client.PostAsync($"/api/operations/{operation:D}/cancel", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.True(ownership.IsClaimed(operation));

            if (!cancelAfterHandler)
            {
                Assert.True(firstBody.ReadToken.IsCancellationRequested);

                firstBody.ReleaseRead.TrySetResult();

                await firstBody.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

                Assert.True(ownership.IsClaimed(operation));

                firstBody.ReleaseDispose.TrySetResult();
            }

            if (expireBeforeSettlement)
            {
                await barrier.Finished.Task.WaitAsync(TimeSpan.FromSeconds(15));

                Assert.Equal(LongRunningOperationState.Abandoned, barrier.Result!.State);

                clock.Advance(TimeSpan.FromMinutes(3));
            }

            barrier.Release.TrySetResult();

            await recoveryPass;

            LongRunningOperation final = (await operations.GetAsync(operation))!;

            Assert.Equal(expireBeforeSettlement ? LongRunningOperationState.Cancelling : LongRunningOperationState.Abandoned, final.State);

            if (!expireBeforeSettlement) Assert.Equal(ErrorCodes.Guild.RefreshCancelled, final.TerminalErrorCode);

            Assert.False(ownership.IsClaimed(operation));

            Assert.Equal(1, gets);

            if (expireBeforeSettlement)
            {
                DateTimeOffset claimAt = clock.GetUtcNow();

                var next = await operations.TryAcquireLeaseAsync(operation, "next-owner", claimAt, claimAt.AddMinutes(2));

                Assert.True(next.Acquired);

                await using var probe = (await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default)).Value;

                probe.Connection.DefaultTimeout = 1;

                WriterProbeClock writerClock = new(probe.Connection, claimAt.AddMinutes(3));

                LongRunningOperationState? stale = await ((IGuildRefreshOwnedSettlement)operations).TrySettleGuildRefreshAsync(operation,
                    next.Operation.Revision, "next-owner", LongRunningOperationState.ReconciliationRequired, ErrorCodes.Guild.Unavailable,
                    writerClock, default);

                Assert.True(writerClock.WriterWasHeld);

                Assert.Null(stale);

                Assert.Equal(LongRunningOperationState.Cancelling, (await operations.GetAsync(operation))!.State);

                clock.Advance(TimeSpan.FromMinutes(3));

                await reconciler.ReconcileNowAsync("final-owner", maxConcurrency: 1);

                LongRunningOperation recovered = (await operations.GetAsync(operation))!;

                Assert.Equal(LongRunningOperationState.Abandoned, recovered.State);

                Assert.Equal(ErrorCodes.Guild.RefreshCancelled, recovered.TerminalErrorCode);

                Assert.Equal(1, gets);
            }
        }
        finally
        {
            barrier.Release.TrySetResult();

            firstBody.ReleaseRead.TrySetResult();

            firstBody.ReleaseDispose.TrySetResult();

            secondBody.ReleaseRead.TrySetResult();

            secondBody.ReleaseDispose.TrySetResult();

            await recoveryPass;
        }
    }

    private sealed class RefreshResultBarrier(ILongRunningOperationRecoveryHandler inner, bool block) : ILongRunningOperationRecoveryHandler
    {
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal LongRunningOperationRecoveryResult? Result { get; private set; }

        public string Kind => inner.Kind;

        public int SupportedCheckpointVersion => inner.SupportedCheckpointVersion;

        public async Task<LongRunningOperationRecoveryResult> RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
        {
            LongRunningOperationRecoveryResult result = await inner.RecoverAsync(operation, cancellationToken);

            Result = result;

            Finished.TrySetResult();

            if (block) await Release.Task;

            return result;
        }
    }


    private sealed class RefreshManualClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }


    private sealed class WriterProbeClock(SqliteConnection probe, DateTimeOffset now) : TimeProvider
    {
        internal bool WriterWasHeld { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            SqliteException error = Assert.Throws<SqliteException>(() => probe.BeginTransaction(deferred: false));

            Assert.Contains(error.SqliteErrorCode, new[] { 5, 6 });

            WriterWasHeld = true;

            return now;
        }
    }

}
