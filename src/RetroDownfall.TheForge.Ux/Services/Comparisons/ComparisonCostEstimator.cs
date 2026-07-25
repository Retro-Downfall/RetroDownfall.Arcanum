using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.TheForge.Ux.Services.Comparisons;

/// <summary>
/// Honest cost labeling for Comparison Workbench results.
/// Exact turn cost is not exposed on NDJSON streams; estimates use <c>GET /api/config</c> Pricing when available.
/// </summary>
public static class ComparisonCostEstimator
{

    public const string CostUnavailable = "cost unavailable";

    public const string EstimatedPrefix = "estimated";

    public static (string Label, decimal? CostUsd) Estimate(
        ChatCompletionUsage? usage,
        string? model,
        PricingSettings? pricing)
    {

        if (usage is null || pricing is null)
        {

            return (CostUnavailable, null);

        }

        ModelPricingEntry entry = pricing.ResolveForModel(model);

        if (entry.InputPer1M == 0m
            && entry.OutputPer1M == 0m
            && entry.CachedPer1M == 0m
            && (entry.ReasoningPer1M ?? 0m) == 0m)
        {

            return (CostUnavailable, null);

        }

        decimal cost = CostCalculator.CalculateCost(
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.CachedTokens,
            usage.ReasoningTokens,
            entry);

        return ($"{EstimatedPrefix} ${cost.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}", cost);

    }

}
