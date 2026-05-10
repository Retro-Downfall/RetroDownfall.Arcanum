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

}
