using System.Collections.Concurrent;

using System.Diagnostics.Metrics;

using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Tests.Telemetry;

/// <summary>
/// Verifies the <see cref="ArcanumMetrics"/> instruments are wired to the shared <c>"Arcanum"</c> meter
/// correctly by attaching a raw <see cref="MeterListener"/> and asserting recorded measurements are
/// observed. <see cref="ArcanumMetrics.Meter"/> is a process-wide static shared with any other code
/// exercising real Arcanum instrumentation in the same test run, so each test tags its measurement with
/// a unique marker value and filters captured measurements down to that marker rather than asserting on
/// the full captured set.
/// </summary>
[Collection("Telemetry")]
public sealed class ArcanumMetricsTests
{

    [Fact]
    public void HttpRequestsTotal_increments_correctly()
    {

        string marker = Guid.NewGuid().ToString("N");

        ConcurrentQueue<long> captured = new();

        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) => activeListener.EnableMeasurementEvents(instrument),
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {

            if (instrument.Name == "arcanum_http_requests_total" && TagsContainMarker(tags, marker))
            {

                captured.Enqueue(measurement);

            }

        });

        listener.Start();

        ArcanumMetrics.HttpRequestsTotal.Add(
            1,
            new KeyValuePair<string, object?>("endpoint", marker),
            new KeyValuePair<string, object?>("method", "GET"),
            new KeyValuePair<string, object?>("status_code", "200"));

        long capturedValue = Assert.Single(captured);

        Assert.Equal(1, capturedValue);

    }

    [Fact]
    public void InferenceDuration_records_values()
    {

        string marker = Guid.NewGuid().ToString("N");

        ConcurrentQueue<double> captured = new();

        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) => activeListener.EnableMeasurementEvents(instrument),
        };

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {

            if (instrument.Name == "arcanum_inference_duration_seconds" && TagsContainMarker(tags, marker))
            {

                captured.Enqueue(measurement);

            }

        });

        listener.Start();

        ArcanumMetrics.InferenceDuration.Record(
            0.42,
            new KeyValuePair<string, object?>("provider", marker),
            new KeyValuePair<string, object?>("model", "test-model"));

        double capturedValue = Assert.Single(captured);

        Assert.Equal(0.42, capturedValue);

    }

    [Fact]
    public void PromptCacheTokensTotal_increments_with_low_cardinality_labels()
    {

        string marker = Guid.NewGuid().ToString("N");

        ConcurrentQueue<long> captured = new();

        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) => activeListener.EnableMeasurementEvents(instrument),
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {

            if (instrument.Name == "arcanum_prompt_cache_tokens_total" && TagsContainMarker(tags, marker))
            {

                captured.Enqueue(measurement);

            }

        });

        listener.Start();

        ArcanumMetrics.PromptCacheTokensTotal.Add(
            150,
            new KeyValuePair<string, object?>("provider", marker),
            new KeyValuePair<string, object?>("model", "test-model"));

        long capturedValue = Assert.Single(captured);

        Assert.Equal(150, capturedValue);

    }

    [Fact]
    public void PromptCacheHitsTotal_increments_with_low_cardinality_labels()
    {

        string marker = Guid.NewGuid().ToString("N");

        ConcurrentQueue<long> captured = new();

        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) => activeListener.EnableMeasurementEvents(instrument),
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {

            if (instrument.Name == "arcanum_prompt_cache_hits_total" && TagsContainMarker(tags, marker))
            {

                captured.Enqueue(measurement);

            }

        });

        listener.Start();

        ArcanumMetrics.PromptCacheHitsTotal.Add(
            1,
            new KeyValuePair<string, object?>("provider", marker),
            new KeyValuePair<string, object?>("model", "test-model"));

        long capturedValue = Assert.Single(captured);

        Assert.Equal(1, capturedValue);

    }

    [Fact]
    public void PromptCacheCallsAndSavings_RecordNewLowCardinalityInstrumentFamilies()
    {
        string marker = Guid.NewGuid().ToString("N");
        ConcurrentDictionary<string, double> captured = new(StringComparer.Ordinal);
        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) =>
                activeListener.EnableMeasurementEvents(instrument),
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (TagsContainMarker(tags, marker))
            {
                captured[instrument.Name] = measurement;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            if (TagsContainMarker(tags, marker))
            {
                captured[instrument.Name] = measurement;
            }
        });
        listener.Start();
        KeyValuePair<string, object?> provider = new("provider", marker);
        KeyValuePair<string, object?> model = new("model", "cache-model");

        ArcanumMetrics.PromptCacheCallsTotal.Add(
            1,
            provider,
            model,
            new KeyValuePair<string, object?>("purpose", "MainInference"),
            new KeyValuePair<string, object?>("control_mode", "Explicit"),
            new KeyValuePair<string, object?>("eligibility", "Eligible"),
            new KeyValuePair<string, object?>("reason", "None"));
        ArcanumMetrics.PromptCachePotentialSavingsUsdTotal.Add(0.25, provider, model);
        ArcanumMetrics.PromptCacheActualSavingsUsdTotal.Add(0.10, provider, model);

        Assert.Equal(1, captured["arcanum_prompt_cache_calls_total"]);
        Assert.Equal(0.25, captured["arcanum_prompt_cache_potential_savings_usd_total"]);
        Assert.Equal(0.10, captured["arcanum_prompt_cache_actual_savings_usd_total"]);
    }

    [Fact]
    public void ReasoningTokensTotal_increments_with_low_cardinality_labels()
    {
        string marker = Guid.NewGuid().ToString("N");
        ConcurrentQueue<long> captured = new();
        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) =>
                activeListener.EnableMeasurementEvents(instrument),
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "arcanum_inference_reasoning_tokens_total"
                && TagsContainMarker(tags, marker))
            {
                captured.Enqueue(measurement);
            }
        });
        listener.Start();

        ArcanumMetrics.ReasoningTokensTotal.Add(
            75,
            new KeyValuePair<string, object?>("provider", marker),
            new KeyValuePair<string, object?>("model", "test-model"));

        Assert.Equal(75, Assert.Single(captured));
    }

    [Fact]
    public void ContextAccountingMetrics_record_estimate_report_variance_and_rejection()
    {
        string marker = Guid.NewGuid().ToString("N");
        ConcurrentDictionary<string, long> captured = new(StringComparer.Ordinal);
        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) =>
                activeListener.EnableMeasurementEvents(instrument),
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (TagsContainMarker(tags, marker))
            {
                captured[instrument.Name] = measurement;
            }
        });
        listener.Start();
        KeyValuePair<string, object?> provider = new("provider", marker);
        KeyValuePair<string, object?> model = new("model", "test-model");

        ArcanumMetrics.EstimatedInputTokens.Record(100, provider, model);
        ArcanumMetrics.ProviderReportedInputTokens.Record(107, provider, model);
        ArcanumMetrics.InputTokenEstimationVariance.Record(
            7,
            provider,
            model,
            new KeyValuePair<string, object?>("direction", "underestimated"));
        ArcanumMetrics.ContextBudgetRejectionsTotal.Add(1, provider, model);

        Assert.Equal(100, captured["arcanum_estimated_input_tokens"]);
        Assert.Equal(107, captured["arcanum_provider_reported_input_tokens"]);
        Assert.Equal(7, captured["arcanum_input_token_estimation_variance"]);
        Assert.Equal(1, captured["arcanum_context_budget_rejections_total"]);
    }

    private static bool TagsContainMarker(ReadOnlySpan<KeyValuePair<string, object?>> tags, string marker)
    {

        foreach (KeyValuePair<string, object?> tag in tags)
        {

            if (tag.Value is string value && string.Equals(value, marker, StringComparison.Ordinal))
            {

                return true;

            }

        }

        return false;

    }

}
