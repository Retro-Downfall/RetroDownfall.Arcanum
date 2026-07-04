namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Stable event-type keys used by <see cref="SseConnectionGate"/> and <see cref="SseConnectionCounter"/>
/// to track per-event-type SSE connection counts. Shared by endpoint call sites and tests so the same
/// key is always used for a given stream family.
/// </summary>
public static class SseEventTypes
{

    public const string Daemon = "DaemonEvent";

    public const string Mcp = "McpServerEvent";

    public const string Logs = "LogEntry";

    public const string Session = "SessionStream";

    public const string Chronicle = "Chronicle";

}
