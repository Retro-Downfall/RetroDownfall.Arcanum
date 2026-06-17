using RetroDownfall.Arcanum.Core.LlamaCpp;

namespace RetroDownfall.Arcanum.Core.Events;

/// <summary>
/// Lifecycle frame for a managed <c>llama-server</c> instance.
/// </summary>
public sealed record LlamaServerEvent(
    DateTimeOffset Timestamp,
    string CacheKey,
    LlamaServerState State,
    int? Port = null,
    string? Message = null) : ArcanumEvent(Timestamp);
