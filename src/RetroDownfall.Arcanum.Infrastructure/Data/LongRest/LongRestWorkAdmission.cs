namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>Shares one admitted lifetime with the worker and its individual physical sends.</summary>
internal sealed class LongRestWorkAdmission(IGrimoireConnectionAdmissionGate gate)
{
    private IGrimoireWorkLease? _lease;

    internal IGrimoireWorkLease Lease => _lease ?? throw new InvalidOperationException("Long Rest work is not admitted.");

    internal IDisposable Bind(IGrimoireWorkLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (_lease is not null) throw new InvalidOperationException("Long Rest admission is already bound.");

        _lease = lease;

        return new Binding(this, lease);
    }

    internal bool HasLease => _lease is not null;

    // Synchronous acquisition preserves the gate's ambient admitted lifetime in the caller.
    // The caller owns this lease and must retain its binding through the admitted work.
    internal IGrimoireWorkLease Acquire(CancellationToken ct)
    {
        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? lease))
        {
            ct.ThrowIfCancellationRequested();

            // A known work kind is refused only after maintenance has closed admission.
            throw new LongRestMaintenanceDeferredException(CancellationToken.None);
        }

        if (lease is null) throw new InvalidOperationException("Long Rest admission returned no lease.");

        return lease;
    }

    private sealed class Binding(LongRestWorkAdmission owner, IGrimoireWorkLease lease) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(owner._lease, lease)) owner._lease = null;
        }
    }
}

internal sealed class LongRestMaintenanceDeferredException(CancellationToken token) : OperationCanceledException(token)
{
}
