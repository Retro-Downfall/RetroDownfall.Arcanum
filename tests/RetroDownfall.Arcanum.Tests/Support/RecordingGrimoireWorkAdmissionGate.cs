using System.Data.Common;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class RecordingGrimoireWorkAdmissionGate(
    IGrimoireConnectionAdmissionGate inner) : IGrimoireConnectionAdmissionGate
{
    private readonly object _observationsGate = new();

    private readonly List<GrimoireWorkKind> _requestedWorkKinds = [];

    private readonly List<IGrimoireWorkLease> _innerWorkLeases = [];

    private readonly List<IGrimoireWorkLease> _issuedWorkLeases = [];

    private readonly List<IGrimoireWorkLease> _effectGroupLeases = [];

    private readonly List<long> _observedGenerationWaits = [];

    private readonly List<DrainedAttestation> _drainedAttestations = [];

    private int _drainedAttestationOrdinal;

    private int _effectGroupAttempts;

    private int _generationWaits;

    private int _activeGenerationWaiters;

    internal IReadOnlyList<GrimoireWorkKind> RequestedWorkKinds
    {
        get
        {
            lock (_observationsGate)
            {
                return _requestedWorkKinds.ToArray();
            }
        }
    }

    internal IReadOnlyList<IGrimoireWorkLease> InnerWorkLeases
    {
        get
        {
            lock (_observationsGate)
            {
                return _innerWorkLeases.ToArray();
            }
        }
    }

    /// <summary>The wrapper leases this gate handed to its callers, in issue order: the instances callers actually hold.</summary>
    internal IReadOnlyList<IGrimoireWorkLease> IssuedWorkLeases
    {
        get
        {
            lock (_observationsGate)
            {
                return _issuedWorkLeases.ToArray();
            }
        }
    }

    /// <summary>The issued lease instance each external-effect group attempt was made on, in attempt order.</summary>
    internal IReadOnlyList<IGrimoireWorkLease> EffectGroupLeases
    {
        get
        {
            lock (_observationsGate)
            {
                return _effectGroupLeases.ToArray();
            }
        }
    }

    /// <summary>Every drained-maintenance attestation this gate was asked for, in order, with its verdict.</summary>
    internal IReadOnlyList<DrainedAttestation> DrainedAttestations
    {
        get
        {
            lock (_observationsGate)
            {
                return _drainedAttestations.ToArray();
            }
        }
    }

    internal int EffectGroupAttempts => Volatile.Read(ref _effectGroupAttempts);

    internal int GenerationWaits => Volatile.Read(ref _generationWaits);

    internal int ActiveGenerationWaiters => Volatile.Read(ref _activeGenerationWaiters);

    internal IReadOnlyList<long> ObservedGenerationWaits
    {
        get
        {
            lock (_observationsGate)
            {
                return _observedGenerationWaits.ToArray();
            }
        }
    }

    internal Action BeforeEffectGroupAdmission { get; set; } = static () => { };

    /// <summary>
    /// Runs before each drained attestation with that attestation's 1-based position in this gate's sequence.
    /// It may drive a real edge on the inner gate (for example <c>BeginOrResumeExclusive</c>) and may only refuse:
    /// returning <see langword="false"/> ANDs the verdict down without consulting the inner gate, and returning
    /// <see langword="true"/> leaves the verdict to the inner gate. It can never grant.
    /// </summary>
    internal Func<int, bool> BeforeDrainedAttestation { get; set; } = static _ => true;

    internal Action<long> AfterOpenGenerationObserved { get; set; } = static _ => { };

    internal Func<ValueTask> BeforeWorkLeaseDisposalAsync { get; set; } =
        static () => ValueTask.CompletedTask;

    internal Func<ValueTask> BeforeEffectGroupDisposalAsync { get; set; } =
        static () => ValueTask.CompletedTask;

    public long CurrentGeneration => inner.CurrentGeneration;

    /// <summary>
    /// Forwards only a lease this gate itself wrapped, unwrapped to the inner gate's own lease.
    /// A wrapper minted by any other gate, or a lease this gate never saw, is refused.
    /// </summary>
    public bool AttestDrainedMaintenance(IGrimoireWorkLease lease, long previousGeneration, long currentGeneration)
    {
        int ordinal = Interlocked.Increment(ref _drainedAttestationOrdinal);

        // The hook runs first and only ever narrows: a refusal never reaches the inner gate, and a pass leaves the
        // verdict to the inner gate, so no hook can manufacture an attestation the real gate would not give.
        bool attested = BeforeDrainedAttestation(ordinal)
            && lease is RecordingWorkLease recording
            && ReferenceEquals(recording.Owner, this)
            && inner.AttestDrainedMaintenance(recording.Inner, previousGeneration, currentGeneration);

        lock (_observationsGate)
        {
            _drainedAttestations.Add(new(lease?.Kind, previousGeneration, currentGeneration, attested));
        }

        return attested;
    }

    public bool TryAcquireRequestLease(
        GrimoireRequestKind kind,
        out IGrimoireRequestLease? lease) =>
        inner.TryAcquireRequestLease(kind, out lease);

    public bool TryAcquireWorkLease(
        GrimoireWorkKind kind,
        out IGrimoireWorkLease? lease)
    {
        lock (_observationsGate)
        {
            _requestedWorkKinds.Add(kind);
        }

        if (!inner.TryAcquireWorkLease(kind, out IGrimoireWorkLease? admitted))
        {
            lease = null;

            return false;
        }

        RecordingWorkLease issued = new(this, admitted!);

        lock (_observationsGate)
        {
            _innerWorkLeases.Add(admitted!);

            _issuedWorkLeases.Add(issued);
        }

        lease = issued;

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

    public async Task<long> WaitForNextOpenGenerationAsync(
        long observedGeneration,
        CancellationToken cancellationToken)
    {
        lock (_observationsGate)
        {
            _observedGenerationWaits.Add(observedGeneration);
        }

        Interlocked.Increment(ref _generationWaits);

        Interlocked.Increment(ref _activeGenerationWaiters);

        try
        {
            long generation = await inner.WaitForNextOpenGenerationAsync(observedGeneration, cancellationToken);

            AfterOpenGenerationObserved(generation);

            return generation;
        }
        finally
        {
            Interlocked.Decrement(ref _activeGenerationWaiters);
        }
    }

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

    internal readonly record struct DrainedAttestation(GrimoireWorkKind? Kind, long Previous, long Current, bool Attested);

    private sealed class RecordingWorkLease(
        RecordingGrimoireWorkAdmissionGate gate,
        IGrimoireWorkLease inner) : IGrimoireWorkLease
    {
        internal RecordingGrimoireWorkAdmissionGate Owner => gate;

        internal IGrimoireWorkLease Inner => inner;

        public GrimoireWorkKind Kind => inner.Kind;

        public long Generation => inner.Generation;

        public CancellationToken MaintenanceRevocation => inner.MaintenanceRevocation;

        public bool MaintenanceRevocationPending => inner.MaintenanceRevocationPending;

        public bool TryBeginExternalEffectGroup(
            out IGrimoireExternalEffectGroup? effectGroup)
        {
            Interlocked.Increment(ref gate._effectGroupAttempts);

            lock (gate._observationsGate)
            {
                gate._effectGroupLeases.Add(this);
            }

            gate.BeforeEffectGroupAdmission();

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
            try
            {
                await gate.BeforeWorkLeaseDisposalAsync();
            }
            finally
            {
                await inner.DisposeAsync();
            }
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
