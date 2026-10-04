using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Memory;

public interface IMemoryReviewTokenCodec
{
    Result<string> IssueCursor(MemoryReviewCursorTokenFacts facts);

    Result<MemoryReviewCursorTokenFacts> ReadCursor(string token);

    Result<string> IssueObservation(MemoryReviewObservationTokenFacts facts);

    Result<MemoryReviewObservationTokenFacts> ReadObservation(string token);

    Result<string> IssuePreparedPlan(MemoryReviewPreparedPlanTokenFacts facts);

    Result<MemoryReviewPreparedPlanTokenFacts> ReadPreparedPlan(string token);
}
