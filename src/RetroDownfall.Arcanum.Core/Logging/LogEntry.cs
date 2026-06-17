namespace RetroDownfall.Arcanum.Core.Logging;

public sealed record LogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception,
    string? CorrelationId,
    string? TraceId,
    Dictionary<string, string> Properties);
