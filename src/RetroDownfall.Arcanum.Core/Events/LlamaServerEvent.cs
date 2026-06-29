using RetroDownfall.Arcanum.Core.LlamaCpp;

namespace RetroDownfall.Arcanum.Core.Events;

/// <summary>
/// Lifecycle frame for a managed <c>llama-server</c> instance.
/// </summary>
/// <remarks>
/// W4.1: published on <see cref="IEventBus"/> and consumed in-process by <c>LlamaServerManager</c>;
/// it is <b>internal/diagnostic only</b> — there is intentionally no <c>/api/events/llama</c> SSE
/// subscriber (unlike <c>/events/daemon</c> and <c>/events/mcp</c>). Add one here and in
/// <c>EventEndpoints</c> if external observation is ever required.
/// </remarks>
public sealed record LlamaServerEvent(
    DateTimeOffset Timestamp,
    string CacheKey,
    LlamaServerState State,
    int? Port = null,
    string? Message = null) : ArcanumEvent(Timestamp);
