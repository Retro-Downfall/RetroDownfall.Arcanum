namespace RetroDownfall.Arcanum.Core.Logging;

public sealed record LogQueryRequest(
    LogLevel? MinLevel = null,
    string? Category = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Search = null,
    int? Limit = null,
    long? BeforeSequence = null);
