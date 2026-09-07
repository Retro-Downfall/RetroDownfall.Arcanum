namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// Refreshes the exact same durable owner's active lease after this process resumes a maintenance-
/// deferred operation it has continuously claimed in memory.
/// </summary>
internal interface ILongRunningOperationSameOwnerLeaseResumption
{
    Task<bool> ResumeSameOwnerLeaseAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);
}
