using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Holds the one committed <see cref="CovenantAuthoritySnapshot"/> for the process.
/// </summary>
/// <remarks>
/// The whole record is swapped atomically rather than assembled field by field, so a reader can
/// never observe a new authority epoch beside a stale host-tools state. That mixed view is the
/// dangerous one: it would let a gate conclude an installation was clean under an epoch that was
/// published precisely because it is not.
///
/// <para><see cref="Publish"/> is internal and is called only after the core install transaction
/// commits. Publishing earlier would let a rolled-back transaction leave the process asserting an
/// authority generation the database never recorded, and nothing would ever take it back.</para>
/// </remarks>
internal sealed class CovenantAuthoritySnapshotProvider : ICovenantAuthoritySnapshotProvider
{

    private CovenantAuthoritySnapshot? _current;

    public CovenantAuthoritySnapshot? Current => Volatile.Read(ref _current);

    /// <summary>
    /// Publishes the committed snapshot. Call only after the transaction that established it has
    /// committed.
    /// </summary>
    internal void Publish(CovenantAuthoritySnapshot snapshot)
    {

        ArgumentNullException.ThrowIfNull(snapshot);

        _ = Interlocked.Exchange(ref _current, snapshot);

    }

}
