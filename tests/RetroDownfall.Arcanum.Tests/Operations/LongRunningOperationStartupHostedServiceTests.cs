using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed class LongRunningOperationStartupHostedServiceTests
{
    [Fact]
    public async Task Background_db_only_recovery_acquires_before_each_private_scope_without_a_group()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        _ = store.Seed(
            LongRunningOperationKinds.WorkspaceIndex,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);
        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.WorkspaceIndex,
            supportedCheckpointVersion: 0);
        List<string> order = [];
        RecoveryAdmissionGate gate = new(order);
        RecoveryScopeFactory scopes = new(
            store,
            Reconciler(store, time, handler),
            order,
            () => gate.ActiveLeases > 0);
        LongRunningOperationStartupHostedService host = Host(scopes, time, gate);

        LongRunningOperationReconciliationSummary summary = await host.RunBackgroundPassAsync(
            time.GetUtcNow(),
            "background-owner",
            CancellationToken.None);

        Assert.Equal(1, summary.Completed);
        Assert.Equal(2, gate.Acquisitions);
        Assert.Equal(0, gate.GroupAttempts);
        Assert.Equal(2, scopes.Created);
        Assert.Equal(2, scopes.Disposed);
        Assert.All(scopes.ScopeCreatedWhileAdmitted, Assert.True);
        Assert.Equal(1, gate.MaximumActiveLeases);

        int discoveryLeaseDisposal = order.IndexOf("work-dispose");

        int operationLeaseAcquisition = order.IndexOf(
            "work-acquire",
            order.IndexOf("work-acquire") + 1);

        Assert.True(discoveryLeaseDisposal >= 0);
        Assert.True(operationLeaseAcquisition > discoveryLeaseDisposal);
    }

    [Fact]
    public async Task External_group_spans_claim_handler_and_settlement_then_closes_before_scope()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        LongRunningOperation seeded = store.Seed(
            LongRunningOperationKinds.Batch,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);
        List<string> order = [];
        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.Batch,
            supportedCheckpointVersion: 0,
            _ =>
            {
                order.Add("handler");

                return LongRunningOperationRecoveryResult.Completed();
            });
        RecoveryAdmissionGate gate = new(order)
        {
            BeforeGroupDisposal = () =>
            {
                LongRunningOperation settled = Assert.Single(
                    store.Operations,
                    operation => operation.Id == seeded.Id);
                Assert.Equal(LongRunningOperationState.Completed, settled.State);
            },
        };
        RecoveryScopeFactory scopes = new(
            store,
            Reconciler(store, time, handler),
            order,
            () => gate.ActiveLeases > 0);
        LongRunningOperationStartupHostedService host = Host(scopes, time, gate);

        LongRunningOperationReconciliationSummary summary = await host.RunBackgroundPassAsync(
            time.GetUtcNow(),
            "background-owner",
            CancellationToken.None);

        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, gate.GroupAttempts);
        Assert.True(order.IndexOf("handler") < order.IndexOf("group-dispose"));
        Assert.True(order.IndexOf("group-dispose") < order.LastIndexOf("scope-dispose"));
        Assert.True(order.LastIndexOf("scope-dispose") < order.LastIndexOf("work-dispose"));
    }

    [Fact]
    public async Task Lost_external_frontier_waits_without_claiming_then_resumes_the_same_identity()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        LongRunningOperation seeded = store.Seed(
            LongRunningOperationKinds.Batch,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);
        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.Batch,
            supportedCheckpointVersion: 0);
        List<string> order = [];
        RecoveryAdmissionGate gate = new(order) { RefuseFirstGroup = true };
        RecoveryScopeFactory scopes = new(
            store,
            Reconciler(store, time, handler),
            order,
            () => gate.ActiveLeases > 0);
        LongRunningOperationStartupHostedService host = Host(scopes, time, gate);

        LongRunningOperationReconciliationSummary summary = await host.RunBackgroundPassAsync(
            time.GetUtcNow(),
            "background-owner",
            CancellationToken.None);

        LongRunningOperation settled = Assert.Single(
            store.Operations,
            operation => operation.Id == seeded.Id);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(2, gate.GroupAttempts);
        Assert.Equal(1, gate.GenerationWaits);
        Assert.Equal(seeded.AttemptCount + 1, settled.AttemptCount);
        Assert.Equal([seeded.Id], handler.Invocations);
    }

    [Fact]
    public async Task A_group_disposal_failure_still_disposes_the_private_scope_and_work_lease()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        _ = store.Seed(
            LongRunningOperationKinds.Batch,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);
        List<string> order = [];
        RecoveryAdmissionGate gate = new(order) { ThrowOnGroupDisposal = true };
        RecoveryScopeFactory scopes = new(
            store,
            Reconciler(
                store,
                time,
                new RecordingRecoveryHandler(
                    LongRunningOperationKinds.Batch,
                    supportedCheckpointVersion: 0)),
            order,
            () => gate.ActiveLeases > 0);
        LongRunningOperationStartupHostedService host = Host(scopes, time, gate);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.RunBackgroundPassAsync(
                time.GetUtcNow(),
                "background-owner",
                CancellationToken.None));

        Assert.Equal(2, scopes.Disposed);
        Assert.Equal(0, gate.ActiveLeases);
        Assert.True(order.IndexOf("group-dispose") < order.LastIndexOf("scope-dispose"));
        Assert.True(order.LastIndexOf("scope-dispose") < order.LastIndexOf("work-dispose"));
    }

    [Fact]
    public async Task A_private_scope_disposal_failure_still_disposes_the_work_lease()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        _ = store.Seed(
            LongRunningOperationKinds.WorkspaceIndex,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);
        List<string> order = [];
        RecoveryAdmissionGate gate = new(order);
        RecoveryScopeFactory scopes = new(
            store,
            Reconciler(
                store,
                time,
                new RecordingRecoveryHandler(
                    LongRunningOperationKinds.WorkspaceIndex,
                    supportedCheckpointVersion: 0)),
            order,
            () => gate.ActiveLeases > 0)
        {
            ThrowOnSecondDisposal = true,
        };
        LongRunningOperationStartupHostedService host = Host(scopes, time, gate);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.RunBackgroundPassAsync(
                time.GetUtcNow(),
                "background-owner",
                CancellationToken.None));

        Assert.Equal(2, scopes.Disposed);
        Assert.Equal(0, gate.ActiveLeases);
        Assert.True(order.LastIndexOf("scope-dispose") < order.LastIndexOf("work-dispose"));
    }

    private static LongRunningOperationStartupHostedService Host(
        IServiceScopeFactory scopes,
        TimeProvider time,
        IGrimoireConnectionAdmissionGate gate) =>
        new(
            scopes,
            time,
            new LongRunningOperationReconciliationStatus(),
            gate,
            NullLogger<LongRunningOperationStartupHostedService>.Instance);

    private static LongRunningOperationReconciler Reconciler(
        FakeLongRunningOperationStore store,
        TimeProvider time,
        params ILongRunningOperationRecoveryHandler[] handlers) =>
        new(
            store,
            handlers,
            time,
            NullLogger<LongRunningOperationReconciler>.Instance,
            new LongRunningOperationOwnership());

    private sealed class RecoveryScopeFactory(
        FakeLongRunningOperationStore store,
        LongRunningOperationReconciler reconciler,
        List<string> order,
        Func<bool> isAdmitted) : IServiceScopeFactory
    {
        private readonly FakeLongRunningOperationStore _store = store;

        private readonly LongRunningOperationReconciler _reconciler = reconciler;

        private readonly List<string> _order = order;

        private readonly Func<bool> _isAdmitted = isAdmitted;

        private int _created;
        private int _disposed;

        internal int Created => Volatile.Read(ref _created);

        internal int Disposed => Volatile.Read(ref _disposed);

        internal bool ThrowOnSecondDisposal { get; init; }

        internal List<bool> ScopeCreatedWhileAdmitted { get; } = [];

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _created);
            ScopeCreatedWhileAdmitted.Add(_isAdmitted());
            _order.Add("scope-create");

            return new Scope(this);
        }

        private sealed class Scope(RecoveryScopeFactory owner)
            : IServiceScope, IServiceProvider, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) => serviceType switch
            {
                _ when serviceType == typeof(ILongRunningOperationGenericRecoveryDiscovery) => owner._store,
                _ when serviceType == typeof(LongRunningOperationReconciler) => owner._reconciler,
                _ => null,
            };

            public void Dispose()
            {
                int disposed = Interlocked.Increment(ref owner._disposed);
                owner._order.Add("scope-dispose");

                if (owner.ThrowOnSecondDisposal && disposed == 2)
                {
                    throw new InvalidOperationException("scope disposal failed");
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecoveryAdmissionGate(List<string> order) : IGrimoireConnectionAdmissionGate
    {
        private readonly List<string> _order = order;

        private long _generation = 1;
        private int _activeLeases;
        private int _maximumActiveLeases;

        internal int Acquisitions { get; private set; }

        internal int GroupAttempts { get; private set; }

        internal int GenerationWaits { get; private set; }

        internal int ActiveLeases => Volatile.Read(ref _activeLeases);

        internal int MaximumActiveLeases => Volatile.Read(ref _maximumActiveLeases);

        internal bool RefuseFirstGroup { get; init; }

        internal bool ThrowOnGroupDisposal { get; init; }

        internal Action BeforeGroupDisposal { get; init; } = static () => { };

        public long CurrentGeneration => Volatile.Read(ref _generation);

        public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease? lease)
        {
            Assert.Equal(GrimoireWorkKind.LongRunningOperationRecovery, kind);
            Acquisitions++;
            int active = Interlocked.Increment(ref _activeLeases);

            int maximum;

            while (active > (maximum = Volatile.Read(ref _maximumActiveLeases))
                && Interlocked.CompareExchange(ref _maximumActiveLeases, active, maximum) != maximum)
            {
            }

            _order.Add("work-acquire");
            lease = new WorkLease(this, CurrentGeneration);

            return true;
        }

        public async Task<long> WaitForNextOpenGenerationAsync(
            long observedGeneration,
            CancellationToken cancellationToken)
        {
            GenerationWaits++;
            cancellationToken.ThrowIfCancellationRequested();
            long next = Interlocked.Increment(ref _generation);
            await Task.Yield();

            return next;
        }

        public bool TryAcquireRequestLease(GrimoireRequestKind kind, out IGrimoireRequestLease? lease) =>
            throw new NotSupportedException();

        public IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection) =>
            throw new NotSupportedException();

        public Result<IGrimoireClosingOwner> BeginOrResumeExclusive(
            CovenantExclusiveRecoveryOwner owner,
            IGrimoireRequestLease? initiatingRequest = null,
            DbConnection? scopedConnection = null) =>
            throw new NotSupportedException();

        public ValueTask<Result> DrainRequestAndWorkAsync(
            IGrimoireClosingOwner closingOwner,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(
            IGrimoireClosingOwner closingOwner,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result> AbortClosingAsync(
            IGrimoireClosingOwner closingOwner,
            Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>> AcquireExpiredLeaseAdoptionInterlockAsync(
            CovenantExclusiveRecoveryOwner candidateOwner,
            Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>> revalidateDurableOwnerAsync,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private sealed class WorkLease(RecoveryAdmissionGate owner, long generation) : IGrimoireWorkLease
        {
            public GrimoireWorkKind Kind => GrimoireWorkKind.LongRunningOperationRecovery;

            public long Generation => generation;

            public CancellationToken MaintenanceRevocation => CancellationToken.None;

            public bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup)
            {
                owner.GroupAttempts++;
                bool refuse = owner.RefuseFirstGroup && owner.GroupAttempts == 1;
                effectGroup = refuse ? null : new Group(owner);

                return !refuse;
            }

            public ValueTask DisposeAsync()
            {
                owner._order.Add("work-dispose");
                Interlocked.Decrement(ref owner._activeLeases);

                return ValueTask.CompletedTask;
            }
        }

        private sealed class Group(RecoveryAdmissionGate owner) : IGrimoireExternalEffectGroup
        {
            public ValueTask DisposeAsync()
            {
                owner.BeforeGroupDisposal();
                owner._order.Add("group-dispose");

                if (owner.ThrowOnGroupDisposal)
                {
                    throw new InvalidOperationException("group disposal failed");
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
