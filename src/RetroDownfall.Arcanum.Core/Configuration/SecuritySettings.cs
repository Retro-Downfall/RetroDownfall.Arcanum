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

}
