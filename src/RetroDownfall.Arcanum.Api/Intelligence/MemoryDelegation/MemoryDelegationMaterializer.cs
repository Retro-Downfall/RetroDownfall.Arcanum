using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;

internal static class MemoryDelegationMaterializer
{
    // The caller retains counted source ownership throughout every consumer of this document.
    internal static SystemPromptDocument Append(SystemPromptDocument document, MemoryEvaluationSourceSnapshot? sources)
    {
        if (sources is null) return document;

        List<PromptSegment> segments = [.. document.OrderedSegments];

        foreach (var fragment in sources.Fragments)
        {
            var coordinate = sources.Coordinates[fragment.SourceIndex];

            if (fragment.Placement is not MemoryEvaluationPromptPart.System)
                throw new InvalidOperationException("Unsupported delegated memory placement.");

            var kind = coordinate.Store is MemoryEvaluationSource.Covenant
                ? coordinate.Lane is CovenantLane.Proposed ? PromptSegmentKind.CovenantProposed
                    : coordinate.Scope is MemoryEvaluationScopeKind.Global ? PromptSegmentKind.CovenantGlobalConfirmed : PromptSegmentKind.CovenantCampaignConfirmed
                : PromptSegmentKind.Data;

            segments.Add(new(kind, PromptSegmentStability.Volatile, fragment.Text,
                CacheBoundaryEligible: false, Sensitive: true));
        }

        return new(segments);
    }
}
