namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Stateless helper for calculating inference cost from token counts and a <see cref="ModelPricingEntry"/>.
/// Uses decimal arithmetic to avoid the precision loss that integer division would introduce.
/// </summary>
public static class CostCalculator
{

    /// <summary>
    /// Calculates the cost in USD from input and output token counts using the supplied pricing.
    /// Formula: (inputTokens * inputPer1M) / 1_000_000m + (outputTokens * outputPer1M) / 1_000_000m.
    /// </summary>
    /// <param name="inputTokens">Number of input (prompt) tokens.</param>
    /// <param name="outputTokens">Number of output (completion) tokens.</param>
    /// <param name="pricing">Pricing rates per 1M tokens.</param>
    /// <returns>Total cost in USD, never negative.</returns>
    public static decimal CalculateCost(long inputTokens, long outputTokens, ModelPricingEntry pricing)
    {

        long clampedInput = Math.Max(0L, inputTokens);

        long clampedOutput = Math.Max(0L, outputTokens);

        decimal inputCost = (clampedInput * pricing.InputPer1M) / 1_000_000m;

        decimal outputCost = (clampedOutput * pricing.OutputPer1M) / 1_000_000m;

        return inputCost + outputCost;

    }

}
