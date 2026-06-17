namespace RetroDownfall.Arcanum.Core.Events;

/// <summary>
/// Lifecycle frame for an Unseen Servant background job, streamed via <c>GET /api/events/daemon</c>.
/// </summary>
public sealed record DaemonEvent(
    DateTimeOffset Timestamp,
    Guid RunId,
    string JobName,
    string TargetSpell,
    DaemonEventType EventType,
    string? Message = null,
    long? DurationMilliseconds = null) : ArcanumEvent(Timestamp);
