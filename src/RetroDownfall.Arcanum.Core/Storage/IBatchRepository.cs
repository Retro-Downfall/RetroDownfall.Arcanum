namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Grimoire-backed metadata for <c>/v1/batches</c> asynchronous bulk chat-completion jobs
/// (DESIGN.md §11.21). <see cref="BatchStatuses"/> enumerates the lifecycle values stored in
/// <see cref="BatchRecord.Status"/>.
/// </summary>
public interface IBatchRepository
{

    Task CreateAsync(BatchRecord record, CancellationToken cancellationToken = default);

    Task<BatchRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BatchRecord>> ListAsync(string? status, CancellationToken cancellationToken = default);

    /// <summary>Every batch not yet in a terminal state (<see cref="BatchStatuses.Validating"/> or <see cref="BatchStatuses.InProgress"/>) — used by the background processor's pickup and expiry sweeps.</summary>
    Task<IReadOnlyList<BatchRecord>> ListActiveAsync(CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        Guid id,
        string status,
        DateTimeOffset? completedAt,
        Guid? outputFileId,
        Guid? errorFileId,
        CancellationToken cancellationToken = default);

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
