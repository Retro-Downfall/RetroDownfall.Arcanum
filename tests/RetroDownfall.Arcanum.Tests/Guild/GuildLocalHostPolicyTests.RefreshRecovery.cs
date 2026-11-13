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

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registered_refresh_handler_and_readiness_own_actual_reader(bool stopDuringBody)
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

        RealRefreshBlockingStream blocked = new();

        GuildOpenRouterSpendReader reader = new(new RefreshRecoveryDns(), () =>
        {
            gets++;

            return stopDuringBody ? new RealRefreshBlockingTransport(blocked) : new RefreshRecoveryTransport();
        });

        LongRunningOperationOwnership ownership = new();

        GuildSpendRefreshService service = new(store, config, resolver, reader, readiness, f.Gate, operations, ownership,
            TimeProvider.System, tracker);

        if (stopDuringBody)
        {
            Task<Result<GuildSpendRefreshResult>> running = service.RefreshAsync(caller, quest.Id, effect.EffectId, Guid.NewGuid(), default);

            Task? stop = null;

            Task? secondStop = null;

            try
            {
                await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

                stop = readiness.StopAsync(new CancellationToken(canceled: true));

                secondStop = readiness.DisposeAsync().AsTask();

                Assert.False(stop.IsCompleted);

                Assert.False(secondStop.IsCompleted);

                Assert.True(GuildWorkspaceLeaseHostLock.TryAcquire(lockPath).IsFailure);

                blocked.ReleaseRead.TrySetResult();

                await blocked.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

                Assert.False(stop.IsCompleted);

                Assert.False(secondStop.IsCompleted);

                Assert.True(GuildWorkspaceLeaseHostLock.TryAcquire(lockPath).IsFailure);

                blocked.ReleaseDispose.TrySetResult();

                await running;

                await Task.WhenAll(stop, secondStop);

                var successor = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

                Assert.True(successor.IsSuccess);

                successor.Value.Dispose();

                Assert.Equal(1, gets);
            }
            finally
            {
                blocked.ReleaseRead.TrySetResult();

                blocked.ReleaseDispose.TrySetResult();

                await running;

                if (stop is not null) await stop;

                if (secondStop is not null) await secondStop;
            }

            return;
        }

        Guid refreshId = Guid.NewGuid();

        var admitted = await store.AdmitProviderSpendRefreshAsync(caller, quest.Id, effect.EffectId, refreshId, currentness.Value, default);

        Assert.True(admitted.IsSuccess, admitted.IsFailure ? admitted.Error.Message : null);

        Guid operationId = admitted.Value.OperationId!.Value;

        var crashed = await operations.TryAcquireLeaseAsync(operationId, "crashed", now, now.AddMinutes(1));

        Assert.True(crashed.Acquired);

        await f.ExecuteAsync($"UPDATE LongRunningOperations SET LeaseExpiresAt='2000-01-01T00:00:00.0000000Z' WHERE Id='{operationId:N}'");

        GuildSpendRefreshRecoveryHandler handler = new(f.Policy, service);

        LongRunningOperationReconciler reconciler = new(operations, [handler], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, ownership);

        await reconciler.ReconcileNowAsync("reconciler", maxConcurrency: 1);

        LongRunningOperation final = (await operations.GetAsync(operationId))!;

        Assert.True(final.State == LongRunningOperationState.Completed, $"State={final.State}, error={final.TerminalErrorCode}, GETs={gets}");

        Assert.Equal(1, gets);

        Assert.False(ownership.IsClaimed(operationId));
    }

    private sealed class RealRefreshBlockingTransport(RealRefreshBlockingStream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
    }

    private sealed class RealRefreshBlockingStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
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

    private sealed class RefreshRecoveryDns : IDnsResolver
    {
        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult<IPAddress[]>([IPAddress.Parse("1.1.1.1")]);
    }

    private sealed class RefreshRecoveryTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"data":{"api_type":"completions","id":"gen-test-123","is_byok":false,"model":"provider/model",
                "native_tokens_cached":40,"native_tokens_completion":20,"native_tokens_completion_images":0,
                "native_tokens_prompt":100,"native_tokens_reasoning":5,"num_input_audio_prompt":0,
                "num_media_completion":0,"num_media_prompt":0,"total_cost":0.25}}
                """) });
    }
}
