using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// The one lease acquisition whose evidence is a held installation maintenance lock.
/// </summary>
/// <remarks>
/// Separate from <c>ILongRunningOperationStore</c> rather than a member of it, because the evidence
/// this takes is an <see cref="ArcanumMaintenanceLock"/> and that type lives in Infrastructure while
/// the ordinary store contract lives in Core. A Core method that took the lock as an opaque object,
/// or took nothing and trusted its caller, would be an expiry-free lease acquisition available to
/// every caller of the ordinary store — which is the one thing this must not become.
///
/// <para>Implemented by the ordinary store over the ordinary statement. Two implementations of "take
/// this row's lease" would agree about which states are reclaimable on the day they were written.</para>
/// </remarks>
internal interface ILongRunningOperationMaintenanceLeaseAdoption
{

    Task<LongRunningOperationLeaseResult> AdoptUnderInstallationLockAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

}
