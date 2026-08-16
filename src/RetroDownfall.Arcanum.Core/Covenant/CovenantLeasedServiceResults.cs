using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// A service result that still owns the lease its content was read under, handed to whoever will
/// serialize it.
/// </summary>
/// <remarks>
/// The transfer exists because releasing a lease on return is the one thing a protected read must
/// not do. A handler that returned a plain <see cref="Result{T}"/> would have no coverage while the
/// response body was being written, and a reset draining in that window would report erasure
/// complete while the same process finished writing the erased bytes down a socket (§10.18).
///
/// <para>Nonserializable and single-take. Two takers would mean two disposals of one registration,
/// and a delayed second release can free a gate slot a later lease already owns. An abandoned
/// transfer is a leaked lease, so <see cref="DisposeAsync"/> releases what was never taken and does
/// nothing once ownership has moved on.</para>
///
/// <para>The failure path carries a lease too. The refusal travels through the same boundary, and a
/// boundary holding coverage only on success would have none exactly while it wrote the body
/// explaining that the content is gone.</para>
/// </remarks>
public sealed class CovenantLeasedServiceResult<T> : IAsyncDisposable
{

    private readonly Result<T> _payload;

    private ICovenantOperationLease? _lease;

    private int _taken;

    private CovenantLeasedServiceResult(Result<T> payload, ICovenantOperationLease lease)
    {

        _payload = payload;

        _lease = lease;

    }

    public static CovenantLeasedServiceResult<T> Create(Result<T> payload, ICovenantOperationLease lease)
    {

        ArgumentNullException.ThrowIfNull(payload);

        ArgumentNullException.ThrowIfNull(lease);

        return new CovenantLeasedServiceResult<T>(payload, lease);

    }

    /// <summary>
    /// Moves the payload and sole lease ownership to the caller. Valid exactly once.
    /// </summary>
    public (Result<T> Payload, ICovenantOperationLease Lease) Take()
    {

        if (Interlocked.Exchange(ref _taken, 1) != 0)
        {

            throw new InvalidOperationException(
                "A leased Covenant service result transfers its lease exactly once.");

        }

        ICovenantOperationLease lease = _lease!;

        _lease = null;

        return (_payload, lease);

    }

    public async ValueTask DisposeAsync()
    {

        if (Interlocked.Exchange(ref _taken, 1) != 0)
        {

            return;

        }

        ICovenantOperationLease? lease = _lease;

        _lease = null;

        if (lease is not null)
        {

            await lease.DisposeAsync().ConfigureAwait(false);

        }

    }

}

/// <summary>
/// The exclusive variant: the result, the closed-scope lease, the one disposition already chosen from
/// evidence, and the durable journal finalizer that may run only after it succeeds.
/// </summary>
/// <remarks>
/// The disposition travels with the lease rather than being decided by the response boundary,
/// because the boundary knows only whether serialization threw. Whether the operation durably
/// mutated anything, and whether that mutation was published, are facts only the coordinator
/// observed — and <see cref="CovenantExclusiveLeaseDisposition.KeepClosed"/> versus
/// <see cref="CovenantExclusiveLeaseDisposition.CommitAndReopen"/> is exactly the difference between
/// preserving a recoverable operation and handing live traffic a database nobody verified (§10.17).
///
/// <para>The finalizer is carried, never invoked here. <c>CovenantExclusiveOperationLease</c> already
/// claims the one-shot disposition and runs the finalizer only on success; a boundary that called
/// them separately would either duplicate the claim or bypass the guard.</para>
/// </remarks>
public sealed class CovenantExclusiveLeasedServiceResult<T> : IAsyncDisposable
{

    private readonly Result<T> _payload;

    private readonly CovenantExclusiveLeaseDisposition _disposition;

    private readonly ICovenantExclusivePostDispositionFinalizer _finalizer;

    private ICovenantExclusiveOperationLease? _lease;

    private int _taken;

    private CovenantExclusiveLeasedServiceResult(
        Result<T> payload,
        ICovenantExclusiveOperationLease lease,
        CovenantExclusiveLeaseDisposition disposition,
        ICovenantExclusivePostDispositionFinalizer finalizer)
    {

        _payload = payload;

        _lease = lease;

        _disposition = disposition;

        _finalizer = finalizer;

    }

    public static CovenantExclusiveLeasedServiceResult<T> Create(
        Result<T> payload,
        ICovenantExclusiveOperationLease lease,
        CovenantExclusiveLeaseDisposition disposition,
        ICovenantExclusivePostDispositionFinalizer finalizer)
    {

        ArgumentNullException.ThrowIfNull(payload);

        ArgumentNullException.ThrowIfNull(lease);

        ArgumentNullException.ThrowIfNull(finalizer);

        if (disposition is not CovenantExclusiveLeaseDisposition.RollbackAndReopen
            and not CovenantExclusiveLeaseDisposition.CommitAndReopen
            and not CovenantExclusiveLeaseDisposition.KeepClosed)
        {

            throw new ArgumentOutOfRangeException(nameof(disposition));

        }

        return new CovenantExclusiveLeasedServiceResult<T>(payload, lease, disposition, finalizer);

    }

    public (Result<T> Payload,
        ICovenantExclusiveOperationLease Lease,
        CovenantExclusiveLeaseDisposition Disposition,
        ICovenantExclusivePostDispositionFinalizer Finalizer) Take()
    {

        if (Interlocked.Exchange(ref _taken, 1) != 0)
        {

            throw new InvalidOperationException(
                "An exclusive leased Covenant service result transfers its lease exactly once.");

        }

        ICovenantExclusiveOperationLease lease = _lease!;

        _lease = null;

        return (_payload, lease, _disposition, _finalizer);

    }

    public async ValueTask DisposeAsync()
    {

        if (Interlocked.Exchange(ref _taken, 1) != 0)
        {

            return;

        }

        ICovenantExclusiveOperationLease? lease = _lease;

        _lease = null;

        if (lease is not null)
        {

            await lease.DisposeAsync().ConfigureAwait(false);

        }

    }

}
