using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>One ordinary full-history summary input, proved clean before any text was read.</summary>
public sealed record SessionSummaryMaintenanceEntry(
    Guid EntryId,
    long Sequence,
    int Role,
    string Content,
    DateTimeOffset CreatedAt);

/// <summary>The exact bounded clean snapshot consumed by background Session compression.</summary>
public sealed record SessionSummaryMaintenanceInput(
    Guid SessionId,
    string? PreviousSummary,
    DateTimeOffset? PreviousWatermark,
    ImmutableArray<SessionSummaryMaintenanceEntry> Entries,
    long HistoryRevision,
    long SensitivityRevision,
    int EntryLimit,
    CovenantDigest SourceDigest);

/// <summary>Reads and publishes ordinary Session summaries through an admitted clean snapshot.</summary>
public interface ISessionSummaryMaintenanceStore
{
    Task<Result<SessionSummaryMaintenanceInput?>> PrepareAsync(
        Guid sessionId,
        int entryLimit,
        CancellationToken cancellationToken);

    Task<Result> PublishAsync(
        SessionSummaryMaintenanceInput input,
        string summary,
        CancellationToken cancellationToken);
}
