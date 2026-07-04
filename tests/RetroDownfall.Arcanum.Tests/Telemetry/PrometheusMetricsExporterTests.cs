using System.Diagnostics.Metrics;

using RetroDownfall.Arcanum.Infrastructure.Telemetry;

namespace RetroDownfall.Arcanum.Tests.Telemetry;

/// <summary>
/// Each test creates its own <see cref="PrometheusMetricsExporter"/> and a dedicated
/// <see cref="Meter"/> per instrument, naming test instruments with an <c>arcanum_test_</c> prefix
/// (allowlisted, per <c>PrometheusMetricsExporter</c>'s render-time prefix filter) so tests are
/// isolated from both each other and from any real Arcanum/runtime metrics active in the same test
/// process. Assertions use substring checks rather than full-string equality, since the exporter also
/// listens to the process-wide built-in runtime meters (System.Runtime, Kestrel, etc.), whose exact
/// output is not under test here.
/// </summary>
public sealed class PrometheusMetricsExporterTests
{

    [Fact]
    public async Task RenderMetrics_emits_help_and_type_lines()
    {

        using Meter meter = new("Arcanum.Tests." + nameof(RenderMetrics_emits_help_and_type_lines));

        Counter<long> counter = meter.CreateCounter<long>("arcanum_test_help_check_total", description: "Test help text");

        PrometheusMetricsExporter exporter = new();

        counter.Add(1);

        string result = await exporter.RenderMetricsAsync();

        Assert.Contains("# HELP arcanum_test_help_check_total Test help text", result, StringComparison.Ordinal);

        Assert.Contains("# TYPE arcanum_test_help_check_total counter", result, StringComparison.Ordinal);

    }

    [Fact]
    public async Task RenderMetrics_formats_counter_with_labels()
    {

        using Meter meter = new("Arcanum.Tests." + nameof(RenderMetrics_formats_counter_with_labels));

        Counter<long> counter = meter.CreateCounter<long>("arcanum_test_requests_total");

        PrometheusMetricsExporter exporter = new();

        counter.Add(1, new KeyValuePair<string, object?>("endpoint", "/x"), new KeyValuePair<string, object?>("method", "GET"));

        counter.Add(2, new KeyValuePair<string, object?>("endpoint", "/x"), new KeyValuePair<string, object?>("method", "GET"));

        string result = await exporter.RenderMetricsAsync();

        Assert.Contains("arcanum_test_requests_total{endpoint=\"/x\",method=\"GET\"} 3", result, StringComparison.Ordinal);

    }

    [Fact]
    public async Task RenderMetrics_formats_gauge()
    {

        using Meter meter = new("Arcanum.Tests." + nameof(RenderMetrics_formats_gauge));

        UpDownCounter<long> gauge = meter.CreateUpDownCounter<long>("arcanum_test_gauge");

        PrometheusMetricsExporter exporter = new();

        gauge.Add(5);

        gauge.Add(-2);

        string result = await exporter.RenderMetricsAsync();

        Assert.Contains("# TYPE arcanum_test_gauge gauge", result, StringComparison.Ordinal);

        Assert.Contains("arcanum_test_gauge 3", result, StringComparison.Ordinal);

    }

    [Fact]
    public async Task RenderMetrics_formats_histogram_with_buckets_sum_and_count()
    {

        using Meter meter = new("Arcanum.Tests." + nameof(RenderMetrics_formats_histogram_with_buckets_sum_and_count));

        // Exactly representable in IEEE-754 double so sum/count assertions can use plain string
        // equality instead of tolerance-based floating point comparison.
        Histogram<double> histogram = meter.CreateHistogram<double>("arcanum_test_hist", "s");

        PrometheusMetricsExporter exporter = new();

        histogram.Record(0.5);

        histogram.Record(1.0);

        histogram.Record(2.0);

        string result = await exporter.RenderMetricsAsync();

        Assert.Contains("# TYPE arcanum_test_hist histogram", result, StringComparison.Ordinal);

        Assert.Contains("arcanum_test_hist_bucket{le=\"0.1\"} 0", result, StringComparison.Ordinal);

        Assert.Contains("arcanum_test_hist_bucket{le=\"0.5\"} 1", result, StringComparison.Ordinal);

        Assert.Contains("arcanum_test_hist_bucket{le=\"1\"} 2", result, StringComparison.Ordinal);

        Assert.Contains("arcanum_test_hist_bucket{le=\"5\"} 3", result, StringComparison.Ordinal);

        Assert.Contains("arcanum_test_hist_bucket{le=\"300\"} 3", result, StringComparison.Ordinal);

        Assert.Contains("arcanum_test_hist_bucket{le=\"+Inf\"} 3", result, StringComparison.Ordinal);

        Assert.Contains("arcanum_test_hist_sum 3.5", result, StringComparison.Ordinal);

        Assert.Contains("arcanum_test_hist_count 3", result, StringComparison.Ordinal);

    }

    [Fact]
    public async Task RenderMetrics_handles_empty_meter()
    {

        PrometheusMetricsExporter exporter = new();

        string result = await exporter.RenderMetricsAsync();

        Assert.NotNull(result);

        Assert.DoesNotContain("arcanum_test_never_created", result, StringComparison.Ordinal);

        // The active-sessions gauge is always rendered by RenderMetricsAsync regardless of what
        // instruments (if any) have recorded measurements, so this confirms rendering completed rather
        // than short-circuiting on the "nothing recorded yet" path.
        Assert.Contains("arcanum_sessions_active", result, StringComparison.Ordinal);

    }

    [Fact]
    public async Task RenderMetrics_reports_supplied_active_sessions_count()
    {

        PrometheusMetricsExporter exporter = new();

        string result = await exporter.RenderMetricsAsync(activeSessions: 7);

        Assert.Contains("arcanum_sessions_active 7", result, StringComparison.Ordinal);

    }

}
