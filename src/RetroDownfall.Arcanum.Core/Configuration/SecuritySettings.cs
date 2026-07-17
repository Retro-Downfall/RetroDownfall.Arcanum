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

    /// <summary>
    /// When <c>false</c> (default), <c>execute_command</c> / <c>run_spell_script</c> children require an OS
    /// filesystem jail where one is active for this beta (macOS deprecated <c>sandbox-exec</c>). Linux Landlock
    /// is present in-tree but inactive — fail-closed unless this escape hatch is true. If the jail cannot be
    /// applied, the tool is denied rather than running unbounded. When <c>true</c>, Arcanum logs a warning
    /// (platform, tool, campaign id) and runs with resource limits / env scrub only (no FS jail). Windows never
    /// provides an FS jail; when Sanctum path-boundary enforcement is active, those tools are denied regardless
    /// of this flag (escape hatch does not bypass that denial). This MVP is filesystem-only — it does not
    /// isolate network use by child binaries.
    /// </summary>
    public bool AllowUnsandboxedToolChildren { get; init; } = false;

}
