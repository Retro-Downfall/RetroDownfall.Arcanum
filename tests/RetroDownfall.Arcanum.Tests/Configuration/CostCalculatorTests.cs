using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class CostCalculatorTests
{

    [Fact]
    public void CalculateCost_WithTypicalTokens_ReturnsCorrectCost()
    {

        ModelPricingEntry pricing = new() { InputPer1M = 10.00m, OutputPer1M = 30.00m };

        decimal cost = CostCalculator.CalculateCost(inputTokens: 500_000, outputTokens: 200_000, pricing);

        decimal expected = (500_000m * 10.00m / 1_000_000m) + (200_000m * 30.00m / 1_000_000m);

        Assert.Equal(expected, cost);

    }

    [Fact]
    public void CalculateCost_WithIntegerPrecisionCase_ReturnsFiveDollars()
    {

        // 500,000 tokens at $10/1M must be $5.00, not $0.00 from integer division.
        ModelPricingEntry pricing = new() { InputPer1M = 10.00m, OutputPer1M = 0.00m };

        decimal cost = CostCalculator.CalculateCost(inputTokens: 500_000, outputTokens: 0, pricing);

        Assert.Equal(5.00m, cost);

    }

    [Fact]
    public void CalculateCost_WithZeroPricing_ReturnsZero()
    {

        ModelPricingEntry pricing = new() { InputPer1M = 0.00m, OutputPer1M = 0.00m };

        decimal cost = CostCalculator.CalculateCost(inputTokens: 1_000_000, outputTokens: 1_000_000, pricing);

        Assert.Equal(0.00m, cost);

    }

    [Fact]
    public void CalculateCost_WithNegativeTokens_TreatsAsZero()
    {

        ModelPricingEntry pricing = new() { InputPer1M = 10.00m, OutputPer1M = 30.00m };

        decimal cost = CostCalculator.CalculateCost(inputTokens: -100, outputTokens: -200, pricing);

        Assert.Equal(0.00m, cost);

    }

    [Fact]
    public void CalculateCost_WithCachedTokens_PricesCachedSeparately()
    {
        ModelPricingEntry pricing = new()
        {
            InputPer1M = 10.00m,
            OutputPer1M = 30.00m,
            CachedPer1M = 1.00m,
        };

        decimal cost = CostCalculator.CalculateCost(
            inputTokens: 1_000_000,
            outputTokens: 0,
            cachedTokens: 400_000,
            pricing);

        // 600k billable input @ $10 + 400k cached @ $1 = $6.00 + $0.40
        Assert.Equal(6.40m, cost);
    }

    public static TheoryData<long, decimal, decimal, decimal> PromptCacheSavingsCases => new()
    {
        { 400_000, 10m, 1m, 3.6m },
        { 400_000, 10m, 10m, 0m },
        { 400_000, 10m, 12m, 0m },
        { -1, 10m, 1m, 0m },
    };

    [Theory]
    [MemberData(nameof(PromptCacheSavingsCases))]
    public void CalculatePromptCacheSavings_UsesNonNegativeRateDelta(
        long tokens,
        decimal inputPer1M,
        decimal cachedPer1M,
        decimal expected)
    {
        ModelPricingEntry pricing = new()
        {
            InputPer1M = inputPer1M,
            CachedPer1M = cachedPer1M,
        };

        decimal savings = CostCalculator.CalculatePromptCacheSavings(tokens, pricing);

        Assert.Equal(expected, savings);
    }

    [Fact]
    public void ModelPricingEntry_ExposesNullableReasoningRate()
    {
        ModelPricingEntry pricing = new();

        Assert.Null(pricing.ReasoningPer1M);

        pricing.ReasoningPer1M = 42m;

        Assert.Equal(42m, pricing.ReasoningPer1M);
    }

    [Fact]
    public void CalculateCost_WithReasoningRate_SplitsCompletionWithoutDoubleBilling()
    {
        ModelPricingEntry pricing = new()
        {
            OutputPer1M = 20m,
            ReasoningPer1M = 80m,
        };

        decimal cost = CostCalculator.CalculateCost(
            inputTokens: 0,
            outputTokens: 1_000_000,
            cachedTokens: 0,
            reasoningTokens: 250_000,
            pricing);

        Assert.Equal(35m, cost);
    }

    [Fact]
    public void CalculateCost_WithoutReasoningRate_FallsBackToOutputRate()
    {
        ModelPricingEntry pricing = new() { OutputPer1M = 30m };

        decimal cost = CostCalculator.CalculateCost(
            inputTokens: 0,
            outputTokens: 1_000_000,
            cachedTokens: 0,
            reasoningTokens: 400_000,
            pricing);

        Assert.Equal(30m, cost);
    }

    [Fact]
    public void CalculateCost_WithExplicitZeroReasoningRate_DoesNotFallBackToOutputRate()
    {
        ModelPricingEntry pricing = new()
        {
            OutputPer1M = 20m,
            ReasoningPer1M = 0m,
        };

        decimal cost = CostCalculator.CalculateCost(
            inputTokens: 0,
            outputTokens: 1_000_000,
            cachedTokens: 0,
            reasoningTokens: 250_000,
            pricing);

        Assert.Equal(15m, cost);
    }

    public static TheoryData<long, long, decimal, decimal> ReasoningCostCases => new()
    {
        { 100, 200, 0m, 0.008m },
        { 100, -50, 0.002m, 0m },
    };

    [Theory]
    [MemberData(nameof(ReasoningCostCases))]
    public void CalculateCost_ClampsInconsistentReasoningOnlyForCostSafety(
        long outputTokens,
        long reasoningTokens,
        decimal expectedOutputCost,
        decimal expectedReasoningCost)
    {
        ModelPricingEntry pricing = new()
        {
            OutputPer1M = 20m,
            ReasoningPer1M = 80m,
        };

        decimal cost = CostCalculator.CalculateCost(
            inputTokens: 0,
            outputTokens,
            cachedTokens: 0,
            reasoningTokens,
            pricing);

        Assert.Equal(expectedOutputCost + expectedReasoningCost, cost);
    }

    [Fact]
    public void CalculateCost_ClampsNegativeAndExtremeRatesAtSupportedBoundary()
    {
        ModelPricingEntry pricing = new()
        {
            InputPer1M = -1m,
            OutputPer1M = decimal.MaxValue,
            CachedPer1M = decimal.MaxValue,
            ReasoningPer1M = decimal.MaxValue,
        };

        decimal cost = CostCalculator.CalculateCost(
            inputTokens: long.MaxValue,
            outputTokens: long.MaxValue,
            cachedTokens: long.MaxValue,
            reasoningTokens: long.MaxValue,
            pricing);

        Assert.Equal(18_446_744_073_709_551_614m, cost);
    }

}
