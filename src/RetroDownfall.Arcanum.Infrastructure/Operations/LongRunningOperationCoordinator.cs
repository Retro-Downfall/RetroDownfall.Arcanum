using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// Scoped facade for operation authors. Heartbeats are explicit and bounded by the caller-provided
/// duration; callers must stop work immediately when renewal returns false.
/// </summary>
internal sealed class LongRunningOperationCoordinator(
    ILongRunningOperationStore store,
    TimeProvider timeProvider) : ILongRunningOperationCoordinator
{
    public async Task<LongRunningOperationLeaseResult> StartAsync(
        LongRunningOperationCreateRequest request,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseDuration(leaseDuration);
        LongRunningOperation operation = await store.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        return await store.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.Add(leaseDuration),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> HeartbeatAsync(
        Guid operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseDuration(leaseDuration);
        DateTimeOffset now = timeProvider.GetUtcNow();
        return store.HeartbeatAsync(
            operationId,
            ownerId,
            now,
            now.Add(leaseDuration),
            cancellationToken);
    }

    public Task<bool> CheckpointAsync(
        Guid operationId,
        string ownerId,
        int expectedCheckpointVersion,
        int checkpointVersion,
        byte[]? checkpointPayload,
        string? checkpointReference,
        string publicSummary,
        CancellationToken cancellationToken = default) =>
        store.SaveCheckpointAsync(
            operationId,
            ownerId,
            expectedCheckpointVersion,
            checkpointVersion,
            checkpointPayload,
            checkpointReference,
            publicSummary,
            timeProvider.GetUtcNow(),
            cancellationToken);

    public Task<bool> CompleteAsync(
        Guid operationId,
        string ownerId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        store.TryTransitionAsync(
            operationId,
            expectedRevision,
            ownerId,
            LongRunningOperationState.Completed,
            timeProvider.GetUtcNow(),
            cancellationToken: cancellationToken);

    public Task<bool> FailAsync(
        Guid operationId,
        string ownerId,
        long expectedRevision,
        string errorCode,
        CancellationToken cancellationToken = default) =>
        store.TryTransitionAsync(
            operationId,
            expectedRevision,
            ownerId,
            LongRunningOperationState.Failed,
            timeProvider.GetUtcNow(),
            errorCode,
            cancellationToken);

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration < TimeSpan.FromSeconds(5) || leaseDuration > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                "Operation leases must be between 5 seconds and 15 minutes.");
        }
    }
}
