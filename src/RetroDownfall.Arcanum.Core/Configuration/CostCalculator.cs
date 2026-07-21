namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Stateless helper for calculating inference cost from token counts and a <see cref="ModelPricingEntry"/>.
/// Uses decimal arithmetic to avoid the precision loss that integer division would introduce.
/// </summary>
public static class CostCalculator
{

    /// <summary>
    /// Calculates the cost in USD from input, output, and cached token counts using the supplied pricing.
    /// Billable input = max(0, inputTokens - cachedTokens) at <see cref="ModelPricingEntry.InputPer1M"/>;
    /// cached tokens priced at <see cref="ModelPricingEntry.CachedPer1M"/>.
    /// </summary>
    public static decimal CalculateCost(
        long inputTokens,
        long outputTokens,
        long cachedTokens,
        ModelPricingEntry pricing)
    {
        long clampedInput = Math.Max(0L, inputTokens);
        long clampedOutput = Math.Max(0L, outputTokens);
        long clampedCached = Math.Max(0L, Math.Min(cachedTokens, clampedInput));
        long billableInput = clampedInput - clampedCached;

        decimal inputCost = (billableInput * pricing.InputPer1M) / 1_000_000m;
        decimal cachedCost = (clampedCached * pricing.CachedPer1M) / 1_000_000m;
        decimal outputCost = (clampedOutput * pricing.OutputPer1M) / 1_000_000m;

        return inputCost + cachedCost + outputCost;
    }

    /// <summary>
    /// Calculates the cost in USD from input and output token counts (cached tokens treated as zero).
    /// </summary>
    public static decimal CalculateCost(long inputTokens, long outputTokens, ModelPricingEntry pricing) =>
        CalculateCost(inputTokens, outputTokens, cachedTokens: 0L, pricing);

}
