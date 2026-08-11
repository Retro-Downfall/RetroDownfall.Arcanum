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

    /// <summary>
    /// Reads one stable newest-first keyset page. <paramref name="pageSize"/> bounds only the
    /// response allocation; callers continue from the last returned position while work remains.
    /// </summary>
    Task<BatchListPage> ListPageAsync(

        string? status,

        BatchListPosition? after,

        int pageSize,

        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads at most <paramref name="pageSize"/> oldest validating batches for the worker's
    /// currently available concurrency slots. Repeated calls after status claims eventually visit
    /// every queued batch without allocating the complete catalog.
    /// </summary>
    Task<IReadOnlyList<BatchRecord>> ListPendingPageAsync(

        int pageSize,

        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Reads the durable per-line checkpoints in an inclusive input-line range. The processor uses
    /// bounded ranges while walking the input, so this is a page boundary rather than a total-work cap.
    /// </summary>
    Task<IReadOnlyList<BatchLineCheckpoint>> ListLineCheckpointsAsync(

        Guid batchId,

        long firstLine,

        long lastLine,

        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one ordered continuation page of checkpoints in <paramref name="state"/> after
    /// <paramref name="afterLine"/>. Callers continue until an empty page is returned.
    /// </summary>
    Task<IReadOnlyList<BatchLineCheckpoint>> ListLineCheckpointsAsync(

        Guid batchId,

        BatchLineCheckpointState state,

        long afterLine,

        int pageSize,

        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically records that a provider request is about to be dispatched. Returns false when the
    /// line already has any durable checkpoint or the batch is no longer in progress.
    /// </summary>
    Task<bool> TryBeginLineAsync(

        Guid batchId,

        long lineNumber,

        string customId,

        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a line that never reached a provider — a JSON-parse failure or a budget refusal —
    /// as a single durable terminal transition, so it is never observable in the
    /// provider-ambiguous <c>Dispatched</c> state. Returns false when the line already has any
    /// durable checkpoint or the batch is no longer in progress.
    /// </summary>
    Task<bool> TryRecordTerminalLineAsync(

        Guid batchId,

        long lineNumber,

        string customId,

        BatchLineOutputKind outputKind,

        BatchRequestOutcome outcome,

        string jsonLine,

        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces a dispatched checkpoint with its terminal JSONL output. Repeating the
    /// exact completion is idempotent; conflicting terminal content is rejected.
    /// </summary>
    Task CompleteLineAsync(

        Guid batchId,

        long lineNumber,

        BatchLineOutputKind outputKind,

        BatchRequestOutcome outcome,

        string jsonLine,

        CancellationToken cancellationToken = default);

    /// <summary>Deletes checkpoints only after their terminal batch artifacts are durably linked.</summary>
    Task DeleteLineCheckpointsAsync(

        Guid batchId,

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
    Guid? ErrorFileId,
    long TotalRequestCount = 0,
    long CompletedRequestCount = 0,
    long FailedRequestCount = 0);

public sealed record BatchListPosition(

    DateTimeOffset CreatedAt,

    Guid Id);

public sealed record BatchListPage(

    IReadOnlyList<BatchRecord> Records,

    bool HasMore);

public enum BatchLineCheckpointState

{

    Dispatched = 0,

    Completed = 1,

}

public enum BatchLineOutputKind

{

    Output = 0,

    Error = 1,

}

public enum BatchRequestOutcome

{

    Completed = 0,

    Failed = 1,

}

public sealed record BatchLineCheckpoint(

    Guid BatchId,

    long LineNumber,

    string CustomId,

    BatchLineCheckpointState State,

    BatchLineOutputKind? OutputKind,

    BatchRequestOutcome? Outcome,

    string? JsonLine,

    DateTimeOffset DispatchedAt,

    DateTimeOffset? CompletedAt);

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
