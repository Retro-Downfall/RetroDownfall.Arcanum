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
    public int ChannelCapacity { get; set; } = 256;

    /// <summary>
    /// SSE keep-alive comment interval in seconds for <c>/api/events/*</c>, session stream, and Chronicle.
    /// <c>0</c> disables heartbeats.
    /// </summary>
    public int HeartbeatSeconds { get; set; } = 30;

    /// <summary>
    /// Global cap on concurrent SSE connections across all event streams. Must be at least
    /// <see cref="MaxSseConnectionsPerType"/> for the per-type cap to have any effect (see
    /// <see cref="Configuration.ConfigurationValidator"/>).
    /// </summary>
    public int MaxSseConnections { get; set; } = 50;

    /// <summary>
    /// Per-event-type cap on concurrent SSE connections, enforced in addition to the global
    /// <see cref="MaxSseConnections"/> cap. Guarantees a fair share of the global pool to each
    /// stream family (daemon, MCP, logs, session, Chronicle) so a single greedy client cannot
    /// starve the others.
    /// </summary>
    public int MaxSseConnectionsPerType { get; set; } = 20;

}
