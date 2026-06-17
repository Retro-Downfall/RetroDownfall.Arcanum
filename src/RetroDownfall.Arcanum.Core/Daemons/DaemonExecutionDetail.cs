using RetroDownfall.Arcanum.Core.Logging;

namespace RetroDownfall.Arcanum.Core.Daemons;

public sealed record DaemonExecutionDetail(
    string Id,
    string DaemonId,
    string DaemonName,
    DaemonJobStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage,
    LogEntry[] Logs);
