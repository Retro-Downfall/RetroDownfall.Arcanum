namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Persists inference run lifecycle for accounting and analytics. Late-bound to idempotency claims.
/// </summary>
public interface ITurnRunWriter
{

    Task<Guid> StartRunAsync(InferenceRunStart start, CancellationToken cancellationToken = default);

    Task CompleteRunAsync(Guid runId, InferenceRunStatus status, CancellationToken cancellationToken = default);

    Task<Guid> RecordBillableOperationAsync(BillableOperationRecord operation, CancellationToken cancellationToken = default);

}

public enum InferenceRunStatus
{

    Running = 0,

    Completed = 1,

    Failed = 2,

    Abandoned = 3,

}

public enum BillableOperationType
{

    Chat = 0,

    Embedding = 1,

    Routing = 2,

    Extraction = 3,

    Retry = 4,

}

public enum BillableOperationStatus
{

    Completed = 0,

    Failed = 1,

}

public sealed record InferenceRunStart(
    string RequestId,
    Guid? SessionId,
    string Surface,
    string Purpose,
    Guid? IdempotencyClaimId,
    DateTimeOffset StartedAt);

public sealed record BillableOperationRecord(
    Guid RunId,
    BillableOperationType OperationType,
    string Provider,
    string Model,
    string Purpose,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    long InputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long CachedTokens,
    string PricingSnapshotJson,
    decimal ActualCostUsd,
    BillableOperationStatus Status,
    string? ProviderRequestId);
