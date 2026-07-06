namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record SecuritySettings
{

    public int MaxApiKeyHeaderUtf16Chars { get; init; } = 512;

    /// <summary>
    /// TTL (seconds) for the in-memory cache of the expected API key digest. After this
    /// window, the filter re-reads the secret store so on-disk rotation takes effect without
    /// a process restart.
    /// </summary>
    public int ApiKeyCacheTtlSeconds { get; init; } = 30;

    /// <summary>
    /// How long a cached <c>Idempotency-Key</c> response is replayed before it is treated as
    /// expired (and eligible for cleanup). See <see cref="ArcanumSettingClamps.SecurityIdempotencyTtlHours"/>.
    /// </summary>
    public int IdempotencyTtlHours { get; init; } = 24;

    /// <summary>
    /// Maximum buffered response size, in bytes, that will be cached for an <c>Idempotency-Key</c>
    /// request. Responses that exceed this cap while streaming are still delivered to the client in
    /// full — only the cache entry is skipped. See <see cref="ArcanumSettingClamps.SecurityIdempotencyMaxResponseBytes"/>.
    /// </summary>
    public int IdempotencyMaxResponseBytes { get; init; } = 10 * 1024 * 1024;

}
