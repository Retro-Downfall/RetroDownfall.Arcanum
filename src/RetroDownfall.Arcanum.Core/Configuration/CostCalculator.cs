namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Stateless helper for calculating inference cost from token counts and a <see cref="ModelPricingEntry"/>.
/// Uses decimal arithmetic to avoid the precision loss that integer division would introduce.
/// </summary>
public static class CostCalculator
{

    /// <summary>
    /// Calculates the cost in USD from input, output, cached, and reasoning token counts using the
    /// supplied pricing.
    /// Billable input = max(0, inputTokens - cachedTokens) at <see cref="ModelPricingEntry.InputPer1M"/>;
    /// cached tokens are priced at <see cref="ModelPricingEntry.CachedPer1M"/>. Reasoning is a subset
    /// of output and is priced at <see cref="ModelPricingEntry.ReasoningPer1M"/>, falling back to
    /// <see cref="ModelPricingEntry.OutputPer1M"/>.
    /// </summary>
    public static decimal CalculateCost(
        long inputTokens,
        long outputTokens,
        long cachedTokens,
        long reasoningTokens,
        ModelPricingEntry pricing)
    {
        long clampedInput = Math.Max(0L, inputTokens);
        long clampedOutput = Math.Max(0L, outputTokens);
        long clampedCached = Math.Max(0L, Math.Min(cachedTokens, clampedInput));
        long clampedReasoning = Math.Max(0L, Math.Min(reasoningTokens, clampedOutput));
        long billableInput = clampedInput - clampedCached;
        long nonReasoningOutput = clampedOutput - clampedReasoning;

        decimal inputRate = ArcanumSettingClamps.PricingRatePer1M(pricing.InputPer1M);
        decimal cachedRate = ArcanumSettingClamps.PricingRatePer1M(pricing.CachedPer1M);
        decimal outputRate = ArcanumSettingClamps.PricingRatePer1M(pricing.OutputPer1M);
        decimal reasoningRate = ArcanumSettingClamps.PricingRatePer1M(
            pricing.ReasoningPer1M ?? outputRate);

        decimal inputCost = (billableInput * inputRate) / 1_000_000m;
        decimal cachedCost = (clampedCached * cachedRate) / 1_000_000m;
        decimal outputCost = (nonReasoningOutput * outputRate) / 1_000_000m;
        decimal reasoningCost = (clampedReasoning * reasoningRate) / 1_000_000m;

        return inputCost + cachedCost + outputCost + reasoningCost;
    }

    /// <summary>Calculates cost with reasoning tokens treated as zero.</summary>
    public static decimal CalculateCost(
        long inputTokens,
        long outputTokens,
        long cachedTokens,
        ModelPricingEntry pricing) =>
        CalculateCost(inputTokens, outputTokens, cachedTokens, reasoningTokens: 0L, pricing);

    /// <summary>
    /// Calculates the cost in USD from input and output token counts (cached tokens treated as zero).
    /// </summary>
    public static decimal CalculateCost(long inputTokens, long outputTokens, ModelPricingEntry pricing) =>
        CalculateCost(inputTokens, outputTokens, cachedTokens: 0L, reasoningTokens: 0L, pricing);

    /// <summary>
    /// Calculates potential or realized savings for prompt tokens served at the cached-input rate
    /// instead of the ordinary input rate.
    /// </summary>
    public static decimal CalculatePromptCacheSavings(
        long cachedOrEligibleTokens,
        ModelPricingEntry pricing)
    {
        long tokens = Math.Max(0L, cachedOrEligibleTokens);
        decimal inputRate = ArcanumSettingClamps.PricingRatePer1M(pricing.InputPer1M);
        decimal cachedRate = ArcanumSettingClamps.PricingRatePer1M(pricing.CachedPer1M);
        decimal rateDelta = Math.Max(0m, inputRate - cachedRate);

        return (tokens * rateDelta) / 1_000_000m;
    }

}
