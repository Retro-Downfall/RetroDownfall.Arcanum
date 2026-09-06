using System.Data.Common;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class RecordingGrimoireWorkAdmissionGate(
    IGrimoireConnectionAdmissionGate inner) : IGrimoireConnectionAdmissionGate
{

    internal List<GrimoireWorkKind> RequestedWorkKinds { get; } = [];

    internal List<IGrimoireWorkLease> InnerWorkLeases { get; } = [];

    internal int EffectGroupAttempts { get; private set; }

    internal Func<ValueTask> BeforeWorkLeaseDisposalAsync { get; set; } =
        static () => ValueTask.CompletedTask;

    internal Func<ValueTask> BeforeEffectGroupDisposalAsync { get; set; } =
        static () => ValueTask.CompletedTask;

    public long CurrentGeneration => inner.CurrentGeneration;

    public bool TryAcquireRequestLease(
        GrimoireRequestKind kind,
        out IGrimoireRequestLease? lease) =>
        inner.TryAcquireRequestLease(kind, out lease);

    public bool TryAcquireWorkLease(
        GrimoireWorkKind kind,
        out IGrimoireWorkLease? lease)
    {

        RequestedWorkKinds.Add(kind);

        if (!inner.TryAcquireWorkLease(kind, out IGrimoireWorkLease? admitted))
        {

            lease = null;

            return false;

        }

        InnerWorkLeases.Add(admitted!);

        lease = new RecordingWorkLease(this, admitted!);

        return true;

    }

    public IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection) =>
        inner.AcquireOrdinaryOpen(connection);

    public Result<IGrimoireClosingOwner> BeginOrResumeExclusive(
        CovenantExclusiveRecoveryOwner owner,
        IGrimoireRequestLease? initiatingRequest = null,
        DbConnection? scopedConnection = null) =>
        inner.BeginOrResumeExclusive(owner, initiatingRequest, scopedConnection);

    public ValueTask<Result> DrainRequestAndWorkAsync(
        IGrimoireClosingOwner closingOwner,
        CancellationToken cancellationToken) =>
        inner.DrainRequestAndWorkAsync(closingOwner, cancellationToken);

    public ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(
        IGrimoireClosingOwner closingOwner,
        CancellationToken cancellationToken) =>
        inner.CloseConnectionAdmissionAsync(closingOwner, cancellationToken);

    public ValueTask<Result> AbortClosingAsync(
        IGrimoireClosingOwner closingOwner,
        Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync,
        CancellationToken cancellationToken) =>
        inner.AbortClosingAsync(
            closingOwner,
            proveNoDestructiveEffectAsync,
            cancellationToken);

    public Task<long> WaitForNextOpenGenerationAsync(
        long observedGeneration,
        CancellationToken cancellationToken) =>
        inner.WaitForNextOpenGenerationAsync(observedGeneration, cancellationToken);

    public ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>>
        AcquireExpiredLeaseAdoptionInterlockAsync(
            CovenantExclusiveRecoveryOwner candidateOwner,
            Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>>
                revalidateDurableOwnerAsync,
            CancellationToken cancellationToken) =>
        inner.AcquireExpiredLeaseAdoptionInterlockAsync(
            candidateOwner,
            revalidateDurableOwnerAsync,
            cancellationToken);

    private sealed class RecordingWorkLease(
        RecordingGrimoireWorkAdmissionGate gate,
        IGrimoireWorkLease inner) : IGrimoireWorkLease
    {

        public GrimoireWorkKind Kind => inner.Kind;

        public long Generation => inner.Generation;

        public CancellationToken MaintenanceRevocation => inner.MaintenanceRevocation;

        public bool TryBeginExternalEffectGroup(
            out IGrimoireExternalEffectGroup? effectGroup)
        {

            gate.EffectGroupAttempts++;

            if (!inner.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? admitted))
            {

                effectGroup = null;

                return false;

            }

            effectGroup = new RecordingExternalEffectGroup(gate, admitted!);

            return true;

        }

        public async ValueTask DisposeAsync()
        {

            await gate.BeforeWorkLeaseDisposalAsync();

            await inner.DisposeAsync();

        }

    }

    private sealed class RecordingExternalEffectGroup(
        RecordingGrimoireWorkAdmissionGate gate,
        IGrimoireExternalEffectGroup inner) : IGrimoireExternalEffectGroup
    {

        public async ValueTask DisposeAsync()
        {

            try
            {

                await gate.BeforeEffectGroupDisposalAsync();

            }
            finally
            {

                await inner.DisposeAsync();

            }

        }

    }

}
