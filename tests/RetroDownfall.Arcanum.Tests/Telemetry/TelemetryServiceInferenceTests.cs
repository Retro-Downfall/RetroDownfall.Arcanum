using System.Diagnostics;

using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Tests.Telemetry;

/// <summary>
/// Covers the inference fan-in that produces the cache-hit/miss and reasoning/standard splits the
/// Command Center telemetry pane renders. These fields are derived inside
/// <see cref="TelemetryService.GetSnapshot"/>, so they are only exercised by driving the real
/// <see cref="ArcanumMetrics"/> instruments — constructing a <see cref="TelemetrySnapshot"/> by hand
/// asserts nothing but the record's own constructor.
/// </summary>
[Collection("Telemetry")]
public sealed class TelemetryServiceInferenceTests
{

    [Fact]
    public void Derives_cache_and_reasoning_splits_from_inference_instruments()
    {

        using TelemetryService telemetry = new();

        TagList promptTags = new()
        {
            { "provider", "test-provider" },
            { "model", "test-model" },
            { "direction", "prompt" },
        };

        TagList completionTags = new()
        {
            { "provider", "test-provider" },
            { "model", "test-model" },
            { "direction", "completion" },
        };

        TagList providerModelTags = new()
        {
            { "provider", "test-provider" },
            { "model", "test-model" },
        };

        ArcanumMetrics.InferenceTokensTotal.Add(500, promptTags);

        ArcanumMetrics.InferenceTokensTotal.Add(400, promptTags);

        ArcanumMetrics.InferenceTokensTotal.Add(300, completionTags);

        ArcanumMetrics.ReasoningTokensTotal.Add(120, providerModelTags);

        ArcanumMetrics.PromptCacheTokensTotal.Add(700, providerModelTags);

        ArcanumMetrics.InferenceDuration.Record(0.5, providerModelTags);

        TelemetrySnapshot snapshot = telemetry.GetSnapshot();

        Assert.Equal(900, snapshot.InputTokens);

        Assert.Equal(700, snapshot.InputCacheHits);

        Assert.Equal(200, snapshot.InputCacheMisses);

        Assert.Equal(300, snapshot.OutputTokens);

        Assert.Equal(120, snapshot.OutputReasoningTokens);

        Assert.Equal(180, snapshot.OutputStandardTokens);

        Assert.Equal(TimeSpan.FromMilliseconds(500), snapshot.CumulativeLatency);

    }

    [Fact]
    public void Clamps_derived_splits_when_provider_reports_more_than_the_totals()
    {

        using TelemetryService telemetry = new();

        TagList promptTags = new()
        {
            { "provider", "test-provider" },
            { "model", "test-model" },
            { "direction", "prompt" },
        };

        TagList completionTags = new()
        {
            { "provider", "test-provider" },
            { "model", "test-model" },
            { "direction", "completion" },
        };

        TagList providerModelTags = new()
        {
            { "provider", "test-provider" },
            { "model", "test-model" },
        };

        ArcanumMetrics.InferenceTokensTotal.Add(100, promptTags);

        ArcanumMetrics.InferenceTokensTotal.Add(50, completionTags);

        ArcanumMetrics.PromptCacheTokensTotal.Add(250, providerModelTags);

        ArcanumMetrics.ReasoningTokensTotal.Add(90, providerModelTags);

        TelemetrySnapshot snapshot = telemetry.GetSnapshot();

        Assert.Equal(0, snapshot.InputCacheMisses);

        Assert.Equal(0, snapshot.OutputStandardTokens);

    }

}
