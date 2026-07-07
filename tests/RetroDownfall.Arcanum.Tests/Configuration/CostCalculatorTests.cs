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

}
