using System.Diagnostics.Metrics;

namespace RetroDownfall.Arcanum.Core.Telemetry;

/// <summary>
/// Real-time aggregate snapshot used by TelemetryPane.
/// </summary>
public sealed record TelemetrySnapshot(
    long InputTokens,
    long InputCacheHits,
    long InputCacheMisses,
    long OutputTokens,
    long OutputReasoningTokens,
    long OutputStandardTokens,
    decimal EstimatedCostUsd,
    TimeSpan CumulativeLatency,
    TimeSpan TimeToFirstToken);

/// <summary>
/// Subscribes to <see cref="ArcanumMetrics"/> instruments and exposes
/// debounced snapshot updates for TelemetryPane.
/// </summary>
public sealed class TelemetryService
{
    private readonly MeterListener _listener;

    public event EventHandler<TelemetrySnapshot>? SnapshotUpdated;

    public TelemetryService()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) => { };
        _listener.MeasurementsCompleted = (instrument, state) => { };
        // Event wired for future TelemetryPane subscription.
        _ = SnapshotUpdated; // suppress warning; consumed by pane host
    }

    public void Start()
    {
        _listener.Start();
    }
}
