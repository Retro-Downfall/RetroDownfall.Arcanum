using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class MemoryEvaluationPolicyTests
{
    [Fact]
    public void A_gain_and_a_regression_in_one_pair_is_mixed()
    {
        Assert.Equal(MemoryEvaluationPairRelation.Mixed, MemoryEvaluationPolicy.Relate(
            [MemoryEvaluationVerdict.Fail, MemoryEvaluationVerdict.Pass],
            [MemoryEvaluationVerdict.Pass, MemoryEvaluationVerdict.Fail]));
    }

    [Fact]
    public void Same_total_with_different_verdict_vector_is_not_equivalent()
    {
        Assert.NotEqual(MemoryEvaluationPairRelation.Equivalent, MemoryEvaluationPolicy.Relate(
            [MemoryEvaluationVerdict.Fail, MemoryEvaluationVerdict.Pass],
            [MemoryEvaluationVerdict.Pass, MemoryEvaluationVerdict.Fail]));
    }

    [Fact]
    public void An_incomplete_favorable_run_is_insufficient()
    {
        MemoryEvaluationDecision decision = MemoryEvaluationPolicy.Classify(MemoryEvaluationTestData.Aggregate(MemoryEvaluationPairRelation.Improved), 3);

        Assert.Equal(MemoryEvaluationOutcome.InsufficientEvidence, decision.Outcome);

        Assert.Equal(MemoryEvaluationReason.Incomplete, decision.Reason);
    }

    [Theory]
    [InlineData(MemoryEvaluationPairRelation.Improved, MemoryEvaluationOutcome.Benefit, MemoryEvaluationReason.None)]
    [InlineData(MemoryEvaluationPairRelation.Regressed, MemoryEvaluationOutcome.Harm, MemoryEvaluationReason.None)]
    [InlineData(MemoryEvaluationPairRelation.Equivalent, MemoryEvaluationOutcome.NoMeasurableEffect, MemoryEvaluationReason.None)]
    [InlineData(MemoryEvaluationPairRelation.Mixed, MemoryEvaluationOutcome.InsufficientEvidence, MemoryEvaluationReason.MixedOutcomes)]
    public void Complete_uniform_outcomes(MemoryEvaluationPairRelation relation, MemoryEvaluationOutcome outcome, MemoryEvaluationReason reason)
    {
        Assert.Equal(new(outcome, reason), MemoryEvaluationPolicy.Classify(MemoryEvaluationTestData.Aggregate(relation, relation, relation), 3));
    }

    [Theory]
    [InlineData(MemoryEvaluationPairRelation.Improved, MemoryEvaluationPairRelation.Equivalent)]
    [InlineData(MemoryEvaluationPairRelation.Regressed, MemoryEvaluationPairRelation.Equivalent)]
    [InlineData(MemoryEvaluationPairRelation.Improved, MemoryEvaluationPairRelation.Regressed)]
    public void Mixed_run_relations_are_insufficient(MemoryEvaluationPairRelation first, MemoryEvaluationPairRelation second)
    {
        Assert.Equal(new(MemoryEvaluationOutcome.InsufficientEvidence, MemoryEvaluationReason.MixedOutcomes), MemoryEvaluationPolicy.Classify(MemoryEvaluationTestData.Aggregate(first, first, second), 3));
    }

    [Theory]
    [InlineData(MemoryEvaluationVerdict.Fail, MemoryEvaluationVerdict.Pass, MemoryEvaluationPairRelation.Improved)]
    [InlineData(MemoryEvaluationVerdict.Pass, MemoryEvaluationVerdict.Fail, MemoryEvaluationPairRelation.Regressed)]
    [InlineData(MemoryEvaluationVerdict.Fail, MemoryEvaluationVerdict.Fail, MemoryEvaluationPairRelation.Equivalent)]
    [InlineData(MemoryEvaluationVerdict.Pass, MemoryEvaluationVerdict.Pass, MemoryEvaluationPairRelation.Equivalent)]
    public void Single_check_relation_truth_table(MemoryEvaluationVerdict control, MemoryEvaluationVerdict treatment, MemoryEvaluationPairRelation expected)
    {
        Assert.Equal(expected, MemoryEvaluationPolicy.Relate([control], [treatment]));
    }

    [Fact]
    public void Invalid_or_cancelled_samples_preserve_failure_reason()
    {
        MemoryEvaluationAggregate aggregate = MemoryEvaluationTestData.Aggregate(MemoryEvaluationPairRelation.Improved, MemoryEvaluationPairRelation.Improved, MemoryEvaluationPairRelation.Improved) with { FailureReason = MemoryEvaluationReason.Cancelled };

        Assert.Equal(new(MemoryEvaluationOutcome.InsufficientEvidence, MemoryEvaluationReason.Cancelled), MemoryEvaluationPolicy.Classify(aggregate, 3));
    }

    [Fact]
    public void Inconsistent_counts_and_undefined_verdicts_are_invalid()
    {
        Assert.Equal(MemoryEvaluationReason.InvalidEvaluation, MemoryEvaluationPolicy.Classify(MemoryEvaluationTestData.Aggregate() with { CompletedPairs = -1 }, 3).Reason);

        Assert.Throws<ArgumentException>(() => MemoryEvaluationPolicy.Relate([], []));

        Assert.Throws<ArgumentException>(() => MemoryEvaluationPolicy.Relate([(MemoryEvaluationVerdict)99], [MemoryEvaluationVerdict.Pass]));
    }
}
