using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed class DeferredBackupOperationCoordinator(
    IServiceProvider serviceProvider) : ILongRunningOperationCoordinator
{

    public Task<LongRunningOperationLeaseResult> StartAsync(
        LongRunningOperationCreateRequest request,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        Service.StartAsync(
            request,
            ownerId,
            leaseDuration,
            cancellationToken);

    public Task<bool> HeartbeatAsync(
        Guid operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        Service.HeartbeatAsync(
            operationId,
            ownerId,
            leaseDuration,
            cancellationToken);

    public Task<bool> CheckpointAsync(
        Guid operationId,
        string ownerId,
        int expectedCheckpointVersion,
        int checkpointVersion,
        byte[]? checkpointPayload,
        string? checkpointReference,
        string publicSummary,
        CancellationToken cancellationToken = default) =>
        Service.CheckpointAsync(
            operationId,
            ownerId,
            expectedCheckpointVersion,
            checkpointVersion,
            checkpointPayload,
            checkpointReference,
            publicSummary,
            cancellationToken);

    public Task<bool> CompleteAsync(
        Guid operationId,
        string ownerId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        Service.CompleteAsync(
            operationId,
            ownerId,
            expectedRevision,
            cancellationToken);

    public Task<bool> FailAsync(
        Guid operationId,
        string ownerId,
        long expectedRevision,
        string errorCode,
        CancellationToken cancellationToken = default) =>
        Service.FailAsync(
            operationId,
            ownerId,
            expectedRevision,
            errorCode,
            cancellationToken);

    private ILongRunningOperationCoordinator Service =>
        serviceProvider.GetRequiredService<ILongRunningOperationCoordinator>();

}

internal sealed class DeferredBackupOperationStore(
    IServiceProvider serviceProvider) : ILongRunningOperationStore
{

    public Task<LongRunningOperation> CreateAsync(
        LongRunningOperationCreateRequest request,
        CancellationToken cancellationToken = default) =>
        Service.CreateAsync(request, cancellationToken);

    public Task<LongRunningOperation?> TryStartSingleFlightAsync(
        LongRunningOperationCreateRequest request,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        Service.TryStartSingleFlightAsync(
            request,
            ownerId,
            utcNow,
            leaseExpiresAt,
            cancellationToken);

    public Task<LongRunningOperation?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        Service.GetAsync(operationId, cancellationToken);

    public Task<IReadOnlyList<LongRunningOperation>> ListAsync(
        LongRunningOperationQuery query,
        CancellationToken cancellationToken = default) =>
        Service.ListAsync(query, cancellationToken);

    public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(
        DateTimeOffset utcNow,
        int limit,
        CancellationToken cancellationToken = default) =>
        Service.FindExpiredAsync(utcNow, limit, cancellationToken);

    public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        Service.TryAcquireLeaseAsync(
            operationId,
            ownerId,
            utcNow,
            leaseExpiresAt,
            cancellationToken);

    public Task<bool> HeartbeatAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        Service.HeartbeatAsync(
            operationId,
            ownerId,
            utcNow,
            leaseExpiresAt,
            cancellationToken);

    public Task<bool> RenewLeaseAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        Service.RenewLeaseAsync(
            operationId,
            ownerId,
            utcNow,
            leaseExpiresAt,
            cancellationToken);

    public Task<bool> SaveCheckpointAsync(
        Guid operationId,
        string ownerId,
        int expectedCheckpointVersion,
        int checkpointVersion,
        byte[]? checkpointPayload,
        string? checkpointReference,
        string publicSummary,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        Service.SaveCheckpointAsync(
            operationId,
            ownerId,
            expectedCheckpointVersion,
            checkpointVersion,
            checkpointPayload,
            checkpointReference,
            publicSummary,
            utcNow,
            cancellationToken);

    public Task<bool> TryTransitionAsync(
        Guid operationId,
        long expectedRevision,
        string? ownerId,
        LongRunningOperationState state,
        DateTimeOffset utcNow,
        string? terminalErrorCode = null,
        CancellationToken cancellationToken = default) =>
        Service.TryTransitionAsync(
            operationId,
            expectedRevision,
            ownerId,
            state,
            utcNow,
            terminalErrorCode,
            cancellationToken);

    public Task<bool> RequestCancellationAsync(
        Guid operationId,
        long expectedRevision,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        Service.RequestCancellationAsync(
            operationId,
            expectedRevision,
            utcNow,
            cancellationToken);

    public Task<bool> ResetForRetryAsync(
        Guid operationId,
        long expectedRevision,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        Service.ResetForRetryAsync(
            operationId,
            expectedRevision,
            utcNow,
            cancellationToken);

    public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(
        CancellationToken cancellationToken = default) =>
        Service.GetCountsAsync(cancellationToken);

    private ILongRunningOperationStore Service =>
        serviceProvider.GetRequiredService<ILongRunningOperationStore>();

}
