using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class SagaRankingPolicyTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Exposure_and_success_counts_alone_never_earn_usefulness_credit()
    {
        SagaRankingComponents result = Evaluate(new(RetrievalCount: long.MaxValue, AdmissionCount: long.MaxValue, SuccessfulUseCount: long.MaxValue));

        Assert.Equal(0f, result.UsefulnessModifier);

        Assert.Equal(0.8f, result.Score);

        Assert.Equal("exposure-only", result.EvidenceClass);
    }

    [Fact]
    public void Counterfactual_benefit_requires_a_committed_successful_exact_use()
    {
        SagaRankingComponents result = Evaluate(new(BenefitCount: 4));

        Assert.Equal(0f, result.UsefulnessModifier);

        Assert.Equal(0.8f, result.Score);

        Assert.Equal("counterfactual-uncorroborated", result.EvidenceClass);
    }

    [Theory]
    [InlineData(1, 0, 0.01f)]
    [InlineData(4, 0, 0.04f)]
    [InlineData(int.MaxValue, 0, 0.04f)]
    [InlineData(4, 3, 0.01f)]
    [InlineData(3, 4, -0.01f)]
    [InlineData(0, int.MaxValue, -0.04f)]
    public void Counterfactual_net_credit_is_bounded_without_integer_overflow(int benefits, int harms, float expected)
    {
        SagaRankingComponents result = Evaluate(new(BenefitCount: benefits, HarmCount: harms, SuccessfulUseCount: 1));

        Assert.Equal(expected, result.UsefulnessModifier, 6);

        Assert.Equal(0.8f + expected, result.Score, 6);

        Assert.Equal("counterfactual", result.EvidenceClass);
    }

    [Fact]
    public void Harm_remains_a_penalty_without_successful_use()
    {
        SagaRankingComponents result = Evaluate(new(BenefitCount: 4, HarmCount: 6));

        Assert.Equal(-0.02f, result.UsefulnessModifier, 6);
    }

    [Fact]
    public void No_measurable_effect_produces_no_credit()
    {
        SagaRankingComponents result = Evaluate(new(NoEffectCount: int.MaxValue, SuccessfulUseCount: 1));

        Assert.Equal(0f, result.UsefulnessModifier);

        Assert.Equal(0.8f, result.Score);

        Assert.Equal(int.MaxValue, result.NoEffectCount);
    }

    [Fact]
    public void Invalid_negative_evidence_cannot_gain_credit()
    {
        SagaRankingComponents result = Evaluate(new(BenefitCount: int.MaxValue, HarmCount: -1, SuccessfulUseCount: 1));

        Assert.Equal(0f, result.UsefulnessModifier);

        Assert.Equal("invalid", result.EvidenceClass);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Nonfinite_similarity_is_rejected_before_it_can_gain_credit(float similarity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SagaRankingPolicy.Evaluate(
            similarity, Snapshot(new(BenefitCount: 4, SuccessfulUseCount: 1)), Created, enabled: true));
    }

    [Theory]
    [InlineData(0, 0f, false)]
    [InlineData(90, -0.025f, false)]
    [InlineData(180, -0.05f, false)]
    [InlineData(364.999, -0.05f, false)]
    [InlineData(365, -0.05f, true)]
    [InlineData(1000, -0.05f, true)]
    public void Decay_penalizes_age_without_deleting_and_excludes_at_the_dormancy_boundary(double days, float expected, bool dormant)
    {
        SagaRankingSnapshot snapshot = Snapshot();

        SagaRankingComponents result = SagaRankingPolicy.Evaluate(0.8f, snapshot, Created.AddDays(days), enabled: true);

        Assert.Equal(expected, result.RecencyModifier, 6);

        Assert.Equal(dormant, result.Dormant);

        Assert.Equal(0.8f + expected, result.Score, 6);

        Assert.False(result.DecayExempt);

        Assert.False(snapshot.Retired);

        Assert.False(snapshot.Consolidated);
    }

    [Fact]
    public void A_corroborated_benefit_refreshes_age_for_its_exact_version()
    {
        SagaRankingSnapshot snapshot = Snapshot(new(BenefitCount: 1, LastBenefitAtUtc: Created.AddDays(390), SuccessfulUseCount: 1));

        SagaRankingComponents result = SagaRankingPolicy.Evaluate(0.8f, snapshot, Created.AddDays(400), enabled: true);

        Assert.False(result.Dormant);

        Assert.Equal(-0.002777778f, result.RecencyModifier, 6);

        Assert.Equal(0.01f, result.UsefulnessModifier, 6);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void A_timestamp_without_valid_corroborated_benefit_does_not_refresh_age(int benefits, long successfulUses)
    {
        SagaRankingSnapshot snapshot = Snapshot(new(BenefitCount: benefits, LastBenefitAtUtc: Created.AddDays(390), SuccessfulUseCount: successfulUses));

        SagaRankingComponents result = SagaRankingPolicy.Evaluate(0.8f, snapshot, Created.AddDays(400), enabled: true);

        Assert.True(result.Dormant);

        Assert.Equal(-0.05f, result.RecencyModifier, 6);
    }

    [Fact]
    public void A_future_benefit_does_not_refresh_age()
    {
        SagaRankingSnapshot snapshot = Snapshot(new(BenefitCount: 1, LastBenefitAtUtc: Created.AddDays(401), SuccessfulUseCount: 1));

        SagaRankingComponents result = SagaRankingPolicy.Evaluate(0.8f, snapshot, Created.AddDays(400), enabled: true);

        Assert.True(result.Dormant);

        Assert.Equal(-0.05f, result.RecencyModifier, 6);
    }

    [Fact]
    public void An_instant_before_creation_never_creates_positive_recency_credit()
    {
        SagaRankingComponents result = SagaRankingPolicy.Evaluate(0.8f, Snapshot(), Created.AddDays(-1), enabled: true);

        Assert.Equal(0f, result.RecencyModifier);

        Assert.Equal(0.8f, result.Score);

        Assert.False(result.Dormant);
    }

    [Theory]
    [InlineData(true, AnnalOrigin.AgentExtracted)]
    [InlineData(false, AnnalOrigin.OperatorStated)]
    public void Pin_and_operator_origin_exempt_decay_without_exempting_harm(bool pinned, AnnalOrigin origin)
    {
        SagaRankingSnapshot snapshot = Snapshot(new(HarmCount: 4)) with { Pinned = pinned, Origin = origin };

        SagaRankingComponents result = SagaRankingPolicy.Evaluate(0.8f, snapshot, Created.AddDays(1000), enabled: true);

        Assert.True(result.DecayExempt);

        Assert.False(result.Dormant);

        Assert.Equal(0f, result.RecencyModifier);

        Assert.Equal(-0.04f, result.UsefulnessModifier, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void A_versionless_legacy_memory_remains_neutral_without_manufacturing_origin(string? version)
    {
        SagaRankingSnapshot snapshot = Snapshot(new(BenefitCount: 4, SuccessfulUseCount: 1)) with
        {
            VersionId = version,

            Origin = AnnalOrigin.OperatorStated,
        };

        SagaRankingComponents result = SagaRankingPolicy.Evaluate(0.8f, snapshot, Created.AddDays(1000), enabled: true);

        Assert.Equal(0.8f, result.Score);

        Assert.Equal(0f, result.UsefulnessModifier);

        Assert.Equal(0f, result.OriginModifier);

        Assert.Equal(0f, result.RecencyModifier);

        Assert.True(result.DecayExempt);

        Assert.False(result.Dormant);

        Assert.Equal("unversioned", result.EvidenceClass);
    }

    [Fact]
    public void A_version_with_unknown_origin_still_decays()
    {
        SagaRankingComponents result = SagaRankingPolicy.Evaluate(0.8f,
            Snapshot() with { Origin = null }, Created.AddDays(365), enabled: true);

        Assert.Equal(0f, result.OriginModifier);

        Assert.False(result.DecayExempt);

        Assert.True(result.Dormant);
    }

    [Fact]
    public void The_most_harmed_operator_statement_outranks_the_most_reinforced_inference_at_equal_cosine()
    {
        SagaRankingComponents operatorStatement = SagaRankingPolicy.Evaluate(0.8f,
            Snapshot(new(HarmCount: int.MaxValue)) with { Origin = AnnalOrigin.OperatorStated }, Created.AddDays(1000), enabled: true);

        SagaRankingComponents inference = Evaluate(new(BenefitCount: int.MaxValue, SuccessfulUseCount: 1));

        Assert.Equal(0.09f, operatorStatement.OriginModifier, 6);

        Assert.Equal(0.85f, operatorStatement.Score, 6);

        Assert.Equal(0.84f, inference.Score, 6);

        Assert.True(operatorStatement.Score > inference.Score);
    }

    [Fact]
    public void The_disabled_policy_preserves_raw_similarity_without_temporal_or_decay_filtering()
    {
        SagaRankingSnapshot snapshot = Snapshot(new(BenefitCount: 4, SuccessfulUseCount: 1)) with
        {
            Origin = AnnalOrigin.OperatorStated,

            ValidToUtc = Created.AddDays(1),
        };

        SagaRankingComponents result = SagaRankingPolicy.Evaluate(0.8f, snapshot, Created.AddDays(1000), enabled: false);

        Assert.Equal(0.8f, result.Score);

        Assert.Equal(0f, result.UsefulnessModifier);

        Assert.Equal(0f, result.RecencyModifier);

        Assert.Equal(0f, result.OriginModifier);

        Assert.False(result.Dormant);

        Assert.True(result.TemporallyValid);

        Assert.Equal("disabled", result.EvidenceClass);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void A_pin_never_overrides_inclusive_valid_from_and_exclusive_valid_to(int days, bool valid)
    {
        SagaRankingSnapshot snapshot = Snapshot() with { Pinned = true, ValidToUtc = Created.AddDays(2) };

        SagaRankingComponents result = SagaRankingPolicy.Evaluate(0.8f, snapshot, Created.AddDays(days), enabled: true);

        Assert.Equal(valid, result.TemporallyValid);
    }

    [Fact]
    public void Equal_inputs_produce_identical_components_without_mutating_evidence()
    {
        SagaRankingSnapshot snapshot = Snapshot(new(BenefitCount: 2, HarmCount: 1, NoEffectCount: 3, SuccessfulUseCount: 1));

        SagaRankingComponents first = SagaRankingPolicy.Evaluate(0.8f, snapshot, Created.AddDays(90), enabled: true);

        SagaRankingComponents second = SagaRankingPolicy.Evaluate(0.8f, snapshot, Created.AddDays(90), enabled: true);

        Assert.Equal(first, second);

        Assert.Equal(2, first.BenefitCount);

        Assert.Equal(1, first.HarmCount);

        Assert.Equal(3, first.NoEffectCount);

        Assert.Equal(2, snapshot.Evidence.BenefitCount);

        Assert.Equal("version-one", first.VersionId);
    }

    private static SagaRankingComponents Evaluate(SagaRankingEvidence evidence) =>
        SagaRankingPolicy.Evaluate(0.8f, Snapshot(evidence), Created, enabled: true);

    private static SagaRankingSnapshot Snapshot(SagaRankingEvidence? evidence = null) =>
        new("version-one", AnnalOrigin.AgentExtracted, Created, Created, null, false, false, false, evidence ?? new());
}
