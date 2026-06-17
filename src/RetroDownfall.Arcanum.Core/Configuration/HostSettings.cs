namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record HostSettings
{

    public int Port { get; init; } = 5001;

    public int RetainedLogFileCount { get; init; } = 7;

    public bool EnableEnterpriseTelemetry { get; init; } = false;

    /// <summary>
    /// Allowed origins for CORS. Defaults to localhost loopback ports.
    /// Use <c>["*"]</c> to allow any origin (browser callers can read responses with the API key).
    /// </summary>
    public string[] CorsAllowedOrigins { get; init; } =
    [
        "http://localhost:5001",
        "http://127.0.0.1:5001",
        "http://localhost:3000",
        "http://127.0.0.1:3000",
    ];

    /// <summary>
    /// When <c>true</c>, mounts the Scalar interactive API documentation UI at <c>/api/scalar</c>.
    /// The UI ships with inline JavaScript and CSS that conflict with strict CSP; default <c>false</c>.
    /// </summary>
    public bool EnableScalarUi { get; init; } = false;

    /// <summary>
    /// Optional stable identifier surfaced as <c>system_fingerprint</c> on OpenAI-shaped
    /// <c>/v1/chat/completions</c> responses. When <c>null</c> (default), the API derives one
    /// from the host assembly's informational version (for example <c>arcanum-0.1.0-beta</c>).
    /// </summary>
    public string? SystemFingerprint { get; init; }

    /// <summary>
    /// When <c>true</c>, Kestrel binds to all network interfaces (<c>ListenAnyIP</c>) instead of
    /// loopback. Default <c>false</c>. The environment variable <c>ARCANUM_HOST_ANY</c> is still
    /// honored as an override (operator container deployments).
    /// </summary>
    public bool ListenAny { get; init; } = false;

    /// <summary>
    /// Kestrel <c>MaxRequestBodySize</c> in bytes. Default 10 MiB; clamped 256 KiB &#8211; 1 GiB.
    /// </summary>
    public long MaxRequestBodyBytes { get; init; } = 10L * 1024L * 1024L;

    /// <summary>
    /// Optional default workspace root for spell management and other workspace-scoped API routes.
    /// Relative paths resolve against the process current directory. Prefer absolute paths in config.
    /// </summary>
    public string? Workspace { get; init; }

    /// <summary>
    /// Optional request rate-limit configuration applied to <c>/api</c> and <c>/v1</c> groups.
    /// </summary>
    public HostRateLimitSettings RateLimit { get; init; } = new();

}

/// <summary>
/// Rate limit configuration; partitions requests by API key (or IP when no key header is present).
/// </summary>
public sealed record HostRateLimitSettings
{

    /// <summary>
    /// When <c>true</c>, registers <c>AddRateLimiter</c> and applies a fixed-window limiter to
    /// the <c>/api</c> and <c>/v1</c> endpoint groups. Default <c>false</c>. Also enabled
    /// automatically when the host binds to all interfaces (<see cref="HostSettings.ListenAny"/>
    /// or <c>ARCANUM_HOST_ANY</c>).
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Maximum requests permitted per <see cref="WindowSeconds"/> per partition. Default 120.
    /// </summary>
    public int PermitLimit { get; init; } = 120;

    /// <summary>
    /// Window size (seconds) for the fixed-window limiter. Default 60.
    /// </summary>
    public int WindowSeconds { get; init; } = 60;

    /// <summary>
    /// Maximum queued requests per partition (responses served once the window resets). Default 0
    /// (no queueing: excess requests are rejected with HTTP 429).
    /// </summary>
    public int QueueLimit { get; init; } = 0;

}
