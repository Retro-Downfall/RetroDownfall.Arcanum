using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Memory;

public interface IMemoryReviewTokenCodec
{
    Result<string> IssueCursor(MemoryReviewCursorTokenFacts facts);

    Result<string> IssueCursor(MemoryReviewCursorTokenFacts facts, out DateTimeOffset expiresAtUtc);

    Result<MemoryReviewCursorTokenFacts> ReadCursor(string token);

    Result<string> IssueObservation(MemoryReviewObservationTokenFacts facts);

    Result<string> IssueObservation(MemoryReviewObservationTokenFacts facts, out DateTimeOffset expiresAtUtc);

    Result<MemoryReviewObservationTokenFacts> ReadObservation(string token);

    /// <summary>On success, reports the authenticated remaining lifetime against the current UTC clock.</summary>
    Result<MemoryReviewObservationTokenFacts> ReadObservation(string token, out DateTimeOffset expiresAtUtc);

    Result<string> IssuePreparedPlan(MemoryReviewPreparedPlanTokenFacts facts);

    Result<string> IssuePreparedPlan(MemoryReviewPreparedPlanTokenFacts facts, out DateTimeOffset expiresAtUtc);

    Result<MemoryReviewPreparedPlanTokenFacts> ReadPreparedPlan(string token);
}
