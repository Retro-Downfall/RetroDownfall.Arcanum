using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Tests.Telemetry;

[Collection("Telemetry")]
public sealed class WebResearchMetricsTests
{
    [Fact]
    public void Web_research_instruments_record_low_cardinality_measurements()
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
        KeyValuePair<string, object?> model = new("model", "sonar");

        ArcanumMetrics.WebResearchRequestsTotal.Add(
            1,
            provider,
            new KeyValuePair<string, object?>("operation", "search"),
            new KeyValuePair<string, object?>("outcome", "success"));
        ArcanumMetrics.WebResearchDuration.Record(
            0.125,
            provider,
            new KeyValuePair<string, object?>("operation", "search"),
            new KeyValuePair<string, object?>("outcome", "success"));
        ArcanumMetrics.WebResearchTokensTotal.Add(
            42,
            provider,
            model,
            new KeyValuePair<string, object?>("kind", "completion"));
        ArcanumMetrics.WebResearchSearchQueriesTotal.Add(2, provider, model);
        ArcanumMetrics.WebResearchCostUsdTotal.Add(0.01, provider, model);

        Assert.Equal(1, captured["arcanum_web_research_requests_total"]);
        Assert.Equal(0.125, captured["arcanum_web_research_duration_seconds"]);
        Assert.Equal(42, captured["arcanum_web_research_tokens_total"]);
        Assert.Equal(2, captured["arcanum_web_research_search_queries_total"]);
        Assert.Equal(0.01, captured["arcanum_web_research_cost_usd_total"]);
    }

    private static bool TagsContainMarker(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string marker)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Value is string value
                && string.Equals(value, marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
