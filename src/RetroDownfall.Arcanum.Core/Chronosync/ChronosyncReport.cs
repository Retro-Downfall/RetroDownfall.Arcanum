using RetroDownfall.Arcanum.Core.Pattern.Entities;

namespace RetroDownfall.Arcanum.Core.Chronosync;

public sealed record ChronosyncReport(
    DateTimeOffset? PreviousSnapshotTime,
    string[] NewThreads,
    string[] MissingThreads,
    bool DomainChanged,
    DomainType? PreviousDomain = null);
