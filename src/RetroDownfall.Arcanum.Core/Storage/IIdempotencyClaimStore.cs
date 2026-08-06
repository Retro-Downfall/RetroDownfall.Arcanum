namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Durable idempotency claims with separate claim-key identity and request fingerprint (Stripe-style).
/// Self-sufficient for replay; <see cref="IdempotencyClaim.RunId"/> is optional analytics join (0D).
/// </summary>
public interface IIdempotencyClaimStore
{

    Task<IdempotencyClaim?> TryGetAsync(string claimKeyHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks a claim up by its identity rather than its key hash. Crash recovery holds the claim id
    /// from the durable operation ledger, never the caller's original key (#40).
    /// </summary>
    Task<IdempotencyClaim?> GetByIdAsync(Guid claimId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically inserts or reclaims a <see cref="IdempotencyClaimState.Running"/> claim, or returns
    /// the existing claim. <see cref="IdempotencyClaimState.Claimed"/> is recoverable pre-execution
    /// state and is never executable. A different fingerprint returns conflict.
    /// </summary>
    Task<IdempotencyClaimAcquireResult> TryAcquireAsync(
        IdempotencyClaimAcquireRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Renews a Running claim only while <paramref name="ownerId"/> still owns it.</summary>
    /// <returns><see langword="true"/> when the claim was renewed; otherwise ownership was lost.</returns>
    Task<bool> HeartbeatAsync(
        Guid claimId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a terminal Completed response. Requires <paramref name="terminalStreamValid"/>.
    /// Partial/abandoned streams must use <see cref="MarkAbandonedAsync"/> or <see cref="MarkFailedAsync"/>.
    /// </summary>
    Task CompleteAsync(
        Guid claimId,
        string ownerId,
        int statusCode,
        string? contentType,
        string responseBody,
        bool terminalStreamValid,
        Guid? runId,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(Guid claimId, string ownerId, CancellationToken cancellationToken = default);

    Task MarkAbandonedAsync(Guid claimId, string ownerId, CancellationToken cancellationToken = default);

    /// <summary>Reclaims an expired Running/Claimed lease for a new owner.</summary>
    Task<bool> TryReclaimAsync(
        Guid claimId,
        string newOwnerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task LinkRunAsync(Guid claimId, Guid runId, CancellationToken cancellationToken = default);

    Task<int> DeleteExpiredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);

}

public enum IdempotencyClaimState
{

    Claimed = 0,

    Running = 1,

    Completed = 2,

    Failed = 3,

    Abandoned = 4,

}

public sealed record IdempotencyClaim(
    Guid Id,
    string ClaimKeyHash,
    string FingerprintHash,
    IdempotencyClaimState State,
    string OwnerId,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset HeartbeatAt,
    Guid? RunId,
    int? StatusCode,
    string? ContentType,
    string? ResponseBody,
    bool TerminalStreamComplete,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record IdempotencyClaimAcquireRequest(
    string ClaimKeyHash,
    string FingerprintHash,
    string OwnerId,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset CreatedAt);

public sealed record IdempotencyClaimAcquireResult(
    bool Conflict,
    bool Acquired,
    IdempotencyClaim Claim);
