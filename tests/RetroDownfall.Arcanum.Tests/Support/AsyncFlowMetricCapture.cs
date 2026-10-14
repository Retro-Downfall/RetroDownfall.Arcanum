using System.Diagnostics.Metrics;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Captures counter measurements made inside the current async flow only. The Arcanum meter is process-global and tests run in
/// parallel, so a plain listener would count other tests' events; the measurement callback runs on the thread that recorded and
/// reads an <see cref="AsyncLocal{T}"/>, which only the flow that started the capture (and its children) sees.
/// </summary>
internal sealed class AsyncFlowMetricCapture : IDisposable
{
    private static readonly AsyncLocal<AsyncFlowMetricCapture?> Current = new();

    private static readonly MeterListener Listener = CreateListener();

    private readonly string[] _instruments;

    private readonly List<Measurement> _measurements = [];

    private readonly AsyncFlowMetricCapture? _previous;

    internal AsyncFlowMetricCapture(params string[] instruments)
    {
        _instruments = instruments;

        _previous = Current.Value;

        Current.Value = this;
    }

    internal sealed record Measurement(string Instrument, long Value, IReadOnlyDictionary<string, object?> Tags);

    internal IReadOnlyList<Measurement> Measurements
    {
        get
        {
            lock (_measurements)
            {
                return [.. _measurements];
            }
        }
    }

    internal long Sum(string instrument, string? tag = null, string? tagValue = null) => Measurements
        .Where(m => m.Instrument == instrument && (tag is null || m.Tags.TryGetValue(tag, out object? value) && Equals(value, tagValue)))
        .Sum(static m => m.Value);

    public void Dispose() => Current.Value = _previous;

    private static MeterListener CreateListener()
    {
        MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, subscribed) =>
            {
                if (instrument.Meter.Name == "Arcanum" && instrument.Name.StartsWith("arcanum.long_rest.", StringComparison.Ordinal))
                {
                    subscribed.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>(static (instrument, value, tags, _) =>
        {
            AsyncFlowMetricCapture? capture = Current.Value;

            if (capture is null || !capture._instruments.Contains(instrument.Name)) return;

            Dictionary<string, object?> copy = [];

            foreach (KeyValuePair<string, object?> tag in tags) copy[tag.Key] = tag.Value;

            lock (capture._measurements) capture._measurements.Add(new(instrument.Name, value, copy));
        });

        listener.Start();

        return listener;
    }
}
