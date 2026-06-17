namespace RetroDownfall.Arcanum.Core.Logging;

public sealed record LogQueryResult(
    LogEntry[] Entries,
    long? NextBeforeSequence,
    bool HasMore);
