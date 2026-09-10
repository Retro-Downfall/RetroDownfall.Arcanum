using System.Collections.Concurrent;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Prevents two live inference turns from appending to the same Session in one host process.
/// </summary>
/// <remarks>
/// A turn's user/assistant pair and later tool pairs are separate Grimoire transactions. Allowing a
/// second turn between them would interleave entry sequences, make the second turn plan against an
/// unfinished answer, and leave no contiguous Saga extraction frontier with the right provenance.
/// This gate closes that process-local window at the API orchestration seam that owns the whole turn
/// lifetime; it does not claim to coordinate independent host processes.
/// </remarks>
public sealed class SessionTurnConcurrencyGate
{

    private readonly ConcurrentDictionary<Guid, Lease> _leases = new();

    public bool TryAcquire(Guid sessionId, out IDisposable? lease)
    {

        if (sessionId == Guid.Empty)
        {

            lease = null;

            return false;

        }

        Lease candidate = new(this, sessionId);

        if (_leases.TryAdd(sessionId, candidate))
        {

            lease = candidate;

            return true;

        }

        lease = null;

        return false;

    }

    private void Release(Lease lease)
    {

        _ = ((ICollection<KeyValuePair<Guid, Lease>>)_leases)
            .Remove(new KeyValuePair<Guid, Lease>(lease.SessionId, lease));

    }

    private sealed class Lease(
        SessionTurnConcurrencyGate owner,
        Guid sessionId) : IDisposable
    {

        private SessionTurnConcurrencyGate? _owner = owner;

        public Guid SessionId { get; } = sessionId;

        public void Dispose()
        {

            SessionTurnConcurrencyGate? releasing = Interlocked.Exchange(ref _owner, null);

            releasing?.Release(this);

        }

    }

}
