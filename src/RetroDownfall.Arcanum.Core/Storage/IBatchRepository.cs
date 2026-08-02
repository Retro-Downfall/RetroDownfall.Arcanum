namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Grimoire-backed metadata for <c>/v1/batches</c> asynchronous bulk chat-completion jobs
/// (<c>docs/Arcanum.DESIGN.md</c> §11.21). <see cref="BatchStatuses"/> enumerates the lifecycle values stored in
/// <see cref="BatchRecord.Status"/>.
/// </summary>
public interface IBatchRepository
{

    /// <summary>
    /// Creates a batch only when every input/output/error file reference resolves to current
    /// uploaded-file metadata in the same database write. A missing reference throws
    /// <see cref="BatchFileReferenceException"/> and no batch row is created.
    /// </summary>
    Task CreateAsync(BatchRecord record, CancellationToken cancellationToken = default);

    Task<BatchRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BatchRecord>> ListAsync(string? status, CancellationToken cancellationToken = default);

    /// <summary>Every batch not yet in a terminal state (<see cref="BatchStatuses.Validating"/> or <see cref="BatchStatuses.InProgress"/>) — used by the background processor's pickup and expiry sweeps.</summary>
    Task<IReadOnlyList<BatchRecord>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Batches whose <see cref="BatchRecord.Status"/> exactly matches <paramref name="status"/> (e.g. stranded <see cref="BatchStatuses.InProgress"/> recovery).</summary>
    Task<IReadOnlyList<BatchRecord>> ListByStatusAsync(string status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates status and conditionally attaches output/error file ids only when their uploaded-file
    /// metadata exists in the same database write. A missing artifact reference throws
    /// <see cref="BatchFileReferenceException"/> without changing the batch.
    /// </summary>
    Task UpdateStatusAsync(
        Guid id,
        string status,
        DateTimeOffset? completedAt,
        Guid? outputFileId,
        Guid? errorFileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically sets status (and optional completion/file fields) only when the row is still
    /// <paramref name="expectedStatus"/> and every supplied output/error id resolves to current
    /// uploaded-file metadata. Returns <see langword="true"/> when exactly one row was updated.
    /// </summary>
    Task<bool> TryCompareAndSetStatusAsync(
        Guid id,
        string expectedStatus,
        string newStatus,
        DateTimeOffset? completedAt,
        Guid? outputFileId,
        Guid? errorFileId,
        CancellationToken cancellationToken = default);

}

public sealed class BatchFileReferenceException(Guid batchId)
    : InvalidOperationException(
        $"Batch '{batchId:D}' references uploaded file metadata that does not exist.")
{

    public Guid BatchId { get; } = batchId;

}

public sealed record BatchRecord(
    Guid Id,
    Guid InputFileId,
    string Endpoint,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    Guid? OutputFileId,
    Guid? ErrorFileId);

/// <summary>Lifecycle values for <see cref="BatchRecord.Status"/>: <c>validating → in_progress → completed/failed/cancelled/expired</c>.</summary>
public static class BatchStatuses
{

    public const string Validating = "validating";

    public const string InProgress = "in_progress";

    public const string Completed = "completed";

    public const string Failed = "failed";

    public const string Cancelled = "cancelled";

    public const string Expired = "expired";

    public static bool IsTerminal(string status) =>
        status is Completed or Failed or Cancelled or Expired;

    public static bool IsStuck(string status) =>
        status == InProgress;

}
