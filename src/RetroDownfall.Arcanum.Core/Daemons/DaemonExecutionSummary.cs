namespace RetroDownfall.Arcanum.Core.Daemons;

public sealed record DaemonExecutionSummary(
    string Id,
    string DaemonId,
    string DaemonName,
    DaemonJobStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);
