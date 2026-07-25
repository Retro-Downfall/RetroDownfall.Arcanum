using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class BudgetReservationEstimateTests
{
    [Fact]
    public void TurnEstimate_TreatsReasoningAsSubsetOfOutputHeadroom()
    {
        ModelPricingEntry pricing = new()
        {
            OutputPer1M = 20m,
            ReasoningPer1M = 80m,
        };

        decimal estimate = BudgetReservationService.EstimateWorstCaseTurnUsd(
            pricing,
            maxOutputTokens: 1_000,
            reasoningBudgetTokens: 600);

        decimal expectedPerCall =
            (400m * 20m / 1_000_000m)
            + (600m * 80m / 1_000_000m);

        Assert.Equal(expectedPerCall * TurnLimitsDefaults.MaxModelCalls, estimate);
    }

    [Fact]
    public void TurnEstimate_UsesLargerReasoningBudgetAsConservativeOutputHeadroom()
    {
        ModelPricingEntry pricing = new()
        {
            OutputPer1M = 20m,
            ReasoningPer1M = 80m,
        };

        decimal estimate = BudgetReservationService.EstimateWorstCaseTurnUsd(
            pricing,
            maxOutputTokens: 1_000,
            reasoningBudgetTokens: 2_000);

        decimal expectedPerCall = 2_000m * 80m / 1_000_000m;

        Assert.Equal(expectedPerCall * TurnLimitsDefaults.MaxModelCalls, estimate);
    }

    [Fact]
    public void TurnEstimate_WhenReasoningIsCheaper_ReservesAtHigherOutputRate()
    {
        ModelPricingEntry pricing = new()
        {
            OutputPer1M = 80m,
            ReasoningPer1M = 20m,
        };

        decimal estimate = BudgetReservationService.EstimateWorstCaseTurnUsd(
            pricing,
            maxOutputTokens: 1_000,
            reasoningBudgetTokens: 600);

        decimal expectedPerCall = 1_000m * 80m / 1_000_000m;

        Assert.Equal(expectedPerCall * TurnLimitsDefaults.MaxModelCalls, estimate);
    }

    [Fact]
    public void BatchEstimate_RemainsSingleCallAndDoesNotAssumeReasoning()
    {
        ModelPricingEntry pricing = new()
        {
            InputPer1M = 10m,
            OutputPer1M = 20m,
            ReasoningPer1M = 80m,
        };

        decimal estimate = BudgetReservationService.EstimateWorstCaseBatchLineUsd(
            pricing,
            maxOutputTokens: 1_000);

        Assert.Equal(0.03m, estimate);
    }

    [Fact]
    public void TurnEstimate_MissingAndNonPositiveTokenOverrides_FallBackToSafeDefaults()
    {
        ModelPricingEntry pricing = new()
        {
            InputPer1M = 10m,
            OutputPer1M = 20m,
            ReasoningPer1M = 80m,
        };

        decimal missingOutputOverride = BudgetReservationService.EstimateWorstCaseTurnUsd(
            pricing,
            maxOutputTokens: null,
            reasoningBudgetTokens: 0);
        decimal nonPositiveOverrides = BudgetReservationService.EstimateWorstCaseTurnUsd(
            pricing,
            maxOutputTokens: 0,
            reasoningBudgetTokens: 0);

        decimal expectedPerCall = 4096m * (10m + 20m) / 1_000_000m;
        decimal expected = expectedPerCall * TurnLimitsDefaults.MaxModelCalls;

        Assert.Equal(expected, missingOutputOverride);
        Assert.Equal(expected, nonPositiveOverrides);
    }
}
