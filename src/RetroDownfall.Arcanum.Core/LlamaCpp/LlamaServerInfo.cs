namespace RetroDownfall.Arcanum.Core.LlamaCpp;

/// <summary>
/// Metadata for a managed <c>llama-server</c> process.
/// </summary>
public sealed record LlamaServerInfo
{

    public string CacheKey { get; init; } = string.Empty;

    public LlamaServerState State { get; init; }

    public int Port { get; init; }

    public string Endpoint { get; init; } = string.Empty;

    public int? ProcessId { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public string? LastError { get; init; }

}
