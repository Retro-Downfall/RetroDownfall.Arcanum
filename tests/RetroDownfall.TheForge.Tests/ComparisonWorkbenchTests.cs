using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.TheForge.Core.Models.Comparisons;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services.Comparisons;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class ComparisonCostEstimatorTests
{

    [Fact]
    public void Estimate_WithoutUsageOrPricing_IsUnavailable()
    {

        (string label, decimal? usd) = ComparisonCostEstimator.Estimate(null, "gpt", null);

        Assert.Equal(ComparisonCostEstimator.CostUnavailable, label);

        Assert.Null(usd);

    }

    [Fact]
    public void Estimate_WithPricing_IsLabeledEstimated()
    {

        PricingSettings pricing = new()
        {
            DefaultPricing = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m },
        };

        ChatCompletionUsage usage = new(1_000_000, 1_000_000, 2_000_000);

        (string label, decimal? usd) = ComparisonCostEstimator.Estimate(usage, "unknown", pricing);

        Assert.StartsWith(ComparisonCostEstimator.EstimatedPrefix, label, StringComparison.Ordinal);

        Assert.Equal(3m, usd);

    }

    [Fact]
    public void Estimate_WithReasoningUsage_SplitsCompletionCost()
    {

        PricingSettings pricing = new()
        {
            DefaultPricing = new ModelPricingEntry
            {
                OutputPer1M = 2m,
                ReasoningPer1M = 8m,
            },
        };

        ChatCompletionUsage usage = new(
            PromptTokens: 0,
            CompletionTokens: 1_000_000,
            TotalTokens: 1_000_000,
            ReasoningTokens: 250_000);

        (_, decimal? usd) = ComparisonCostEstimator.Estimate(usage, "unknown", pricing);

        Assert.Equal(3.5m, usd);

    }

    [Fact]
    public void Estimate_WithReasoningOnlyPricing_IsAvailable()
    {

        PricingSettings pricing = new()
        {
            DefaultPricing = new ModelPricingEntry { ReasoningPer1M = 8m },
        };

        ChatCompletionUsage usage = new(
            PromptTokens: 0,
            CompletionTokens: 1_000_000,
            TotalTokens: 1_000_000,
            ReasoningTokens: 250_000);

        (string label, decimal? usd) =
            ComparisonCostEstimator.Estimate(usage, "unknown", pricing);

        Assert.StartsWith(ComparisonCostEstimator.EstimatedPrefix, label, StringComparison.Ordinal);
        Assert.Equal(2m, usd);

    }

}

public class ComparisonRunStoreTests
{

    [Fact]
    public async Task RoundTrip_CapsRuns()
    {

        string path = Path.Combine(Path.GetTempPath(), $"forge-cmp-{Guid.NewGuid():N}.json");

        try
        {

            ComparisonRunStore store = new(path, maxRuns: 2);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            List<ComparisonRunRecord> runs =
            [
                new(Guid.NewGuid(), now.AddMinutes(-3), now.AddMinutes(-2), "comparison", null, "a", []),
                new(Guid.NewGuid(), now.AddMinutes(-2), now.AddMinutes(-1), "comparison", null, "b", []),
                new(Guid.NewGuid(), now.AddMinutes(-1), now, "comparison", null, "c", []),
            ];

            await store.SaveAsync(new ComparisonStoreDocument(1, now, now, runs));

            ComparisonStoreDocument loaded = await store.LoadAsync();

            Assert.Equal(2, loaded.Runs.Count);

            Assert.Equal(runs[2].Id, loaded.Runs[0].Id);

        }
        finally
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }

    }

}
