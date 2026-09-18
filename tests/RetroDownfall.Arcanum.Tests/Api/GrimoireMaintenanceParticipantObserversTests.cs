using Microsoft.Data.Sqlite;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]

[Trait("Category", "Integration")]
public sealed class GrimoireMaintenanceParticipantObserversTests
{
    [Fact]
    public void Drain_observer_tracks_reference_counted_active_enrolments_and_idempotent_disposal()
    {
        SqliteNativeRuntime.Instance.Initialize();

        using SqliteConnection connection = new("Data Source=:memory:;Pooling=False");

        connection.Open();

        ObservingCovenantConnectionDrain drain = new(new CovenantConnectionDrain());

        using IDisposable firstEnrolment = drain.Register(connection);

        using IDisposable secondEnrolment = drain.Register(connection);

        Assert.Equal(1, drain.OpenEnrolledConnectionCount);

        Assert.Equal(2, drain.OpenEnrolmentCount);

        Assert.Equal(2, drain.OpenEnrolmentCountFor(connection));

        Assert.Equal(1, drain.RegisteredConnectionCount);

        Assert.Equal(2, drain.RegisteredEnrolmentCount);

        Assert.Equal(2, drain.RegisteredEnrolmentCountFor(connection));

        connection.Close();

        Assert.Equal(0, drain.OpenEnrolledConnectionCount);

        Assert.Equal(0, drain.OpenEnrolmentCount);

        Assert.Equal(0, drain.OpenEnrolmentCountFor(connection));

        Assert.Equal(2, drain.RegisteredEnrolmentCountFor(connection));

        connection.Open();

        DrainEnrolmentObservation[] observations = [.. drain.Enrolments];

        Assert.Equal(2, observations.Length);

        firstEnrolment.Dispose();

        Assert.Equal(1, drain.OpenEnrolledConnectionCount);

        Assert.Equal(1, drain.OpenEnrolmentCount);

        Assert.Equal(1, drain.OpenEnrolmentCountFor(connection));

        Assert.Equal(1, drain.RegisteredConnectionCount);

        Assert.Equal(1, drain.RegisteredEnrolmentCount);

        Assert.Equal(1, drain.RegisteredEnrolmentCountFor(connection));

        Assert.Equal(1, observations[0].Disposals);

        firstEnrolment.Dispose();

        Assert.Equal(1, drain.OpenEnrolledConnectionCount);

        Assert.Equal(1, drain.OpenEnrolmentCount);

        Assert.Equal(1, drain.RegisteredEnrolmentCount);

        Assert.Equal(1, observations[0].Disposals);

        secondEnrolment.Dispose();

        Assert.Equal(0, drain.OpenEnrolledConnectionCount);

        Assert.Equal(0, drain.OpenEnrolmentCount);

        Assert.Equal(0, drain.OpenEnrolmentCountFor(connection));

        Assert.Equal(0, drain.RegisteredConnectionCount);

        Assert.Equal(0, drain.RegisteredEnrolmentCount);

        Assert.Equal(0, drain.RegisteredEnrolmentCountFor(connection));

        Assert.Equal(1, observations[1].Disposals);
    }

    [Fact]
    public async Task Reconciliation_barrier_waits_for_the_exact_next_generation()
    {
        const long expectedGeneration = 43;

        OrdinaryMutationObservations observations = new();

        SessionAttachmentIndexRequest[] pending =
        [
            new(Guid.NewGuid(), Guid.NewGuid(), Attempt: 7),
        ];

        var index = new ObservingSessionAttachmentIndexWriter(
            new ControlledIndexWriter(() => Task.FromResult(pending)),
            observations,
            () => expectedGeneration);

        long completedBefore = observations.Snapshot.Reconciliations;

        Task<IndexingReconciliationObservation> waiting = observations.WaitForNextReconciliationAsync(
            completedBefore,
            expectedGeneration,
            CancellationToken.None);

        Assert.False(waiting.IsCompleted);

        Assert.Same(pending, await index.ReconcileAndFindPendingAsync(64, 8, CancellationToken.None));

        IndexingReconciliationObservation completed = await waiting;

        Assert.Equal(completedBefore + 1, completed.Ordinal);

        Assert.Equal(expectedGeneration, completed.Generation);

        Assert.Equal(completedBefore + 1, observations.Snapshot.Reconciliations);

        await Assert.ThrowsAsync<InvalidOperationException>(() => observations.WaitForNextReconciliationAsync(
            completedBefore,
            expectedGeneration + 1,
            CancellationToken.None));
    }

    [Fact]
    public async Task Mutation_forwarders_preserve_returns_and_count_only_completed_successes()
    {
        OrdinaryMutationObservations observations = new();

        SessionAttachmentIndexRequest[] pending =
        [
            new(Guid.NewGuid(), Guid.NewGuid(), Attempt: 7),
        ];

        Guid billableId = Guid.NewGuid();

        var index = new ObservingSessionAttachmentIndexWriter(
            new ControlledIndexWriter(() => Task.FromResult(pending)), observations, static () => 0);

        var accounting = new ObservingTurnRunWriter(
            new ControlledTurnRunWriter(() => Task.FromResult(billableId)), observations);

        var watermarks = new ObservingUnseenServantWatermarkStore(
            new ControlledWatermarkStore(() => Task.CompletedTask), observations);

        Assert.Same(pending, await index.ReconcileAndFindPendingAsync(64, 8, CancellationToken.None));

        Assert.Equal(billableId, await accounting.RecordBillableOperationAsync(Billable(), CancellationToken.None));

        await watermarks.SaveAsync("observer", DateTimeOffset.UnixEpoch, 5, CancellationToken.None);

        Assert.Equal(
            new OrdinaryMutationCounters(
                Invocations: 3,
                Successes: 3,
                IndexWrites: 0,
                IndexAttempts: 0,
                Billing: 1,
                Failures: 0,
                Watermarks: 1,
                Reconciliations: 1),
            observations.Snapshot);
    }

    [Fact]
    public async Task Mutation_forwarders_preserve_exception_and_cancellation_without_recording_success()
    {
        OrdinaryMutationObservations observations = new();

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        OperationCanceledException cancelled = new(cancellation.Token);

        InvalidOperationException billingFailure = new("billing failed");

        InvalidOperationException watermarkFailure = new("watermark failed");

        var index = new ObservingSessionAttachmentIndexWriter(
            new ControlledIndexWriter(() => Task.FromException<SessionAttachmentIndexRequest[]>(cancelled)),
            observations,
            static () => 0);

        var accounting = new ObservingTurnRunWriter(
            new ControlledTurnRunWriter(() => Task.FromException<Guid>(billingFailure)), observations);

        var watermarks = new ObservingUnseenServantWatermarkStore(
            new ControlledWatermarkStore(() => Task.FromException(watermarkFailure)), observations);

        OperationCanceledException observedCancellation = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            index.ReconcileAndFindPendingAsync(64, 8, cancellation.Token));

        InvalidOperationException observedBilling = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accounting.RecordBillableOperationAsync(Billable(), CancellationToken.None));

        InvalidOperationException observedWatermark = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            watermarks.SaveAsync("observer", DateTimeOffset.UnixEpoch, 5, CancellationToken.None));

        Assert.Same(cancelled, observedCancellation);

        Assert.Equal(cancellation.Token, observedCancellation.CancellationToken);

        Assert.Same(billingFailure, observedBilling);

        Assert.Same(watermarkFailure, observedWatermark);

        Assert.Equal(
            new OrdinaryMutationCounters(
                Invocations: 3,
                Successes: 0,
                IndexWrites: 0,
                IndexAttempts: 0,
                Billing: 0,
                Failures: 0,
                Watermarks: 0,
                Reconciliations: 0),
            observations.Snapshot);
    }

    [Fact]
    public async Task Initial_worker_scope_waits_for_actual_async_disposal_before_participant_seed()
    {
        HeldInitialScopeSentinel sentinel = new();

        await using ServiceProvider services = new ServiceCollection().AddScoped(_ => sentinel).BuildServiceProvider();

        ObservingWorkerScopeFactory factory = new(services.GetRequiredService<IServiceScopeFactory>());

        Task readyToSeed = factory.WaitUntilInitialScopeDisposedAsync();

        Assert.False(readyToSeed.IsCompleted);

        AsyncServiceScope scope = factory.CreateAsyncScope();

        _ = scope.ServiceProvider.GetRequiredService<HeldInitialScopeSentinel>();

        Task disposing = scope.DisposeAsync().AsTask();

        try
        {
            await sentinel.Checkpoint.WaitUntilReachedAsync();

            Assert.False(readyToSeed.IsCompleted);

            Assert.Equal(0, Assert.Single(factory.Scopes).Disposals);

            sentinel.Checkpoint.Release();

            await disposing.WaitAsync(TimeSpan.FromSeconds(10));

            await readyToSeed.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, Assert.Single(factory.Scopes).Disposals);
        }
        finally
        {
            sentinel.Checkpoint.Release();

            await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Worker_scope_disposal_barrier_holds_after_the_inner_scope_is_disposed()
    {
        DisposedScopeSentinel sentinel = new();

        await using ServiceProvider services = new ServiceCollection().AddScoped(_ => sentinel).BuildServiceProvider();

        ObservingWorkerScopeFactory factory = new(services.GetRequiredService<IServiceScopeFactory>());

        WorkerScopeDisposalBarrier barrier = factory.HoldAfterScopeDisposal(scopeOrdinal: 1);

        AsyncServiceScope scope = factory.CreateAsyncScope();

        _ = scope.ServiceProvider.GetRequiredService<DisposedScopeSentinel>();

        Task disposing = scope.DisposeAsync().AsTask();

        try
        {
            await barrier.WaitUntilReachedAsync();

            Assert.Equal(1, sentinel.Disposals);

            Assert.Equal(1, Assert.Single(factory.Scopes).Disposals);

            Assert.False(disposing.IsCompleted);

            barrier.Release();

            await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            barrier.Release();

            await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class HeldInitialScopeSentinel : IAsyncDisposable
    {
        internal MaintenanceCheckpoint Checkpoint { get; } = new();

        public async ValueTask DisposeAsync() => await Checkpoint.PauseAsync(CancellationToken.None);
    }

    private sealed class DisposedScopeSentinel : IAsyncDisposable
    {
        private int _disposals;

        internal int Disposals => Volatile.Read(ref _disposals);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);

            return ValueTask.CompletedTask;
        }
    }

    private static BillableOperationRecord Billable() => new(
        Guid.NewGuid(),
        BillableOperationType.Embedding,
        "test",
        "test",
        "observer",
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        0,
        0,
        0,
        0,
        "{}",
        0m,
        BillableOperationStatus.Completed,
        null);

    private sealed class ControlledIndexWriter(
        Func<Task<SessionAttachmentIndexRequest[]>> reconcile) : ISessionAttachmentIndexWriter
    {
        public Task<SessionAttachmentIndexRequest[]> ReconcileAndFindPendingAsync(
            int expectedDimensions,
            int maxAttachments,
            CancellationToken cancellationToken) => reconcile();

        public Task SetPendingAsync(
            SessionAttachmentRecord attachment,
            int attempt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MarkWithoutIndexAsync(
            Guid attachmentId,
            string contentSha256,
            SessionAttachmentIndexStatus status,
            int attempt,
            string? failureReason,
            DateTimeOffset? extractedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SessionAttachmentIndexCheckpoint> BeginReplaceAsync(
            SessionAttachmentRecord attachment,
            int expectedDimensions,
            string pipelineFingerprint,
            DateTimeOffset extractedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AppendReplaceBatchAsync(
            SessionAttachmentRecord attachment,
            string generationId,
            IReadOnlyList<SessionAttachmentTextChunk> chunks,
            IReadOnlyList<Embedding<float>> embeddings,
            int expectedDimensions,
            DateTimeOffset extractedAt,
            DateTimeOffset indexedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CompleteReplaceAsync(
            SessionAttachmentRecord attachment,
            string generationId,
            int expectedChunkCount,
            DateTimeOffset extractedAt,
            DateTimeOffset indexedAt,
            int attempt,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ControlledTurnRunWriter(
        Func<Task<Guid>> bill) : ITurnRunWriter
    {
        public Task<Guid> StartRunAsync(
            InferenceRunStart start,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CompleteRunAsync(
            Guid runId,
            InferenceRunStatus status,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TryAbandonRunAsync(
            Guid runId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Guid> RecordBillableOperationAsync(
            BillableOperationRecord operation,
            CancellationToken cancellationToken = default) => bill();
    }

    private sealed class ControlledWatermarkStore(
        Func<Task> save) : IUnseenServantWatermarkStore
    {
        public Task<UnseenServantWatermark?> GetAsync(
            string jobKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveAsync(
            string jobKey,
            DateTimeOffset lastRunAt,
            int effectiveIntervalMinutes,
            CancellationToken cancellationToken = default) => save();

        public Task SaveLastRunAsync(
            string jobKey,
            DateTimeOffset lastRunAt,
            int initialIntervalMinutes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveIntervalAsync(
            string jobKey,
            DateTimeOffset initialLastRunAt,
            int effectiveIntervalMinutes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<UnseenServantWatermark>> GetAllAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(
            string jobKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Effect_observation_keeps_the_real_work_lifetime_until_its_terminal_disposal()
    {
        GrimoireMaintenanceAdmissionObserver observer = new(new GrimoireConnectionAdmissionGate(TimeProvider.System));

        Assert.True(observer.TryAcquireWorkLease(GrimoireWorkKind.SessionAttachmentIndexing, out IGrimoireWorkLease? work));

        await using IGrimoireWorkLease held = work!;

        Assert.True(work!.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect));

        await using IGrimoireExternalEffectGroup heldEffect = effect!;

        Assert.False(work.TryBeginExternalEffectGroup(out _));

        await using IGrimoireClosingOwner closing = observer.BeginOrResumeExclusive(Owner()).Value;

        Assert.False(work.TryBeginExternalEffectGroup(out _));

        await work.DisposeAsync();

        Task<Result> drain = observer.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        Assert.False(drain.IsCompleted);

        Assert.Equal(0, Assert.Single(observer.Effects).TerminalCount);

        await effect!.DisposeAsync();

        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Assert.Equal(1, observer.Effects[0].Disposals);

        Assert.Equal(1, observer.Effects[0].TerminalCount);
    }

    [Theory]
    [InlineData("opened")]
    [InlineData("failed")]
    [InlineData("refused-after-open")]
    public async Task Opening_ticket_reports_one_real_terminal_and_preserves_reuse_failure(string terminal)
    {
        GrimoireMaintenanceAdmissionObserver observer = new(new GrimoireConnectionAdmissionGate(TimeProvider.System));

        using SqliteConnection connection = new();

        using IGrimoireConnectionOpenTicket ticket = observer.AcquireOrdinaryOpen(connection);

        Assert.Equal(1, Assert.Single(observer.Tickets).Generation);

        IGrimoireClosingOwner? closing = null;

        Task<Result<IGrimoireExclusiveClosedLease>>? closingTask = null;

        try
        {
            if (terminal == "opened")
            {
                Assert.True(ticket.RevalidateAfterNativeOpen().IsSuccess);

                Assert.True(ticket.MarkOpened().IsSuccess);

                Assert.Throws<InvalidOperationException>(() => ticket.MarkOpened());
            }
            else if (terminal == "failed")
            {
                ticket.MarkFailed();

                Assert.Throws<InvalidOperationException>(ticket.MarkFailed);
            }
            else
            {
                closing = observer.BeginOrResumeExclusive(Owner()).Value;

                closingTask = observer.CloseConnectionAdmissionAsync(closing, CancellationToken.None).AsTask();

                await observer.StageTwo.WaitUntilEnteredAsync();

                Assert.False(closingTask.IsCompleted);

                Assert.Equal(0, observer.StageTwo.CompletedCount);

                Assert.True(ticket.RevalidateAfterNativeOpen().IsFailure);

                ticket.MarkRefusedAfterOpen();

                Assert.Throws<InvalidOperationException>(ticket.MarkRefusedAfterOpen);
            }

            Assert.Equal(terminal, observer.Tickets[0].Terminal);

            Assert.Equal(1, observer.Tickets[0].TerminalCount);
        }
        finally
        {
            if (closingTask is not null)
            {
                await using IGrimoireExclusiveClosedLease closed = (await closingTask.WaitAsync(TimeSpan.FromSeconds(10))).Value;

                Assert.Equal(1, observer.StageTwo.CompletedCount);
            }

            if (closing is not null)
            {
                await closing.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_and_closed_disposal_never_invent_a_reopening_disposition(bool complete)
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        GrimoireMaintenanceAdmissionObserver observer = new(inner);

        IGrimoireClosingOwner closing = observer.BeginOrResumeExclusive(Owner()).Value;

        IGrimoireExclusiveClosedLease closed = (await observer.CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        if (complete)
        {
            Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

            Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, CancellationToken.None)).IsFailure);

            Assert.Equal(CovenantExclusiveLeaseDisposition.RollbackAndReopen, Assert.Single(observer.ClosedLeases[0].Dispositions));
        }

        await closed.DisposeAsync();

        await closing.DisposeAsync();

        Assert.Equal(1, Assert.Single(observer.ClosingOwners).Disposals);

        Assert.Equal(1, Assert.Single(observer.ClosedLeases).Disposals);

        Assert.Equal(complete ? 1 : 0, observer.ClosedLeases[0].Dispositions.Count);

        Assert.Equal(complete, inner.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? lease));

        if (lease is not null)
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task Admission_observer_preserves_live_leases_and_inner_refusals()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        GrimoireMaintenanceAdmissionObserver observer = new(inner);

        Assert.True(observer.TryAcquireRequestLease(
            GrimoireRequestKind.QuiesceableStream,
            out IGrimoireRequestLease? request));

        Assert.True(observer.TryAcquireWorkLease(
            GrimoireWorkKind.SessionAttachmentIndexing,
            out IGrimoireWorkLease? work));

        await using IGrimoireRequestLease heldRequest = request!;

        await using IGrimoireWorkLease heldWork = work!;

        Assert.Equal(GrimoireRequestKind.QuiesceableStream, request!.Kind);

        Assert.Equal(GrimoireWorkKind.SessionAttachmentIndexing, work!.Kind);

        Assert.Equal(inner.CurrentGeneration, request.Generation);

        Assert.Equal(inner.CurrentGeneration, work.Generation);

        Assert.Equal(observer.InnerRequest(request).MaintenanceRevocation, request.MaintenanceRevocation);

        Assert.Equal(observer.InnerWork(work).MaintenanceRevocation, work.MaintenanceRevocation);

        await using IGrimoireClosingOwner closing = inner.BeginOrResumeExclusive(Owner()).Value;

        Assert.True(request.MaintenanceRevocation.IsCancellationRequested);

        Assert.True(work.MaintenanceRevocation.IsCancellationRequested);

        Assert.False(observer.TryAcquireRequestLease(GrimoireRequestKind.Finite, out _));

        Assert.False(observer.TryAcquireWorkLease(GrimoireWorkKind.EntryWeaving, out _));

        Assert.False(inner.TryAcquireRequestLease(GrimoireRequestKind.Finite, out _));

        Assert.False(inner.TryAcquireWorkLease(GrimoireWorkKind.EntryWeaving, out _));

        await request.DisposeAsync();

        await work.DisposeAsync();

        Assert.Equal(2, observer.RequestAttempts.Count);

        Assert.Equal(2, observer.WorkAttempts.Count);

        Assert.Equal(1, observer.RequestAttempts[0].Disposals);

        Assert.Equal(1, observer.WorkAttempts[0].Disposals);
    }

    [Fact]
    public async Task Only_this_observers_exact_capabilities_are_unwrapped_at_closure_reentry()
    {
        GrimoireConnectionAdmissionGate inner = new(TimeProvider.System);

        GrimoireMaintenanceAdmissionObserver observer = new(inner);

        Assert.True(observer.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? request));

        await using IGrimoireRequestLease held = request!;

        using SqliteConnection connection = new();

        ForeignRequest foreignRequest = new(request!);

        Assert.True(observer.BeginOrResumeExclusive(Owner(), foreignRequest, connection).IsFailure);

        Result<IGrimoireClosingOwner> begun = observer.BeginOrResumeExclusive(Owner(), request, connection);

        Assert.True(begun.IsSuccess, begun.Error.Message);

        await using IGrimoireClosingOwner closing = begun.Value;

        Assert.NotSame(inner.BeginOrResumeExclusive(Owner()).Value, closing);

        Assert.Same(closing, observer.BeginOrResumeExclusive(Owner()).Value);

        ForeignOwner foreignOwner = new(closing);

        Assert.True((await observer.DrainRequestAndWorkAsync(foreignOwner, CancellationToken.None)).IsFailure);

        Assert.True((await observer.CloseConnectionAdmissionAsync(foreignOwner, CancellationToken.None)).IsFailure);

        Assert.True((await observer.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        Result<IGrimoireExclusiveClosedLease> result = await observer.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        await using IGrimoireExclusiveClosedLease closed = result.Value;

        Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

        Assert.True(inner.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? reopened));

        await reopened!.DisposeAsync();
    }

    [Fact]
    public async Task Abort_forwards_the_exact_owner_and_preserves_foreign_owner_refusal()
    {
        ControlledClock clock = new();

        GrimoireConnectionAdmissionGate inner = new(clock, new CovenantConnectionDrain(), TimeSpan.FromSeconds(30));

        GrimoireMaintenanceAdmissionObserver observer = new(inner);

        Assert.True(observer.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? request));

        await using IGrimoireRequestLease held = request!;

        await using IGrimoireClosingOwner closing = observer.BeginOrResumeExclusive(Owner()).Value;

        Assert.NotSame(inner.BeginOrResumeExclusive(Owner()).Value, closing);

        Task<Result> draining = observer.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        clock.Expire();

        Result drained = await draining.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ErrorCodes.Grimoire.WorkDrainTimeout, drained.Error.Code);

        Assert.True((await observer.AbortClosingAsync(new ForeignOwner(closing), static _ => ValueTask.FromResult(true), CancellationToken.None)).IsFailure);

        Assert.True((await observer.AbortClosingAsync(closing, static _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

        Assert.Equal(2, inner.CurrentGeneration);
    }

    private sealed class ControlledClock : TimeProvider
    {
        private Action? _expire;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _expire = () => callback(state);

            return new NoopTimer();
        }

        internal void Expire() => Assert.IsType<Action>(_expire).Invoke();

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ForeignRequest(IGrimoireRequestLease inner) : IGrimoireRequestLease
    {
        public GrimoireRequestKind Kind => inner.Kind;

        public long Generation => inner.Generation;

        public CancellationToken MaintenanceRevocation => inner.MaintenanceRevocation;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ForeignOwner(IGrimoireClosingOwner inner) : IGrimoireClosingOwner
    {
        public CovenantExclusiveRecoveryOwner Owner => inner.Owner;

        public long Generation => inner.Generation;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(
            Guid.Parse("25700000-0000-0000-0000-000000000003"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat((byte)3, 32).ToArray()));
}
