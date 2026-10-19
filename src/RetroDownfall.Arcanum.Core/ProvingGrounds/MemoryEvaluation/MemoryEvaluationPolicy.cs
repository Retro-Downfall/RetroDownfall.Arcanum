using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

public static class MemoryEvaluationPolicy
{
    public static MemoryEvaluationPairRelation Relate(ImmutableArray<MemoryEvaluationVerdict> control, ImmutableArray<MemoryEvaluationVerdict> treatment)
    {
        if (control.IsDefaultOrEmpty || control.Length != treatment.Length || control.Length > MemoryEvaluationLimits.MaxChecks)
        {
            throw new ArgumentException("A pair requires equal nonempty bounded verdict vectors.");
        }

        bool gain = false;

        bool regression = false;

        for (int index = 0; index < control.Length; index++)
        {
            if (!Enum.IsDefined(control[index]) || !Enum.IsDefined(treatment[index]))
            {
                throw new ArgumentException("A verdict must be Pass or Fail.");
            }

            gain |= control[index] == MemoryEvaluationVerdict.Fail && treatment[index] == MemoryEvaluationVerdict.Pass;

            regression |= control[index] == MemoryEvaluationVerdict.Pass && treatment[index] == MemoryEvaluationVerdict.Fail;
        }

        return (gain, regression) switch
        {
            (true, true) => MemoryEvaluationPairRelation.Mixed,
            (true, false) => MemoryEvaluationPairRelation.Improved,
            (false, true) => MemoryEvaluationPairRelation.Regressed,
            _ => MemoryEvaluationPairRelation.Equivalent
        };
    }

    public static MemoryEvaluationDecision Classify(MemoryEvaluationAggregate aggregate, int requestedPairCount)
    {
        if (requestedPairCount is < MemoryEvaluationLimits.MinPairs or > MemoryEvaluationLimits.MaxPairs
            || aggregate.CompletedPairs < 0 || aggregate.ValidPairs < 0 || aggregate.InvalidPairs < 0
            || aggregate.ImprovedPairs < 0 || aggregate.RegressedPairs < 0 || aggregate.EquivalentPairs < 0 || aggregate.MixedPairs < 0
            || aggregate.CompletedPairs > requestedPairCount
            || (long)aggregate.ValidPairs + aggregate.InvalidPairs != aggregate.CompletedPairs
            || (long)aggregate.ImprovedPairs + aggregate.RegressedPairs + aggregate.EquivalentPairs + aggregate.MixedPairs != aggregate.ValidPairs
            || !Enum.IsDefined(aggregate.FailureReason))
        {
            return Insufficient(MemoryEvaluationReason.InvalidEvaluation);
        }

        if (aggregate.FailureReason != MemoryEvaluationReason.None)
        {
            return Insufficient(aggregate.FailureReason);
        }

        if (aggregate.InvalidPairs != 0)
        {
            return Insufficient(MemoryEvaluationReason.InvalidEvaluation);
        }

        if (aggregate.ValidPairs != requestedPairCount)
        {
            return Insufficient(MemoryEvaluationReason.Incomplete);
        }

        return aggregate switch
        {
            { ImprovedPairs: var count } when count == requestedPairCount => new(MemoryEvaluationOutcome.Benefit, MemoryEvaluationReason.None),
            { RegressedPairs: var count } when count == requestedPairCount => new(MemoryEvaluationOutcome.Harm, MemoryEvaluationReason.None),
            { EquivalentPairs: var count } when count == requestedPairCount => new(MemoryEvaluationOutcome.NoMeasurableEffect, MemoryEvaluationReason.None),
            _ => Insufficient(MemoryEvaluationReason.MixedOutcomes)
        };
    }

    private static MemoryEvaluationDecision Insufficient(MemoryEvaluationReason reason) => new(MemoryEvaluationOutcome.InsufficientEvidence, reason);
}
