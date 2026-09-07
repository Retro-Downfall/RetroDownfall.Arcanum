using System.Collections.Concurrent;
using System.Data.Common;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

internal sealed class UnseenServantAdmissionObserver(
    UnseenServantAdmissionHarness harness,
    RecordingGrimoireWorkAdmissionGate inner) : IGrimoireConnectionAdmissionGate
{
    private readonly object _gate = new();

    internal Action<IGrimoireWorkLease>? OnAcquired { get; set; }

    internal ConcurrentQueue<long> Waits { get; } = new();

    public long CurrentGeneration => inner.CurrentGeneration;

    public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease? lease)
    {
        lock (_gate)
        {
            if (!inner.TryAcquireWorkLease(kind, out IGrimoireWorkLease? admitted))
            {
                lease = null;

                return false;
            }

            lease = new ObservedLease(this, admitted!);
        }

        OnAcquired?.Invoke(lease);

        return true;
    }

    public async Task<long> WaitForNextOpenGenerationAsync(long observedGeneration, CancellationToken cancellationToken)
    {
        Waits.Enqueue(observedGeneration);

        await harness.StepAsync("wait", cancellationToken);

        long generation = await inner.WaitForNextOpenGenerationAsync(observedGeneration, cancellationToken);

        await harness.StepAsync("reopened", cancellationToken);

        return generation;
    }

    public bool TryAcquireRequestLease(GrimoireRequestKind kind, out IGrimoireRequestLease? lease) => inner.TryAcquireRequestLease(kind, out lease);

    public IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection) => inner.AcquireOrdinaryOpen(connection);

    public Result<IGrimoireClosingOwner> BeginOrResumeExclusive(CovenantExclusiveRecoveryOwner owner, IGrimoireRequestLease? initiatingRequest = null, DbConnection? scopedConnection = null) => inner.BeginOrResumeExclusive(owner, initiatingRequest, scopedConnection);

    public ValueTask<Result> DrainRequestAndWorkAsync(IGrimoireClosingOwner closingOwner, CancellationToken cancellationToken) => inner.DrainRequestAndWorkAsync(closingOwner, cancellationToken);

    public ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(IGrimoireClosingOwner closingOwner, CancellationToken cancellationToken) => inner.CloseConnectionAdmissionAsync(closingOwner, cancellationToken);

    public ValueTask<Result> AbortClosingAsync(IGrimoireClosingOwner closingOwner, Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync, CancellationToken cancellationToken) => inner.AbortClosingAsync(closingOwner, proveNoDestructiveEffectAsync, cancellationToken);

    public ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>> AcquireExpiredLeaseAdoptionInterlockAsync(CovenantExclusiveRecoveryOwner candidateOwner, Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>> revalidateDurableOwnerAsync, CancellationToken cancellationToken) => inner.AcquireExpiredLeaseAdoptionInterlockAsync(candidateOwner, revalidateDurableOwnerAsync, cancellationToken);

    private sealed class ObservedLease(UnseenServantAdmissionObserver observer, IGrimoireWorkLease inner) : IGrimoireWorkLease
    {
        public GrimoireWorkKind Kind => inner.Kind;

        public long Generation => inner.Generation;

        public CancellationToken MaintenanceRevocation => inner.MaintenanceRevocation;

        public bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup)
        {
            lock (observer._gate)
            {
                return inner.TryBeginExternalEffectGroup(out effectGroup);
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
