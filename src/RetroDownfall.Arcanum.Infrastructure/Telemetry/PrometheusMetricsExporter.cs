using System.Collections.Concurrent;

using System.Diagnostics.Metrics;

using System.Globalization;

using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Telemetry;

/// <summary>
/// Singleton Prometheus text-format (<c>0.0.4</c>) exporter for every <c>System.Diagnostics.Metrics</c>
/// meter in the process — the Arcanum meter (<see cref="RetroDownfall.Arcanum.Core.Telemetry.ArcanumMetrics"/>)
/// plus the built-in <c>System.Runtime</c>, <c>Microsoft.AspNetCore.Hosting</c>, and
/// <c>Microsoft.AspNetCore.Server.Kestrel</c> meters. Backed entirely by <see cref="MeterListener"/> and
/// <see cref="StringBuilder"/> — AOT-safe, no reflection-based serialization, no third-party telemetry SDK.
/// </summary>
/// <remarks>
/// <see cref="Histogram{T}"/> in <c>System.Diagnostics.Metrics</c> does not expose Prometheus-compatible
/// bucket boundaries or its own aggregated state. This exporter listens to every raw
/// <c>Histogram&lt;T&gt;.Record</c> call via <see cref="MeterListener"/> and re-buckets each observation
/// into <see cref="HistogramBucketBoundaries"/> itself; the instrument's native aggregation is never read.
/// </remarks>
public sealed class PrometheusMetricsExporter : IDisposable
{

    private static readonly double[] DurationHistogramBucketBoundaries =
    [
        0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30, 60, 300,
    ];

    private static readonly double[] TokenHistogramBucketBoundaries =
    [
        1, 4, 16, 64, 256, 1_024, 4_096, 16_384, 65_536, 262_144, 1_048_576, 2_097_152,
    ];

    /// <summary>
    /// Render-time metric name allowlist. The <see cref="MeterListener"/> subscribes to every published
    /// meter (required to capture the built-in runtime meters), but only sanitized names starting with one
    /// of these prefixes are ever recorded or rendered — this bounds output to Arcanum's own metrics plus
    /// the known .NET/ASP.NET Core runtime meters, so an unrelated third-party meter loaded into the same
    /// process cannot leak unbounded metric noise into the scrape.
    /// </summary>
    private static readonly string[] AllowedMetricNamePrefixes =
    [
        "arcanum_", "process_", "dotnet_", "http_server_", "kestrel_",
    ];

    private const int MaxLabelValueLength = 256;

    private readonly ConcurrentDictionary<string, InstrumentInfo> _instrumentInfo = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, MetricValueState>> _counterSeries = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, MetricValueState>> _gaugeSeries = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, HistogramState>> _histogramSeries = new(StringComparer.Ordinal);

    private readonly object _startLock = new();

    private MeterListener? _listener;

    private int _disposed;

    /// <summary>
    /// Starts the <see cref="MeterListener"/> immediately on construction rather than deferring to the
    /// first scrape. <see cref="MeterListener"/> only observes measurements recorded <em>after</em> it is
    /// started — it never replays history — so a lazy "start on first scrape" would silently drop every
    /// counter/histogram/gauge update that happened between process startup and the first
    /// <c>GET /metrics</c> call (which, for a Prometheus scraper on a 15s interval, could be several
    /// requests' worth of data). Starting eagerly still satisfies the original goal of tolerating meters
    /// registered after DI setup, since <see cref="MeterListener.InstrumentPublished"/> fires for
    /// instruments published at any time, not only ones that already existed when <c>Start()</c> ran.
    /// </summary>
    public PrometheusMetricsExporter()
    {

        EnsureListenerStarted();

    }

    /// <summary>
    /// Renders the current Prometheus text-format scrape body. <paramref name="activeSessions"/> is
    /// supplied by the caller (the <c>/metrics</c> endpoint handler, which owns a request scope and a
    /// scoped <c>ArcanumDbContext</c>) as <c>arcanum_sessions_active</c> — this exporter is a singleton and
    /// deliberately does no database access or caching of its own.
    /// </summary>
    public Task<string> RenderMetricsAsync(long activeSessions = 0, CancellationToken cancellationToken = default)
    {

        cancellationToken.ThrowIfCancellationRequested();

        // Observable instruments (ObservableCounter/ObservableGauge/ObservableUpDownCounter — this is how
        // the built-in System.Runtime / Kestrel / AspNetCore.Hosting meters expose GC, memory, and
        // connection stats) only report a value when polled; this is the poll.
        _listener!.RecordObservableInstruments();

        RegisterManualGauge(
            "arcanum_sessions_active",
            "Number of sessions with Status = active",
            activeSessions);

        StringBuilder output = new(4096);

        foreach (string metricName in _instrumentInfo.Keys.OrderBy(static name => name, StringComparer.Ordinal))
        {

            if (!IsAllowedMetricName(metricName))
            {

                continue;

            }

            InstrumentInfo info = _instrumentInfo[metricName];

            output.Append("# HELP ").Append(metricName).Append(' ').Append(info.Help).Append('\n');

            output.Append("# TYPE ").Append(metricName).Append(' ').Append(info.PrometheusKind).Append('\n');

            switch (info.PrometheusKind)
            {

                case "counter":
                    EmitValueSeries(output, metricName, _counterSeries);
                    break;

                case "histogram":
                    EmitHistogramSeries(output, metricName);
                    break;

                default:
                    EmitValueSeries(output, metricName, _gaugeSeries);
                    break;

            }

        }

        return Task.FromResult(output.ToString());

    }

    public void Dispose()
    {

        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {

            _listener?.Dispose();

        }

    }

    private void EnsureListenerStarted()
    {

        if (_listener is not null)
        {

            return;

        }

        lock (_startLock)
        {

            if (_listener is not null)
            {

                return;

            }

            MeterListener listener = new()
            {
                InstrumentPublished = static (instrument, activeListener) => activeListener.EnableMeasurementEvents(instrument),
            };

            listener.SetMeasurementEventCallback<byte>(OnMeasurementRecorded);

            listener.SetMeasurementEventCallback<short>(OnMeasurementRecorded);

            listener.SetMeasurementEventCallback<int>(OnMeasurementRecorded);

            listener.SetMeasurementEventCallback<long>(OnMeasurementRecorded);

            listener.SetMeasurementEventCallback<float>(OnMeasurementRecorded);

            listener.SetMeasurementEventCallback<double>(OnMeasurementRecorded);

            listener.SetMeasurementEventCallback<decimal>(OnMeasurementRecorded);

            listener.Start();

            _listener = listener;

        }

    }

    private void OnMeasurementRecorded<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
        where T : struct
    {

        string metricName = SanitizeMetricName(instrument.Name);

        if (!IsAllowedMetricName(metricName))
        {

            return;

        }

        double value = ToDouble(measurement);

        (string promKind, bool useSetSemantics) = ClassifyInstrument(instrument);

        InstrumentInfo info = _instrumentInfo.GetOrAdd(
            metricName,
            _ => new InstrumentInfo(
                promKind,
                BuildHelpText(instrument),
                useSetSemantics,
                ResolveHistogramBucketBoundaries(instrument)));

        string labelKey = BuildLabelString(tags);

        if (info.PrometheusKind == "histogram")
        {

            ConcurrentDictionary<string, HistogramState> histogramSeries = _histogramSeries.GetOrAdd(
                metricName,
                static _ => new ConcurrentDictionary<string, HistogramState>(StringComparer.Ordinal));

            HistogramState histogramState = histogramSeries.GetOrAdd(
                labelKey,
                _ => new HistogramState(info.HistogramBucketBoundaries));

            histogramState.Record(value);

            return;

        }

        ConcurrentDictionary<string, ConcurrentDictionary<string, MetricValueState>> seriesByMetric =
            info.PrometheusKind == "counter" ? _counterSeries : _gaugeSeries;

        ConcurrentDictionary<string, MetricValueState> valueSeries = seriesByMetric.GetOrAdd(
            metricName,
            static _ => new ConcurrentDictionary<string, MetricValueState>(StringComparer.Ordinal));

        MetricValueState valueState = valueSeries.GetOrAdd(labelKey, static _ => new MetricValueState());

        if (info.UseSetSemantics)
        {

            valueState.Set(value);

        }
        else
        {

            valueState.Add(value);

        }

    }

    private void RegisterManualGauge(string metricName, string help, double value)
    {

        _instrumentInfo.GetOrAdd(
            metricName,
            _ => new InstrumentInfo("gauge", help, UseSetSemantics: true, []));

        ConcurrentDictionary<string, MetricValueState> series = _gaugeSeries.GetOrAdd(
            metricName,
            static _ => new ConcurrentDictionary<string, MetricValueState>(StringComparer.Ordinal));

        MetricValueState valueState = series.GetOrAdd(string.Empty, static _ => new MetricValueState());

        valueState.Set(value);

    }

    private static void EmitValueSeries(
        StringBuilder output,
        string metricName,
        ConcurrentDictionary<string, ConcurrentDictionary<string, MetricValueState>> seriesByMetric)
    {

        if (!seriesByMetric.TryGetValue(metricName, out ConcurrentDictionary<string, MetricValueState>? series))
        {

            return;

        }

        foreach (KeyValuePair<string, MetricValueState> entry in series.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {

            AppendSampleLine(output, metricName, entry.Key, FormatDouble(entry.Value.Read()));

        }

    }

    private void EmitHistogramSeries(StringBuilder output, string metricName)
    {

        if (!_histogramSeries.TryGetValue(metricName, out ConcurrentDictionary<string, HistogramState>? series))
        {

            return;

        }

        string bucketMetricName = metricName + "_bucket";

        string sumMetricName = metricName + "_sum";

        string countMetricName = metricName + "_count";

        foreach (KeyValuePair<string, HistogramState> entry in series.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {

            (long[] cumulativeBucketCounts, double sum, long count) = entry.Value.Snapshot();

            string baseLabels = entry.Key;

            double[] boundaries = entry.Value.BucketBoundaries;

            for (int i = 0; i < boundaries.Length; i++)
            {

                string bucketLabels = AppendLeLabel(baseLabels, FormatDouble(boundaries[i]));

                AppendSampleLine(output, bucketMetricName, bucketLabels, cumulativeBucketCounts[i].ToString(CultureInfo.InvariantCulture));

            }

            string infLabels = AppendLeLabel(baseLabels, "+Inf");

            AppendSampleLine(output, bucketMetricName, infLabels, cumulativeBucketCounts[^1].ToString(CultureInfo.InvariantCulture));

            AppendSampleLine(output, sumMetricName, baseLabels, FormatDouble(sum));

            AppendSampleLine(output, countMetricName, baseLabels, count.ToString(CultureInfo.InvariantCulture));

        }

    }

    private static void AppendSampleLine(StringBuilder output, string metricName, string labelKey, string formattedValue)
    {

        output.Append(metricName);

        if (labelKey.Length > 0)
        {

            output.Append('{').Append(labelKey).Append('}');

        }

        output.Append(' ').Append(formattedValue).Append('\n');

    }

    private static string AppendLeLabel(string baseLabels, string le)
    {

        string leSegment = string.Concat("le=\"", le, "\"");

        return baseLabels.Length == 0 ? leSegment : string.Concat(baseLabels, ",", leSegment);

    }

    /// <summary>
    /// Classifies a published instrument by its open generic type (metadata-only comparison — AOT-safe,
    /// no dynamic invocation) into a Prometheus metric kind plus whether measurements are absolute
    /// snapshots ("Set") or deltas to accumulate ("Add"). Observable instruments always report the current
    /// absolute value on every poll (per the <see cref="MeterListener.RecordObservableInstruments"/>
    /// contract), so they use Set semantics; eager instruments (<see cref="Counter{T}"/>,
    /// <see cref="UpDownCounter{T}"/>) report deltas via <c>.Add()</c>, so they accumulate.
    /// </summary>
    private static (string PrometheusKind, bool UseSetSemantics) ClassifyInstrument(Instrument instrument)
    {

        Type instrumentType = instrument.GetType();

        Type openGeneric = instrumentType.IsGenericType ? instrumentType.GetGenericTypeDefinition() : instrumentType;

        if (openGeneric == typeof(Histogram<>))
        {

            return ("histogram", false);

        }

        if (openGeneric == typeof(Counter<>))
        {

            return ("counter", false);

        }

        if (openGeneric == typeof(ObservableCounter<>))
        {

            return ("counter", true);

        }

        if (openGeneric == typeof(UpDownCounter<>))
        {

            return ("gauge", false);

        }

        // ObservableUpDownCounter<T> and ObservableGauge<T> (and any other future observable kind) all
        // report an absolute current value on every poll — render as a Prometheus gauge with Set semantics.
        return ("gauge", true);

    }

    private static double ToDouble<T>(T value)
        where T : struct
    {

        return value switch
        {

            byte typed => typed,
            short typed => typed,
            int typed => typed,
            long typed => typed,
            float typed => typed,
            double typed => typed,
            decimal typed => (double)typed,
            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),

        };

    }

    private static string BuildHelpText(Instrument instrument) =>
        string.IsNullOrWhiteSpace(instrument.Description) ? SanitizeMetricName(instrument.Name) : instrument.Description;

    /// <summary>
    /// Converts an in-box runtime instrument name (for example <c>dotnet.gc.collections</c>) to Prometheus
    /// snake_case form (<c>dotnet_gc_collections</c>). Arcanum's own instrument names are already valid
    /// Prometheus names and pass through unchanged.
    /// </summary>
    private static string SanitizeMetricName(string name)
    {

        StringBuilder sanitized = new(name.Length);

        foreach (char c in name)
        {

            sanitized.Append(c is '.' or '-' or ' ' ? '_' : char.ToLowerInvariant(c));

        }

        return sanitized.ToString();

    }

    private static bool IsAllowedMetricName(string metricName)
    {

        foreach (string prefix in AllowedMetricNamePrefixes)
        {

            if (metricName.StartsWith(prefix, StringComparison.Ordinal))
            {

                return true;

            }

        }

        return false;

    }

    private static string BuildLabelString(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {

        if (tags.Length == 0)
        {

            return string.Empty;

        }

        (string Key, string Value)[] pairs = new (string Key, string Value)[tags.Length];

        for (int i = 0; i < tags.Length; i++)
        {

            pairs[i] = (tags[i].Key, FormatLabelValue(tags[i].Value));

        }

        Array.Sort(pairs, static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        StringBuilder labelBuilder = new();

        for (int i = 0; i < pairs.Length; i++)
        {

            if (i > 0)
            {

                labelBuilder.Append(',');

            }

            labelBuilder.Append(pairs[i].Key).Append("=\"").Append(EscapeLabelValue(pairs[i].Value)).Append('"');

        }

        return labelBuilder.ToString();

    }

    /// <summary>
    /// Bounds an arbitrary tag value to <see cref="MaxLabelValueLength"/> characters. Endpoint route
    /// patterns and tool names are already finite/bounded by design, but this guards any unforeseen
    /// high-cardinality tag value from bloating the scrape body.
    /// </summary>
    private static string FormatLabelValue(object? value)
    {

        string formatted = value switch
        {

            null => string.Empty,
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,

        };

        if (formatted.Length > MaxLabelValueLength)
        {

            formatted = string.Concat(formatted.AsSpan(0, MaxLabelValueLength), "[truncated]");

        }

        return formatted;

    }

    private static string EscapeLabelValue(string value)
    {

        if (value.IndexOfAny(['\\', '"', '\n']) < 0)
        {

            return value;

        }

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    }

    private static string FormatDouble(double value)
    {

        if (double.IsNaN(value))
        {

            return "NaN";

        }

        if (double.IsPositiveInfinity(value))
        {

            return "+Inf";

        }

        if (double.IsNegativeInfinity(value))
        {

            return "-Inf";

        }

        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
        {

            return ((long)value).ToString(CultureInfo.InvariantCulture);

        }

        return value.ToString(CultureInfo.InvariantCulture);

    }

    private static double[] ResolveHistogramBucketBoundaries(Instrument instrument) =>
        string.Equals(instrument.Unit, "{tokens}", StringComparison.Ordinal)
            ? TokenHistogramBucketBoundaries
            : DurationHistogramBucketBoundaries;

    private sealed record InstrumentInfo(
        string PrometheusKind,
        string Help,
        bool UseSetSemantics,
        double[] HistogramBucketBoundaries);

    /// <summary>Mutable cumulative-or-current double value backing both counter and gauge series.</summary>
    private sealed class MetricValueState
    {

        private double _value;

        public void Add(double delta)
        {

            lock (this)
            {

                _value += delta;

            }

        }

        public void Set(double value)
        {

            lock (this)
            {

                _value = value;

            }

        }

        public double Read()
        {

            lock (this)
            {

                return _value;

            }

        }

    }

    /// <summary>
    /// Manual Prometheus histogram: a cumulative bucket-count array (index <c>i</c> counts every
    /// observation at or below its instrument-specific boundary, matching Prometheus
    /// expects at render time — no prefix-summing needed), plus running sum and count.
    /// </summary>
    private sealed class HistogramState
    {
        public HistogramState(double[] bucketBoundaries)
        {
            BucketBoundaries = bucketBoundaries;
            _cumulativeBucketCounts = new long[bucketBoundaries.Length + 1];
        }

        private readonly long[] _cumulativeBucketCounts;

        private double _sum;

        private long _count;

        public double[] BucketBoundaries { get; }

        public void Record(double value)
        {

            lock (this)
            {

                _sum += value;

                _count++;

                for (int i = 0; i < BucketBoundaries.Length; i++)
                {

                    if (value <= BucketBoundaries[i])
                    {

                        _cumulativeBucketCounts[i]++;

                    }

                }

                _cumulativeBucketCounts[^1]++;

            }

        }

        public (long[] CumulativeBucketCounts, double Sum, long Count) Snapshot()
        {

            lock (this)
            {

                return ((long[])_cumulativeBucketCounts.Clone(), _sum, _count);

            }

        }

    }

}
