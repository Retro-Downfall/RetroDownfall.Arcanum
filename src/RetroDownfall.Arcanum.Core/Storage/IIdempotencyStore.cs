namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Grimoire-backed cache of replayed responses for the <c>Idempotency-Key</c> request header
/// (RFC-style client-supplied replay protection — see <c>docs/Arcanum.DESIGN.md</c> §11.17). Keyed by a SHA-256 hash
/// of the client's key plus the canonical request body, so two different bodies sent under the
/// same header value are never conflated.
/// </summary>
public interface IIdempotencyStore
{

    /// <summary>Returns the cached record for <paramref name="keyHash"/>, or <c>null</c> if absent or older than <paramref name="notOlderThan"/> (expired).</summary>
    Task<IdempotencyRecord?> TryGetAsync(string keyHash, DateTimeOffset notOlderThan, CancellationToken cancellationToken = default);

    Task SaveAsync(
        string keyHash,
        int statusCode,
        string? contentType,
        string responseBody,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes every row with <see cref="IdempotencyRecord.CreatedAt"/> older than <paramref name="olderThan"/>. Returns the number of rows removed.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);

}

public sealed record IdempotencyRecord(
    string KeyHash,
    int StatusCode,
    string? ContentType,
    string ResponseBody,
    DateTimeOffset CreatedAt);
