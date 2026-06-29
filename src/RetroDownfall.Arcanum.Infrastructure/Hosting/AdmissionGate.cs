namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Atomic increment-then-compare soft-cap gate with an idempotent release lease.
/// </summary>
public sealed class AdmissionGate
{

    private int _count;

    public int CurrentCount => Volatile.Read(ref _count);

    public bool TryEnter(int max, out IDisposable? lease)
    {

        int active = Interlocked.Increment(ref _count);

        if (active > max)
        {

            Interlocked.Decrement(ref _count);

            lease = null;

            return false;

        }

        lease = new Lease(this);

        return true;

    }

    internal void Release()
    {

        Interlocked.Decrement(ref _count);

    }

    private sealed class Lease : IDisposable
    {

        private readonly AdmissionGate _gate;

        private int _disposed;

        internal Lease(AdmissionGate gate)
        {

            _gate = gate;

        }

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {

                _gate.Release();

            }

        }

    }

}
