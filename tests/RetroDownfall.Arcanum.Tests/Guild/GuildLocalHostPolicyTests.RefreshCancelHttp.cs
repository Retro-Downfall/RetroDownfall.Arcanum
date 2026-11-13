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
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Http_cancel_signals_only_matching_refresh_and_keeps_physical_claim_until_join(bool leaseLost, bool callbackFails)
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

        var (_, store, effect, _) = await CaptureCancellationRefreshAsync(f, caller, quest, db, operations);

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

        Guid firstRefresh = Guid.NewGuid();

        Guid secondRefresh = Guid.NewGuid();

        var first = service.RefreshAsync(caller, quest.Id, effect.EffectId, firstRefresh, default);

        Task<Result<GuildSpendRefreshResult>>? second = null;

        GuildSpendRefreshTracker.Entry? faultEntry = null;

        CancellationTokenRegistration faultRegistration = default;

        try
        {
            await firstBody.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            second = service.RefreshAsync(caller, quest.Id, effect.EffectId, secondRefresh, default);

            await secondBody.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var firstSaved = await store.PrepareProviderSpendRefreshAsync(caller, quest.Id, effect.EffectId, firstRefresh, default);

            var secondSaved = await store.PrepareProviderSpendRefreshAsync(caller, quest.Id, effect.EffectId, secondRefresh, default);

            Guid firstOperation = firstSaved.Value.OperationId!.Value;

            Guid secondOperation = secondSaved.Value.OperationId!.Value;

            if (callbackFails)
            {
                Assert.True(tracker.TryEnter(quest.RootQuestId, out faultEntry));

                Assert.NotNull(faultEntry);

                faultEntry.BindOperation(firstOperation);

                faultRegistration = faultEntry.Stop.Register(() => throw new InvalidOperationException("injected cancellation callback failure"));
            }

            using HttpClient client = server.GetTestClient();

            using HttpResponseMessage response = await client.PostAsync($"/api/operations/{firstOperation:D}/cancel", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.True(firstBody.ReadToken.IsCancellationRequested);

            Assert.False(secondBody.ReadToken.IsCancellationRequested);

            Assert.False(first.IsCompleted);

            Assert.True(ownership.IsClaimed(firstOperation));

            Assert.True(ownership.IsClaimed(secondOperation));

            Assert.Equal(LongRunningOperationState.Cancelling, (await operations.GetAsync(firstOperation))!.State);

            if (leaseLost)
            {
                await f.ExecuteAsync($"UPDATE LongRunningOperations SET LeaseExpiresAt='2000-01-01T00:00:00.0000000Z' WHERE Id='{firstOperation:N}'");

                await reconciler.ReconcileNowAsync("cancel-while-owned", maxConcurrency: 1);

                Assert.True(ownership.IsClaimed(firstOperation));

                Assert.Equal(2, gets);
            }

            firstBody.ReleaseRead.TrySetResult();

            await firstBody.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(ownership.IsClaimed(firstOperation));

            Assert.False(first.IsCompleted);

            firstBody.ReleaseDispose.TrySetResult();

            var cancelled = await first;

            Assert.True(cancelled.IsSuccess);

            Assert.False(ownership.IsClaimed(firstOperation));

            if (leaseLost)
            {
                Assert.Equal(LongRunningOperationState.Cancelling, (await operations.GetAsync(firstOperation))!.State);

                await reconciler.ReconcileNowAsync("cancel-after-join", maxConcurrency: 1);
            }
            else Assert.True(cancelled.Value.Cancelled);

            LongRunningOperation settled = (await operations.GetAsync(firstOperation))!;

            Assert.Equal(LongRunningOperationState.Abandoned, settled.State);

            Assert.Equal(ErrorCodes.Guild.RefreshCancelled, settled.TerminalErrorCode);

            var replay = await service.RefreshAsync(caller, quest.Id, effect.EffectId, firstRefresh, default);

            Assert.True(replay.IsSuccess);

            Assert.Equal(firstOperation, replay.Value.OperationId);

            Assert.True(replay.Value.Cancelled);

            Assert.Equal(2, gets);
        }
        finally
        {
            faultRegistration.Dispose();

            faultEntry?.Dispose();

            firstBody.ReleaseRead.TrySetResult();

            firstBody.ReleaseDispose.TrySetResult();

            secondBody.ReleaseRead.TrySetResult();

            secondBody.ReleaseDispose.TrySetResult();

            await first;

            if (second is not null) await second;
        }
    }

    private sealed class CancelProbeTransport(CancelProbeStream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
    }

    private sealed class CancelProbeStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken ReadToken { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadToken = cancellationToken;

            Entered.TrySetResult();

            await ReleaseRead.Task;

            return 0;
        }

        public override async ValueTask DisposeAsync()
        {
            Disposing.TrySetResult();

            await ReleaseDispose.Task;

            await base.DisposeAsync();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
