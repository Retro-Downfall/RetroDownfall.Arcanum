using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class DataRetentionSweepHostedServiceTests
{
    [Fact]
    public async Task ClosedGateSkipsAutomaticSweepBeforeScope()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        RecordingHostedRetentionService service = new();

        MutablePolicyStore policy = new(new RetentionSettings { AutomaticSweepsEnabled = true });

        TestCapturingLogger<DataRetentionSweepHostedService> logger = new();

        LongRunningOperationOwnership ownership = new();

        await using ServiceProvider provider = Services(service).BuildServiceProvider();

        CountingScopeFactory scopes = new(provider.GetRequiredService<IServiceScopeFactory>());

        DataRetentionSweepHostedService hosted = new(
            scopes,
            policy,
            TimeProvider.System,
            admission,
            ownership,
            logger);

        IGrimoireClosingOwner closing = inner.BeginOrResumeExclusive(Owner()).Value;

        try
        {
            await hosted.RunOnceAsync(CancellationToken.None);

            Assert.Equal([GrimoireWorkKind.DataRetentionSweep], admission.RequestedWorkKinds);

            Assert.Equal(0, policy.Reads);

            Assert.Equal(0, scopes.Created);

            Assert.Equal(0, service.ApplyCalls);

            Assert.DoesNotContain(logger.Entries, static entry => entry.Level >= LogLevel.Warning);
        }
        finally
        {
            await ReopenAsync(inner, closing);

            hosted.Dispose();
        }
    }

    [Fact]
    public async Task KeepClosedRetainedSweepWaitIsCancelledByStop()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        LongRunningOperationOwnership ownership = new();

        DeferredHostedRetentionService service = new(inner, ownership);

        MutablePolicyStore policy = new(new RetentionSettings { AutomaticSweepsEnabled = true });

        TestCapturingLogger<DataRetentionSweepHostedService> logger = new();

        await using ServiceProvider provider = Services(service).BuildServiceProvider();

        CountingScopeFactory scopes = new(provider.GetRequiredService<IServiceScopeFactory>());

        using DataRetentionSweepHostedService hosted = new(
            scopes,
            policy,
            TimeProvider.System,
            admission,
            ownership,
            logger);

        await hosted.StartAsync(CancellationToken.None);

        try
        {
            await service.Deferred.Task.WaitAsync(TimeSpan.FromSeconds(10));

            IGrimoireClosingOwner closing = Assert.IsAssignableFrom<IGrimoireClosingOwner>(service.Closing);

            IGrimoireExclusiveClosedLease closed = await CloseAsync(inner, closing);

            Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, CancellationToken.None)).IsSuccess);

            await WaitUntilAsync(() => admission.ActiveGenerationWaiters == 1);

            Assert.Equal([0L], admission.ObservedGenerationWaits);

            DataRetentionHostedSweepContinuation continuation = Assert.IsType<DataRetentionHostedSweepContinuation>(
                service.Continuation);

            Assert.True(ownership.IsClaimedBy(
                continuation.OperationId,
                continuation.OwnershipToken));

            Assert.Equal(1, service.ApplyCalls);

            Assert.False(service.LastContinuationWasPresent);

            Task stopped = hosted.StopAsync(CancellationToken.None);

            await stopped.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(stopped.IsCompletedSuccessfully);

            Assert.Equal(0, admission.ActiveGenerationWaiters);

            Assert.Equal(1, scopes.Created);

            Assert.Equal(1, scopes.Disposed);

            Assert.False(ownership.IsClaimedBy(
                continuation.OperationId,
                continuation.OwnershipToken));

            Assert.DoesNotContain(logger.Entries, static entry => entry.Level >= LogLevel.Warning);

            await closed.DisposeAsync();

            await closing.DisposeAsync();
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ImmediateRecloseRetainsExactSweepWithoutOpeningAnotherScope()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        LongRunningOperationOwnership ownership = new();

        DeferredHostedRetentionService service = new(inner, ownership, concludeOnResume: true);

        MutablePolicyStore policy = new(new RetentionSettings { AutomaticSweepsEnabled = true });

        await using ServiceProvider provider = Services(service).BuildServiceProvider();

        CountingScopeFactory scopes = new(provider.GetRequiredService<IServiceScopeFactory>());

        using DataRetentionSweepHostedService hosted = new(
            scopes,
            policy,
            TimeProvider.System,
            admission,
            ownership,
            new TestCapturingLogger<DataRetentionSweepHostedService>());

        Task running = hosted.RunOnceAsync(CancellationToken.None);

        await service.Deferred.Task.WaitAsync(TimeSpan.FromSeconds(10));

        IGrimoireClosingOwner firstClosing = Assert.IsAssignableFrom<IGrimoireClosingOwner>(service.Closing);

        IGrimoireExclusiveClosedLease firstClosed = await CloseAsync(inner, firstClosing);

        IGrimoireClosingOwner? reclosed = null;

        admission.AfterOpenGenerationObserved = _ =>
            reclosed ??= inner.BeginOrResumeExclusive(Owner()).Value;

        Assert.True((await firstClosed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

        await WaitUntilAsync(() => admission.GenerationWaits == 2 && admission.ActiveGenerationWaiters == 1);

        Assert.Equal([0L, 1L], admission.ObservedGenerationWaits);

        DataRetentionHostedSweepContinuation continuation = Assert.IsType<DataRetentionHostedSweepContinuation>(
            service.Continuation);

        Assert.Equal(1, service.ApplyCalls);

        Assert.Equal(1, scopes.Created);

        Assert.Equal(1, scopes.Disposed);

        Assert.True(ownership.IsClaimedBy(
            continuation.OperationId,
            continuation.OwnershipToken));

        await firstClosed.DisposeAsync();

        await firstClosing.DisposeAsync();

        await ReopenAsync(inner, Assert.IsAssignableFrom<IGrimoireClosingOwner>(reclosed));

        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, service.ApplyCalls);

        Assert.True(service.LastContinuationWasPresent);

        Assert.Equal(2, scopes.Created);

        Assert.Equal(2, scopes.Disposed);

        Assert.False(ownership.IsClaimedBy(
            continuation.OperationId,
            continuation.OwnershipToken));
    }

    [Fact]
    public async Task ResumedSweepRefusesAReplacementContinuationAndReleasesItsOriginalClaim()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        LongRunningOperationOwnership ownership = new();

        DeferredHostedRetentionService service = new(
            inner,
            ownership,
            replaceContinuationOnResume: true);

        MutablePolicyStore policy = new(new RetentionSettings { AutomaticSweepsEnabled = true });

        await using ServiceProvider provider = Services(service).BuildServiceProvider();

        using DataRetentionSweepHostedService hosted = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            policy,
            TimeProvider.System,
            admission,
            ownership,
            new TestCapturingLogger<DataRetentionSweepHostedService>());

        Task running = hosted.RunOnceAsync(CancellationToken.None);

        await service.Deferred.Task.WaitAsync(TimeSpan.FromSeconds(10));

        DataRetentionHostedSweepContinuation original = Assert.IsType<DataRetentionHostedSweepContinuation>(
            service.Continuation);

        await ReopenAsync(
            inner,
            Assert.IsAssignableFrom<IGrimoireClosingOwner>(service.Closing));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await running.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("exact durable continuation", failure.Message, StringComparison.Ordinal);

        Assert.Equal(2, service.ApplyCalls);

        Assert.False(ownership.IsClaimedBy(original.OperationId, original.OwnershipToken));
    }

    [Fact]
    public async Task InvalidFreshDeferredOutcomeStillReleasesItsRetainedClaim()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        LongRunningOperationOwnership ownership = new();

        DeferredHostedRetentionService service = new(
            inner,
            ownership,
            returnResultOnInitialDeferral: true);

        MutablePolicyStore policy = new(new RetentionSettings { AutomaticSweepsEnabled = true });

        await using ServiceProvider provider = Services(service).BuildServiceProvider();

        using DataRetentionSweepHostedService hosted = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            policy,
            TimeProvider.System,
            admission,
            ownership,
            new TestCapturingLogger<DataRetentionSweepHostedService>());

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => hosted.RunOnceAsync(CancellationToken.None));

        Assert.Contains("no terminal result", failure.Message, StringComparison.Ordinal);

        DataRetentionHostedSweepContinuation retained = Assert.IsType<DataRetentionHostedSweepContinuation>(
            service.Continuation);

        Assert.False(ownership.IsClaimedBy(retained.OperationId, retained.OwnershipToken));

        await ReopenAsync(
            inner,
            Assert.IsAssignableFrom<IGrimoireClosingOwner>(service.Closing));
    }

    [Fact]
    public async Task WorkLeaseRemainsHeldUntilHostedSweepScopeIsDisposed()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        TaskCompletionSource disposalReached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        admission.BeforeWorkLeaseDisposalAsync = async () =>
        {
            disposalReached.TrySetResult();

            await releaseDisposal.Task;
        };

        RecordingHostedRetentionService service = new();

        MutablePolicyStore policy = new(new RetentionSettings { AutomaticSweepsEnabled = true });

        LongRunningOperationOwnership ownership = new();

        await using ServiceProvider provider = Services(service).BuildServiceProvider();

        CountingScopeFactory scopes = new(provider.GetRequiredService<IServiceScopeFactory>());

        using DataRetentionSweepHostedService hosted = new(
            scopes,
            policy,
            TimeProvider.System,
            admission,
            ownership,
            new TestCapturingLogger<DataRetentionSweepHostedService>());

        Task running = hosted.RunOnceAsync(CancellationToken.None);

        await disposalReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, scopes.Disposed);

        IGrimoireClosingOwner closing = inner.BeginOrResumeExclusive(Owner()).Value;

        Task<Result> drain = inner.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None).AsTask();

        Assert.False(drain.IsCompleted);

        releaseDisposal.TrySetResult();

        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        await ReopenClosedAsync(inner, closing);
    }

    [Fact]
    public async Task DeferredContinuationSurvivesScopeDisposalFailureForExactClaimCleanup()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        RecordingGrimoireWorkAdmissionGate admission = new(inner);

        LongRunningOperationOwnership ownership = new();

        DeferredHostedRetentionService service = new(inner, ownership);

        MutablePolicyStore policy = new(new RetentionSettings { AutomaticSweepsEnabled = true });

        await using ServiceProvider provider = Services(service).BuildServiceProvider();

        CountingScopeFactory scopes = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new IOException("scope disposal failed"));

        using DataRetentionSweepHostedService hosted = new(
            scopes,
            policy,
            TimeProvider.System,
            admission,
            ownership,
            new TestCapturingLogger<DataRetentionSweepHostedService>());

        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => hosted.RunOnceAsync(CancellationToken.None));

        Assert.Equal("scope disposal failed", failure.Message);

        DataRetentionHostedSweepContinuation continuation = Assert.IsType<DataRetentionHostedSweepContinuation>(
            service.Continuation);

        Assert.False(ownership.IsClaimedBy(
            continuation.OperationId,
            continuation.OwnershipToken));

        Assert.Equal(1, scopes.Disposed);

        await ReopenAsync(
            inner,
            Assert.IsAssignableFrom<IGrimoireClosingOwner>(service.Closing));
    }

    [Fact]
    public async Task RunOnceAsync_AppliesOnlyWhenAutomaticSweepsAreEnabled()
    {
        RecordingHostedRetentionService service = new();

        MutablePolicyStore policy = new(new RetentionSettings { AutomaticSweepsEnabled = false });

        ServiceCollection services = Services(service);

        await using ServiceProvider provider = services.BuildServiceProvider();

        GrimoireConnectionAdmissionGate admission = new(TimeProvider.System);

        LongRunningOperationOwnership ownership = new();

        DataRetentionSweepHostedService hosted = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            policy,
            TimeProvider.System,
            admission,
            ownership,
            new TestCapturingLogger<DataRetentionSweepHostedService>());

        await hosted.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, service.ApplyCalls);

        policy.Current = policy.Current with { AutomaticSweepsEnabled = true };

        await hosted.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, service.ApplyCalls);

        hosted.Dispose();
    }

    private static ServiceCollection Services(IDataRetentionHostedSweep service)
    {
        ServiceCollection services = new();

        services.AddSingleton(service);

        return services;
    }

    private static async Task<IGrimoireExclusiveClosedLease> CloseAsync(
        GrimoireConnectionAdmissionGate gate,
        IGrimoireClosingOwner closing)
    {
        Result drained = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        return closed.Value;
    }

    private static async Task ReopenAsync(
        GrimoireConnectionAdmissionGate gate,
        IGrimoireClosingOwner closing)
    {
        IGrimoireExclusiveClosedLease closed = await CloseAsync(gate, closing);

        Result reopened = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.IsFailure ? reopened.Error.Message : null);

        await closed.DisposeAsync();

        await closing.DisposeAsync();
    }

    private static async Task ReopenClosedAsync(
        GrimoireConnectionAdmissionGate gate,
        IGrimoireClosingOwner closing)
    {
        Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(
            closing,
            CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        Assert.True((await closed.Value.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None)).IsSuccess);

        await closed.Value.DisposeAsync();

        await closing.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        while (!predicate())
        {
            await Task.Yield();

            timeout.Token.ThrowIfCancellationRequested();
        }
    }

    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(new byte[32]));

    private sealed class CountingScopeFactory(
        IServiceScopeFactory inner,
        Exception? disposalException = null) : IServiceScopeFactory
    {
        private readonly Exception? _disposalException = disposalException;

        internal int Created { get; private set; }

        internal int Disposed { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;

            return new CountingScope(this, inner.CreateScope());
        }

        private sealed class CountingScope(
            CountingScopeFactory owner,
            IServiceScope innerScope) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => innerScope.ServiceProvider;

            public void Dispose()
            {
                innerScope.Dispose();

                owner.Disposed++;

                if (owner._disposalException is not null)
                {
                    throw owner._disposalException;
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (innerScope is IAsyncDisposable asyncScope)
                {
                    await asyncScope.DisposeAsync();
                }
                else
                {
                    innerScope.Dispose();
                }

                owner.Disposed++;

                if (owner._disposalException is not null)
                {
                    throw owner._disposalException;
                }
            }
        }
    }

    private sealed class MutablePolicyStore(RetentionSettings current) : IDataRetentionPolicyStore
    {
        private RetentionSettings _current = current;

        internal int Reads { get; private set; }

        public RetentionSettings Current
        {
            get
            {
                Reads++;

                return _current;
            }
            set => _current = value;
        }

        public Task<Result<RetentionSettings>> UpdateRuleAsync(
            RetentionRuleUpdateRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingHostedRetentionService : IDataRetentionHostedSweep
    {
        public int ApplyCalls { get; private set; }

        public Task<DataRetentionHostedSweepOutcome> ApplyOrResumeHostedPruneAsync(
            DataRetentionHostedSweepContinuation? continuation,
            IGrimoireWorkLease workLease,
            CancellationToken cancellationToken)
        {
            ApplyCalls++;

            DataRetentionApplyResult applied = new(
                Guid.NewGuid(),
                "plan",
                0,
                0,
                0,
                0,
                Reconciled: true,
                [],
                []);

            return Task.FromResult(
                new DataRetentionHostedSweepOutcome(
                    DataRetentionHostedSweepDisposition.Concluded,
                    Continuation: null,
                    Result<DataRetentionApplyResult>.Success(applied)));
        }
    }

    private sealed class DeferredHostedRetentionService(
        GrimoireConnectionAdmissionGate gate,
        LongRunningOperationOwnership ownership,
        bool concludeOnResume = false,
        bool replaceContinuationOnResume = false,
        bool returnResultOnInitialDeferral = false) : IDataRetentionHostedSweep
    {
        internal TaskCompletionSource Deferred { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal IGrimoireClosingOwner? Closing { get; private set; }

        internal DataRetentionHostedSweepContinuation? Continuation { get; private set; }

        internal int ApplyCalls { get; private set; }

        internal bool LastContinuationWasPresent { get; private set; }

        public Task<DataRetentionHostedSweepOutcome> ApplyOrResumeHostedPruneAsync(
            DataRetentionHostedSweepContinuation? continuation,
            IGrimoireWorkLease workLease,
            CancellationToken cancellationToken)
        {
            ApplyCalls++;

            LastContinuationWasPresent = continuation is not null;

            if (continuation is not null && concludeOnResume)
            {
                Assert.Same(Continuation, continuation);

                _ = ownership.Release(
                    continuation.OperationId,
                    continuation.OwnershipToken);

                DataRetentionApplyResult applied = new(
                    continuation.OperationId,
                    "plan",
                    0,
                    0,
                    0,
                    0,
                    Reconciled: true,
                    [],
                    []);

                return Task.FromResult(
                    new DataRetentionHostedSweepOutcome(
                        DataRetentionHostedSweepDisposition.Concluded,
                        Continuation: null,
                        Result<DataRetentionApplyResult>.Success(applied)));
            }

            if (continuation is not null && replaceContinuationOnResume)
            {
                return Task.FromResult(
                    new DataRetentionHostedSweepOutcome(
                        DataRetentionHostedSweepDisposition.DeferredForMaintenance,
                        continuation with { OwnerId = continuation.OwnerId + "-replacement" },
                        Result: null));
            }

            Guid operationId = Guid.NewGuid();

            Assert.True(ownership.TryClaim(operationId, out Guid ownershipToken));

            DataRetentionHostedSweepContinuation retained = new(
                operationId,
                "automatic-retention",
                ownershipToken);

            Continuation = retained;

            Closing = gate.BeginOrResumeExclusive(Owner()).Value;

            Deferred.TrySetResult();

            return Task.FromResult(
                new DataRetentionHostedSweepOutcome(
                    DataRetentionHostedSweepDisposition.DeferredForMaintenance,
                    retained,
                    returnResultOnInitialDeferral
                        ? Result<DataRetentionApplyResult>.Success(
                            new DataRetentionApplyResult(
                                operationId,
                                "invalid-deferred-result",
                                0,
                                0,
                                0,
                                0,
                                Reconciled: true,
                                [],
                                []))
                        : null));
        }
    }
}
