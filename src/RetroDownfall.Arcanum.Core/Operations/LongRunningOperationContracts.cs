using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

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

    /// <summary>
    /// Discarding and rebuilding the derived Covenant inspection index (#87).
    /// </summary>
    /// <remarks>
    /// Server-generated identity only. A rebuild replaces a derived accelerator and never a canonical
    /// row, so there is no authenticated preflight to name it after and no effect for a caller to
    /// bind an apply request to.
    /// </remarks>
    public const string CovenantIndexRebuild = "covenant-index-rebuild";

    /// <summary>
    /// Dropping and reinstalling the whole Covenant schema family after protected erasure (#87).
    /// </summary>
    /// <remarks>
    /// The one Covenant kind that carries a caller-supplied durable identity, because it replaces a
    /// database: an operation whose only replay key is the HTTP response announcing it has no replay
    /// key once that process is gone.
    /// </remarks>
    public const string CovenantFamilyReinitialize = "covenant-family-reinitialize";

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

    /// <summary>
    /// An inbound A2A Sending parked at <c>input-required</c>: the peer relay died with its process, but
    /// the escalated Apprentice is still there and the peer's answer can still resume it (#68).
    /// </summary>
    /// <remarks>
    /// Deliberately non-terminal. The row stays re-leasable — see
    /// <c>ILongRunningOperationStore.TryAcquireLeaseAsync</c> — because closing it would destroy the only
    /// record that lets a continuation find its Apprentice, which is exactly the failure #68 removes.
    /// </remarks>
    public const string A2AInboundParkedAwaitingAnswer = "a2a.inbound_parked_awaiting_answer";

    /// <summary>
    /// A data-retention mutation died between its single-flight insert and the durable journal that
    /// authorizes any storage change, so nothing was captured, quarantined, or deleted. There is no
    /// repair to perform and the row must close rather than park — a parked retention row blocks
    /// every later retention operation.
    /// </summary>
    public const string RetentionMutationNeverStarted = "data-retention.mutation_never_started";
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

/// <summary>
/// The caller-supplied durable identity of one prepare/apply operation.
/// </summary>
/// <param name="RequestedOperationId">
/// The name the caller asked the operation to be created under. Unique across the ledger, so a
/// retried apply resolves to the operation it already started instead of starting a second one.
/// </param>
/// <param name="ApplyRequestDigest">
/// The stable digest of the apply request. Independent of token bytes, boot salt, timestamps, and key
/// version, which is what lets replay survive a restart, a secret rotation, and an expired token.
/// </param>
/// <param name="EffectDigest">The canonical effect the plan described, kept as durable evidence.</param>
/// <remarks>
/// Optional on <see cref="LongRunningOperationCreateRequest"/> and required by the operations that
/// replace a database. An operation whose only replay key is the HTTP response that announced it has
/// no replay key at all once the process that wrote that response is gone (§10.16).
/// </remarks>
public sealed record LongRunningOperationRequestIdentity(
    Guid RequestedOperationId,
    CovenantDigest ApplyRequestDigest,
    CovenantDigest EffectDigest);

/// <summary>
/// What a request-identity resolution did.
/// </summary>
public enum LongRunningOperationRequestIdentityOutcome
{

    /// <summary>No operation existed under this identity, so one was created.</summary>
    Created = 0,

    /// <summary>An operation already existed under this identity with the same apply digest.</summary>
    Replayed = 1,

    /// <summary>
    /// An operation exists under this identity with a different apply digest. Nothing was created.
    /// </summary>
    DigestConflict = 2,

}

/// <summary>
/// The outcome of one request-identity resolution.
/// </summary>
/// <remarks>
/// <paramref name="Operation"/> is null exactly when the outcome is
/// <see cref="LongRunningOperationRequestIdentityOutcome.DigestConflict"/>: the caller asked for a
/// different effect under a name that is already taken, and returning the existing operation would
/// invite it to be treated as the one that was requested.
/// </remarks>
public sealed record LongRunningOperationRequestIdentityResult(
    LongRunningOperationRequestIdentityOutcome Outcome,
    LongRunningOperation? Operation);

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

    /// <summary>
    /// Creates the operation under a caller-supplied durable identity, or resolves the one that
    /// identity already names.
    /// </summary>
    /// <remarks>
    /// The identity row and the operation row are written in one transaction, so a crash between them
    /// cannot leave an operation nobody can find by name or a name pointing at nothing.
    /// </remarks>
    Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(
        LongRunningOperationCreateRequest request,
        LongRunningOperationRequestIdentity identity,
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

    /// <summary>
    /// The normalized request-identity row an operation was created under, or null when it was
    /// created without one.
    /// </summary>
    /// <remarks>
    /// Null is an ordinary answer rather than a failure. A server-generated operation has no
    /// identity row at all, and a caller that treated absence as corruption would turn every
    /// unnamed operation into a recovery escalation.
    /// </remarks>
    Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(
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

    /// <summary>
    /// Starts, or replays, an operation the caller named itself.
    /// </summary>
    /// <remarks>
    /// A replayed operation is returned without acquiring a second lease: the first caller may still
    /// be running it, and handing a second owner the same work is precisely what the durable identity
    /// exists to prevent. A digest conflict returns
    /// <c>Security.IdempotencyConflict</c> and creates nothing.
    /// </remarks>
    Task<Result<LongRunningOperationRequestIdentityResult>> StartWithRequestIdentityAsync(
        LongRunningOperationCreateRequest request,
        LongRunningOperationRequestIdentity identity,
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
