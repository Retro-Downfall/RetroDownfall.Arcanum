namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Global settings for the local <c>llama-server</c> child-process backend.
/// </summary>
public sealed record LlamaCppSettings
{

    /// <summary>
    /// Absolute or relative path to the <c>llama-server</c> executable. When <c>null</c>, search <c>PATH</c> (and <c>llama-server.exe</c> on Windows).
    /// </summary>
    public string? ServerExecutablePath { get; init; }

    /// <summary>
    /// GPU layers to offload. <c>0</c> = CPU only. <c>-1</c> = sentinel for offload all (mapped to <c>999</c> on the command line). Clamp -1 – 1024.
    /// </summary>
    public int GpuLayers { get; init; } = 0;

    /// <summary>
    /// Context size passed as <c>--ctx-size</c>. Clamp 256 – 1,048,576.
    /// </summary>
    public int ContextSize { get; init; } = 4096;

    /// <summary>
    /// First port to try when auto-selecting a listen port. Clamp 1 – 65,535.
    /// </summary>
    public int PortStart { get; init; } = 50_000;

    /// <summary>
    /// Number of consecutive ports to try from <see cref="PortStart"/>. Clamp 1 – 65,535.
    /// </summary>
    public int PortRange { get; init; } = 1000;

    /// <summary>
    /// Maximum concurrent inference requests per running server. Clamp 1 – 256.
    /// </summary>
    public int MaxConcurrentRequests { get; init; } = 4;

    /// <summary>
    /// Timeout (seconds) for <c>GET /health</c> probes during startup. Clamp 1 – 600.
    /// </summary>
    public int HealthProbeTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Maximum wait (seconds) for a server to become healthy after spawn. Clamp 1 – 600.
    /// </summary>
    public int StartTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Grace period (seconds) before <c>Kill(entireProcessTree: true)</c> on shutdown. Clamp 1 – 600.
    /// </summary>
    public int ShutdownTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Extra arguments appended to the <c>llama-server</c> command line.
    /// </summary>
    public string[]? AdditionalArguments { get; init; }

    /// <summary>
    /// Maximum cached GGUF entries before LRU eviction. Clamp 1 – 100.
    /// </summary>
    public int MaxCachedModels { get; init; } = 5;

    /// <summary>
    /// Timeout (seconds) for the named <c>HttpClient("LlamaModelDownload")</c> used to fetch GGUF files.
    /// Default 3600; clamp 60 – 86,400.
    /// </summary>
    public int ModelDownloadTimeoutSeconds { get; init; } = 3600;

    /// <summary>
    /// Maximum bytes accepted for a single GGUF download. Default 50 GiB; clamp 1 MiB – 200 GiB.
    /// </summary>
    public long ModelDownloadMaxBytes { get; init; } = 50L * 1024L * 1024L * 1024L;

    /// <summary>
    /// Optional SHA-256 hex digests (lowercase) keyed by model cache key for download verification.
    /// </summary>
    public Dictionary<string, string>? ModelSha256Map { get; init; }

    public bool RequireModelHash { get; init; } = true;

}
