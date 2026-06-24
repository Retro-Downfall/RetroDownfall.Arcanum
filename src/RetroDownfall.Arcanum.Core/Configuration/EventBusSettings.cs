namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// In-memory event bus tuning for live SSE push updates.
/// </summary>
public sealed record EventBusSettings
{

    /// <summary>
    /// Per-subscriber bounded channel capacity. When full, <c>DropOldest</c> discards the oldest
    /// frame so publishers never block — appropriate for live dashboards that need recent state.
    /// Capacity is applied when a per-event-type hub is first created; if <c>arcanum.json</c>
    /// reloads, existing hubs retain their original capacity (new event types use the updated value).
    /// </summary>
    public int ChannelCapacity { get; init; } = 256;

    /// <summary>
    /// SSE keep-alive comment interval in seconds for <c>/api/events/*</c>, session stream, and Chronicle.
    /// <c>0</c> disables heartbeats.
    /// </summary>
    public int HeartbeatSeconds { get; init; } = 30;

    public int MaxSseConnections { get; init; } = 20;

}
