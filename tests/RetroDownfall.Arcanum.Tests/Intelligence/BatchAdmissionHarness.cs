using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>Task-local, thread-safe observations around the real admission state machine.</summary>
internal sealed class BatchAdmissionGate : IGrimoireConnectionAdmissionGate
{
    internal GrimoireConnectionAdmissionGate Inner { get; } = new(TimeProvider.System);

    internal ConcurrentQueue<string> Events { get; } = new();

    internal ConcurrentQueue<long> WaitGenerations { get; } = new();

    internal long? ObservedGeneration { get; set; }

    internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Action<int>? BeforeLease { get; set; }

    internal Action<int>? BeforeGroup { get; set; }

    internal Action<int>? OnWait { get; set; }

    private int _waits;

    internal Func<ValueTask>? BeforeGroupDisposal { get; set; }

    internal Func<ValueTask>? BeforeLeaseDisposal { get; set; }

    private int _leaseAttempts;

    private int _groupAttempts;

    private int _activeLeases;

    private int _activeGroups;

    internal int LeaseAttempts => Volatile.Read(ref _leaseAttempts);

    internal int GroupAttempts => Volatile.Read(ref _groupAttempts);

    internal int ActiveLeases => Volatile.Read(ref _activeLeases);

    internal int ActiveGroups => Volatile.Read(ref _activeGroups);

    public long CurrentGeneration => ObservedGeneration ?? Inner.CurrentGeneration;

    internal IGrimoireClosingOwner Close()
    {
        Result<IGrimoireClosingOwner> result = Inner.BeginOrResumeExclusive(new(
            Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat((byte)7, 32).ToArray())));

        Assert.True(result.IsSuccess);

        return result.Value;
    }

    internal async Task ReopenAsync(IGrimoireClosingOwner closing)
    {
        Assert.True((await Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        Result<IGrimoireExclusiveClosedLease> result = await Inner.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(result.IsSuccess);

        await using IGrimoireExclusiveClosedLease closed = result.Value;

        Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

        await closing.DisposeAsync();
    }

    public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease? lease)
    {
        int attempt = Interlocked.Increment(ref _leaseAttempts);

        BeforeLease?.Invoke(attempt);

        Assert.Equal(GrimoireWorkKind.BatchProcessing, kind);

        Events.Enqueue("lease-attempt");

        if (!Inner.TryAcquireWorkLease(kind, out IGrimoireWorkLease? admitted))
        {
            lease = null;

            return false;
        }

        Interlocked.Increment(ref _activeLeases);

        Events.Enqueue("lease-open");

        lease = new Lease(this, admitted!);

        return true;
    }

    public Task<long> WaitForNextOpenGenerationAsync(long observedGeneration, CancellationToken cancellationToken)
    {
        WaitGenerations.Enqueue(observedGeneration);

        Events.Enqueue("wait");

        OnWait?.Invoke(Interlocked.Increment(ref _waits));

        Waiting.TrySetResult();

        return Inner.WaitForNextOpenGenerationAsync(observedGeneration, cancellationToken);
    }

    private sealed class Lease(BatchAdmissionGate owner, IGrimoireWorkLease inner) : IGrimoireWorkLease
    {
        public GrimoireWorkKind Kind => inner.Kind;

        public long Generation => inner.Generation;

        public CancellationToken MaintenanceRevocation => inner.MaintenanceRevocation;

        public bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup)
        {
            int attempt = Interlocked.Increment(ref owner._groupAttempts);

            owner.BeforeGroup?.Invoke(attempt);

            owner.Events.Enqueue("group-attempt");

            if (!inner.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? admitted))
            {
                effectGroup = null;

                return false;
            }

            Interlocked.Increment(ref owner._activeGroups);

            owner.Events.Enqueue("group-open");

            effectGroup = new Group(owner, admitted!);

            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (owner.BeforeLeaseDisposal is { } before)
            {
                await before();
            }

            await inner.DisposeAsync();

            Interlocked.Decrement(ref owner._activeLeases);

            owner.Events.Enqueue("lease-close");
        }
    }

    private sealed class Group(BatchAdmissionGate owner, IGrimoireExternalEffectGroup inner) : IGrimoireExternalEffectGroup
    {
        public async ValueTask DisposeAsync()
        {
            if (owner.BeforeGroupDisposal is { } before)
            {
                await before();
            }

            await inner.DisposeAsync();

            Interlocked.Decrement(ref owner._activeGroups);

            owner.Events.Enqueue("group-close");
        }
    }

    public bool TryAcquireRequestLease(GrimoireRequestKind kind, out IGrimoireRequestLease? lease) => Inner.TryAcquireRequestLease(kind, out lease);

    public IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection) => Inner.AcquireOrdinaryOpen(connection);

    public Result<IGrimoireClosingOwner> BeginOrResumeExclusive(CovenantExclusiveRecoveryOwner owner, IGrimoireRequestLease? initiatingRequest = null, DbConnection? scopedConnection = null) => Inner.BeginOrResumeExclusive(owner, initiatingRequest, scopedConnection);

    public ValueTask<Result> DrainRequestAndWorkAsync(IGrimoireClosingOwner closingOwner, CancellationToken cancellationToken) => Inner.DrainRequestAndWorkAsync(closingOwner, cancellationToken);

    public ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(IGrimoireClosingOwner closingOwner, CancellationToken cancellationToken) => Inner.CloseConnectionAdmissionAsync(closingOwner, cancellationToken);

    public ValueTask<Result> AbortClosingAsync(IGrimoireClosingOwner closingOwner, Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync, CancellationToken cancellationToken) => Inner.AbortClosingAsync(closingOwner, proveNoDestructiveEffectAsync, cancellationToken);

    public ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>> AcquireExpiredLeaseAdoptionInterlockAsync(CovenantExclusiveRecoveryOwner candidateOwner, Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>> revalidateDurableOwnerAsync, CancellationToken cancellationToken) => Inner.AcquireExpiredLeaseAdoptionInterlockAsync(candidateOwner, revalidateDurableOwnerAsync, cancellationToken);
}

internal sealed class BatchAdmissionScopes(IServiceScopeFactory inner, BatchAdmissionGate gate) : IServiceScopeFactory
{
    internal ConcurrentQueue<int> CreationLeaseCounts { get; } = new();

    internal int Active => Volatile.Read(ref _active);

    internal Func<ValueTask>? BeforeDisposal { get; set; }

    internal Action? BeforeCreation { get; set; }

    private int _active;

    public IServiceScope CreateScope()
    {
        BeforeCreation?.Invoke();

        CreationLeaseCounts.Enqueue(gate.ActiveLeases);

        Interlocked.Increment(ref _active);

        gate.Events.Enqueue("scope-open");

        return new Scope(this, inner.CreateScope(), gate);
    }

    private sealed class Scope(BatchAdmissionScopes owner, IServiceScope inner, BatchAdmissionGate gate) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public void Dispose()
        {
            gate.Events.Enqueue("scope-sync-close");

            inner.Dispose();

            Interlocked.Decrement(ref owner._active);
        }

        public async ValueTask DisposeAsync()
        {
            if (owner.BeforeDisposal is { } before)
            {
                await before();
            }

            if (inner is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                inner.Dispose();
            }

            Interlocked.Decrement(ref owner._active);

            gate.Events.Enqueue("scope-close");
        }
    }
}

internal sealed class BatchAdmissionAccounting(BatchAdmissionGate gate) : ITurnRunWriter, IBudgetReservationService
{
    internal int Runs => Volatile.Read(ref _runs);

    internal int Reservations => Volatile.Read(ref _reservations);

    internal int RejectReservation { get; set; }

    internal Func<Task>? BeforeReconcile { get; set; }

    private int _runs;

    private int _reservations;

    public Task<Guid> StartRunAsync(InferenceRunStart start, CancellationToken cancellationToken = default)
    {
        Assert.Equal(1, gate.ActiveGroups);

        Interlocked.Increment(ref _runs);

        gate.Events.Enqueue("accounting-begin");

        return Task.FromResult(Guid.NewGuid());
    }

    public Task CompleteRunAsync(Guid runId, InferenceRunStatus status, CancellationToken cancellationToken = default)
    {
        Assert.Equal(1, gate.ActiveGroups);

        gate.Events.Enqueue("accounting-complete");

        return Task.CompletedTask;
    }

    public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<Guid> RecordBillableOperationAsync(BillableOperationRecord operation, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());

    public Task<Result<BudgetReservation>> ReserveAsync(BudgetReservationRequest request, CancellationToken cancellationToken = default)
    {
        Assert.Equal(1, gate.ActiveGroups);

        int number = Interlocked.Increment(ref _reservations);

        return Task.FromResult(number == RejectReservation
            ? Result<BudgetReservation>.Failure(new Error("test-budget-policy", "Budget rejected."))
            : Result<BudgetReservation>.Success(new BudgetReservation(Guid.NewGuid(), request.RunId,
                request.BudgetPeriod, request.ReservedUsd, 0m, BudgetReservationStatus.Reserved,
                request.ExpiresAt, DateTimeOffset.UtcNow)));
    }

    public async Task ReconcileAsync(Guid reservationId, decimal actualCostUsd, CancellationToken cancellationToken = default)
    {
        Assert.Equal(1, gate.ActiveGroups);

        Assert.False(cancellationToken.CanBeCanceled);

        if (BeforeReconcile is { } before) { await before(); }

        gate.Events.Enqueue("accounting-reconciled");
    }

    public Task<Result> AdjustAsync(Guid reservationId, decimal reservedUsd, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());

    public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default) => Task.FromResult(0m);

    public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default) => Task.FromResult(0m);

    public Task<int> SweepExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default) => Task.FromResult(0);
}

internal sealed class BatchAdmissionFiles(IUploadedFileRepository inner,
    Func<UploadedFileRecord, Task> afterCreate,
    Func<UploadedFileRecord, Task>? beforeCreate = null) : IUploadedFileRepository
{
    public async Task CreateForOwnedFileAsync(UploadedFileRecord record, CancellationToken cancellationToken = default)
    {
        if (beforeCreate is not null) { await beforeCreate(record); }

        await inner.CreateForOwnedFileAsync(record, cancellationToken);

        await afterCreate(record);
    }

    public Task CreateAsync(UploadedFileRecord record, CancellationToken cancellationToken = default) => inner.CreateAsync(record, cancellationToken);

    public Task<UploadedFileRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<UploadedFileRecord>> ListAsync(string? purpose, CancellationToken cancellationToken = default) => inner.ListAsync(purpose, cancellationToken);

    public Task<UploadedFileDeleteStatus> TryDeleteUnreferencedAsync(Guid id, CancellationToken cancellationToken = default) => inner.TryDeleteUnreferencedAsync(id, cancellationToken);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);
}

internal sealed class BatchAdmissionBlobs(IEncryptedBlobStore inner, BatchAdmissionGate gate) : IEncryptedBlobStore
{
    internal int CreatedWriters => Volatile.Read(ref _createdWriters);

    internal bool RequireReadFrontier { get; set; }

    internal Action<string>? BeforeArtifactRead { get; set; }

    internal int ActiveInputs => Volatile.Read(ref _activeInputs);

    private int _createdWriters;

    private int _activeInputs;

    public async Task<Stream> OpenReadAsync(string path, EncryptedBlobPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (purpose == EncryptedBlobPurpose.BatchArtifact) { BeforeArtifactRead?.Invoke(path); }

        Stream source = await inner.OpenReadAsync(path, purpose, cancellationToken);

        if (purpose != EncryptedBlobPurpose.UploadedFile)
        {
            return source;
        }

        Interlocked.Increment(ref _activeInputs);

        return new Input(source, this, gate);
    }

    public Task<EncryptedBlobWriter> CreateWriterAsync(string destinationPath, EncryptedBlobPurpose purpose, ReadOnlyMemory<byte> authenticatedMetadata = default, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _createdWriters);

        gate.Events.Enqueue("writer-create");

        return inner.CreateWriterAsync(destinationPath, purpose, authenticatedMetadata, cancellationToken);
    }

    public Task<EncryptedBlobDescriptor> WriteAsync(string destinationPath, Stream plaintext, EncryptedBlobPurpose purpose, ReadOnlyMemory<byte> authenticatedMetadata = default, long? plaintextLength = null, CancellationToken cancellationToken = default) => inner.WriteAsync(destinationPath, plaintext, purpose, authenticatedMetadata, plaintextLength, cancellationToken);

    public Task<EncryptedBlobDescriptor> InspectAsync(string path, EncryptedBlobPurpose purpose, bool verifyAllChunks, CancellationToken cancellationToken = default) => inner.InspectAsync(path, purpose, verifyAllChunks, cancellationToken);

    public bool HasEnvelope(string path) => inner.HasEnvelope(path);

    private sealed class Input(Stream inner, BatchAdmissionBlobs owner, BatchAdmissionGate gate) : Stream
    {
        private int _unadmittedBytes;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await inner.ReadAsync(buffer, cancellationToken);

            if (owner.RequireReadFrontier && gate.ActiveGroups == 0)
            {
                _unadmittedBytes += count;

                Assert.InRange(_unadmittedBytes, 0, 64 * 1024);
            }

            return count;
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();

            Interlocked.Decrement(ref owner._activeInputs);

            gate.Events.Enqueue("input-close");

            GC.SuppressFinalize(this);
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
