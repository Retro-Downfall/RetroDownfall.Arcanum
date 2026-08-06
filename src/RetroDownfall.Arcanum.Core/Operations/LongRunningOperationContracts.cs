namespace RetroDownfall.Arcanum.Core.Operations;

/// <summary>
/// Durable lifecycle state for work that can outlive the process which started it.
/// </summary>
public enum LongRunningOperationState
{
    Pending = 0,
    Running = 1,
    Waiting = 2,
    Cancelling = 3,
    Completed = 4,
    Failed = 5,
    Abandoned = 6,
    ReconciliationRequired = 7,
}

public enum LongRunningOperationRecoveryPolicy
{
    ResumeFromCheckpoint = 0,
    RestartIdempotently = 1,
    ReconcileAndComplete = 2,
    AbandonSafely = 3,
}

/// <summary>
/// Bounded operation-kind vocabulary. New durable workflows must add their recovery policy to
/// <see cref="LongRunningOperationPolicyCatalog"/>.
/// </summary>
public static class LongRunningOperationKinds
{
    public const string InferenceRun = "inference-run";
    public const string Subagent = "subagent";
    public const string BudgetReservation = "budget-reservation";
    public const string Batch = "batch";
    public const string Apprentice = "apprentice";
    public const string AttachmentPromotion = "attachment-promotion";
    public const string WorkspaceIndex = "workspace-index";
    public const string IdempotencyClaim = "idempotency-claim";
    public const string BlobEncryptionMigration = "blob-encryption-migration";
    public const string BlobEncryptionKeyRotation = "blob-encryption-key-rotation";

    public const string BackupCreate = "backup-create";

    public const string DataRetentionPrune = "data-retention-prune";

    public const string DataRetentionMutation = "data-retention-mutation";

    public const string DataRetentionFactoryReset = "data-retention-factory-reset";

    /// <summary>An inbound A2A Sending: a peer's task id bound to the Apprentice serving it (#62).</summary>
    public const string A2AInboundSending = "a2a-inbound-sending";

    /// <summary>An outbound A2A Sending: a remote task id this instance is waiting on (#62).</summary>
    public const string A2AOutboundSending = "a2a-outbound-sending";
}

public static class LongRunningOperationErrorCodes
{
    public const string UnsupportedCheckpointVersion = "operation.checkpoint_version_unsupported";
    public const string CorruptCheckpoint = "operation.checkpoint_corrupt";
    public const string RecoveryHandlerMissing = "operation.recovery_handler_missing";
    public const string RecoveryFailed = "operation.recovery_failed";
    public const string InvalidRecoveryResult = "operation.recovery_result_invalid";

    /// <summary>
    /// The ledger row is missing the domain id its handler needs (inference run, claim, reservation).
    /// Recovery cannot guess which entity the crashed work owned, so an operator has to look (#40).
    /// </summary>
    public const string MissingOperationLink = "operation.link_missing";
}

/// <summary>
/// Named terminal codes recorded by the shared recovery handlers, so <c>arcanum operation list</c>
/// says what actually happened instead of a bare "abandoned" (#40).
/// </summary>
public static class LongRunningOperationRecoveryOutcomes
{
    /// <summary>A crashed subagent child: never restarted, never re-billed, reservation released.</summary>
    public const string SubagentChildAbandoned = "subagent.child_abandoned";

    /// <summary>The ledger row outlived the Apprentice it was tracking.</summary>
    public const string ApprenticeMissing = "apprentice.missing";

    /// <summary>
    /// The interrupted response was not fully captured, so the claim can never be replayed as a
    /// cached result and the caller must re-send.
    /// </summary>
    public const string ClaimNotReplayable = "idempotency.claim_not_replayable";
}

/// <summary>
/// Policy column of <see cref="LongRunningOperationRecoveryRegistry"/>, kept as a separate surface
/// because callers that only need "which class does this kind recover as" should not depend on the
/// whole descriptor. It is projected rather than duplicated, so the two cannot drift (#40).
/// </summary>
public static class LongRunningOperationPolicyCatalog
{
    private static readonly IReadOnlyDictionary<string, LongRunningOperationRecoveryPolicy> Policies =
        LongRunningOperationRecoveryRegistry.Descriptors.ToDictionary(
            static entry => entry.Key,
            static entry => entry.Value.Policy,
            StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, LongRunningOperationRecoveryPolicy> Registered => Policies;

    public static bool IsRegistered(string kind, LongRunningOperationRecoveryPolicy policy) =>
        Policies.TryGetValue(kind, out LongRunningOperationRecoveryPolicy registered)
        && registered == policy;
}

public sealed record LongRunningOperation(
    Guid Id,
    string Kind,
    LongRunningOperationState State,
    LongRunningOperationRecoveryPolicy RecoveryPolicy,
    Guid? RootOperationId,
    Guid? ParentOperationId,
    Guid? SessionId,
    Guid? RunId,
    Guid? InferenceRunId,
    Guid? BudgetReservationId,
    Guid? IdempotencyClaimId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset? CompletedAt,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAt,
    int AttemptCount,
    int CheckpointVersion,
    byte[]? CheckpointPayload,
    string? CheckpointReference,
    string PublicSummary,
    string? TerminalErrorCode,
    long Revision);

public sealed record LongRunningOperationCreateRequest(
    string Kind,
    LongRunningOperationRecoveryPolicy RecoveryPolicy,
    string PublicSummary,
    DateTimeOffset CreatedAt,
    Guid? RootOperationId = null,
    Guid? ParentOperationId = null,
    Guid? SessionId = null,
    Guid? RunId = null,
    Guid? InferenceRunId = null,
    Guid? BudgetReservationId = null,
    Guid? IdempotencyClaimId = null);

public sealed record LongRunningOperationQuery(
    string? Kind = null,
    LongRunningOperationState? State = null,
    int Limit = 100,
    int Offset = 0);

public sealed record LongRunningOperationLeaseResult(
    bool Acquired,
    LongRunningOperation Operation);

public sealed record LongRunningOperationCount(
    string Kind,
    LongRunningOperationState State,
    long Count);

/// <summary>
/// Authenticated operator view. Encrypted checkpoint payloads/references are deliberately absent.
/// </summary>
public sealed record LongRunningOperationDto(
    Guid Id,
    string Kind,
    LongRunningOperationState State,
    LongRunningOperationRecoveryPolicy RecoveryPolicy,
    Guid? RootOperationId,
    Guid? ParentOperationId,
    Guid? SessionId,
    Guid? RunId,
    Guid? InferenceRunId,
    Guid? BudgetReservationId,
    Guid? IdempotencyClaimId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset? CompletedAt,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAt,
    int AttemptCount,
    int CheckpointVersion,
    bool HasCheckpoint,
    string PublicSummary,
    string? TerminalErrorCode,
    long Revision)
{
    public static LongRunningOperationDto FromOperation(LongRunningOperation operation) =>
        new(
            operation.Id,
            operation.Kind,
            operation.State,
            operation.RecoveryPolicy,
            operation.RootOperationId,
            operation.ParentOperationId,
            operation.SessionId,
            operation.RunId,
            operation.InferenceRunId,
            operation.BudgetReservationId,
            operation.IdempotencyClaimId,
            operation.CreatedAt,
            operation.StartedAt,
            operation.HeartbeatAt,
            operation.CompletedAt,
            operation.LeaseOwner,
            operation.LeaseExpiresAt,
            operation.AttemptCount,
            operation.CheckpointVersion,
            operation.CheckpointPayload is not null || operation.CheckpointReference is not null,
            operation.PublicSummary,
            operation.TerminalErrorCode,
            operation.Revision);
}

public sealed record LongRunningOperationReconciliationSummary(
    int Examined,
    int Claimed,
    int Completed,
    int Failed,
    int Abandoned,
    int RequiresAttention,
    int Skipped);

public sealed record LongRunningOperationRecoveryResult(
    LongRunningOperationState State,
    string? ErrorCode = null)
{
    public static LongRunningOperationRecoveryResult Completed() =>
        new(LongRunningOperationState.Completed);

    public static LongRunningOperationRecoveryResult Failed(string errorCode) =>
        new(LongRunningOperationState.Failed, errorCode);

    public static LongRunningOperationRecoveryResult Abandoned(string? errorCode = null) =>
        new(LongRunningOperationState.Abandoned, errorCode);

    public static LongRunningOperationRecoveryResult RequiresAttention(string errorCode) =>
        new(LongRunningOperationState.ReconciliationRequired, errorCode);
}

public interface ILongRunningOperationStore
{
    Task<LongRunningOperation> CreateAsync(
        LongRunningOperationCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<LongRunningOperation?> TryStartSingleFlightAsync(
        LongRunningOperationCreateRequest request,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task<LongRunningOperation?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LongRunningOperation>> ListAsync(
        LongRunningOperationQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(
        DateTimeOffset utcNow,
        int limit,
        CancellationToken cancellationToken = default);

    Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task<bool> HeartbeatAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews a lease from an independent persistence path while the owned workload may still be
    /// using its scoped database connection. Implementations that do not need that separation can
    /// use the ordinary heartbeat path.
    /// </summary>
    Task<bool> RenewLeaseAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        HeartbeatAsync(
            operationId,
            ownerId,
            utcNow,
            leaseExpiresAt,
            cancellationToken);

    Task<bool> SaveCheckpointAsync(
        Guid operationId,
        string ownerId,
        int expectedCheckpointVersion,
        int checkpointVersion,
        byte[]? checkpointPayload,
        string? checkpointReference,
        string publicSummary,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task<bool> TryTransitionAsync(
        Guid operationId,
        long expectedRevision,
        string? ownerId,
        LongRunningOperationState state,
        DateTimeOffset utcNow,
        string? terminalErrorCode = null,
        CancellationToken cancellationToken = default);

    Task<bool> RequestCancellationAsync(
        Guid operationId,
        long expectedRevision,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task<bool> ResetForRetryAsync(
        Guid operationId,
        long expectedRevision,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(
        CancellationToken cancellationToken = default);
}

public interface ILongRunningOperationCoordinator
{
    Task<LongRunningOperationLeaseResult> StartAsync(
        LongRunningOperationCreateRequest request,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> HeartbeatAsync(
        Guid operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> CheckpointAsync(
        Guid operationId,
        string ownerId,
        int expectedCheckpointVersion,
        int checkpointVersion,
        byte[]? checkpointPayload,
        string? checkpointReference,
        string publicSummary,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(
        Guid operationId,
        string ownerId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<bool> FailAsync(
        Guid operationId,
        string ownerId,
        long expectedRevision,
        string errorCode,
        CancellationToken cancellationToken = default);
}

public interface ILongRunningOperationRecoveryHandler
{
    string Kind { get; }

    int SupportedCheckpointVersion { get; }

    Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken);
}
