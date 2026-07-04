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
