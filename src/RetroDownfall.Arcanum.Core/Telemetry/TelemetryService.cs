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
    TimeSpan TimeToFirstToken)
{
    /// <summary>
    /// Native web-research aggregates. This is a non-positional property so the
    /// original snapshot constructor remains source-compatible with pane clients.
    /// </summary>
    public WebResearchTelemetrySnapshot WebResearch { get; init; } = new();

    /// <summary>
    /// Isolated child-run roll-up. A child terminal transition updates this as one unified
    /// snapshot rather than exposing its internal conversational turns.
    /// </summary>
    public SubagentTelemetrySnapshot Subagents { get; init; } = new();
}

/// <summary>Process-local aggregates for native web-research provider calls.</summary>
public sealed record WebResearchTelemetrySnapshot(
    long Requests = 0,
    long SuccessfulRequests = 0,
    long FailedRequests = 0,
    long PromptTokens = 0,
    long CompletionTokens = 0,
    long TotalTokens = 0,
    long ReasoningTokens = 0,
    long CitationTokens = 0,
    long SearchQueries = 0,
    decimal CostUsd = 0,
    TimeSpan CumulativeLatency = default);

/// <summary>
/// Subscribes to <see cref="ArcanumMetrics"/> instruments and exposes
/// debounced snapshot updates for TelemetryPane.
/// </summary>
public sealed class TelemetryService : ISubagentTelemetrySink, IDisposable
{
    private readonly MeterListener _listener;
    private readonly object _decimalGate = new();

    private long _inputTokens;
    private long _inputCacheHits;
    private long _outputTokens;
    private long _outputReasoningTokens;
    private long _cumulativeLatencyTicks;

    private long _webRequests;
    private long _webSuccessfulRequests;
    private long _webFailedRequests;
    private long _webPromptTokens;
    private long _webCompletionTokens;
    private long _webTotalTokens;
    private long _webReasoningTokens;
    private long _webCitationTokens;
    private long _webSearchQueries;
    private long _webCumulativeLatencyTicks;
    private decimal _webCostUsd;

    private long _subagentRuns;
    private long _subagentCompleted;
    private long _subagentFailed;
    private long _subagentBudgetExhausted;
    private long _subagentTokens;
    private long _subagentLatencyTicks;
    private decimal _subagentCostUsd;
    private int _started;
    private int _disposed;

    public event EventHandler<TelemetrySnapshot>? SnapshotUpdated;

    public TelemetryService()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = static (instrument, listener) =>
        {
            if (string.Equals(
                    instrument.Meter.Name,
                    ArcanumMetrics.Meter.Name,
                    StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
        Start();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _listener.Start();
        }
    }

    public TelemetrySnapshot GetSnapshot()
    {
        long inputTokens = Interlocked.Read(ref _inputTokens);
        long cacheHits = Interlocked.Read(ref _inputCacheHits);
        long outputTokens = Interlocked.Read(ref _outputTokens);
        long reasoningTokens = Interlocked.Read(ref _outputReasoningTokens);
        decimal webCost;
        decimal subagentCost;

        lock (_decimalGate)
        {
            webCost = _webCostUsd;
            subagentCost = _subagentCostUsd;
        }

        WebResearchTelemetrySnapshot webResearch = new(
            Requests: Interlocked.Read(ref _webRequests),
            SuccessfulRequests: Interlocked.Read(ref _webSuccessfulRequests),
            FailedRequests: Interlocked.Read(ref _webFailedRequests),
            PromptTokens: Interlocked.Read(ref _webPromptTokens),
            CompletionTokens: Interlocked.Read(ref _webCompletionTokens),
            TotalTokens: Interlocked.Read(ref _webTotalTokens),
            ReasoningTokens: Interlocked.Read(ref _webReasoningTokens),
            CitationTokens: Interlocked.Read(ref _webCitationTokens),
            SearchQueries: Interlocked.Read(ref _webSearchQueries),
            CostUsd: webCost,
            CumulativeLatency: TimeSpan.FromTicks(
                Interlocked.Read(ref _webCumulativeLatencyTicks)));

        SubagentTelemetrySnapshot subagents = new(
            Runs: Interlocked.Read(ref _subagentRuns),
            Completed: Interlocked.Read(ref _subagentCompleted),
            Failed: Interlocked.Read(ref _subagentFailed),
            BudgetExhausted: Interlocked.Read(ref _subagentBudgetExhausted),
            Tokens: Interlocked.Read(ref _subagentTokens),
            CostUsd: subagentCost,
            CumulativeLatency: TimeSpan.FromTicks(
                Interlocked.Read(ref _subagentLatencyTicks)));

        return new TelemetrySnapshot(
            InputTokens: inputTokens,
            InputCacheHits: cacheHits,
            InputCacheMisses: Math.Max(0, inputTokens - cacheHits),
            OutputTokens: outputTokens,
            OutputReasoningTokens: reasoningTokens,
            OutputStandardTokens: Math.Max(0, outputTokens - reasoningTokens),
            EstimatedCostUsd: 0,
            CumulativeLatency: TimeSpan.FromTicks(
                Interlocked.Read(ref _cumulativeLatencyTicks)),
            TimeToFirstToken: TimeSpan.Zero)
        {
            WebResearch = webResearch,
            Subagents = subagents,
        };
    }

    public void RecordSubagentRun(SubagentTelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);

        Interlocked.Increment(ref _subagentRuns);
        Interlocked.Add(
            ref _subagentTokens,
            Math.Max(0L, telemetryEvent.Tokens));
        Interlocked.Add(
            ref _subagentLatencyTicks,
            Math.Max(0L, telemetryEvent.Latency.Ticks));

        lock (_decimalGate)
        {
            _subagentCostUsd += Math.Max(0m, telemetryEvent.CostUsd);
        }

        switch (telemetryEvent.Outcome)
        {
            case SubagentRunOutcome.Completed:
                Interlocked.Increment(ref _subagentCompleted);
                break;
            case SubagentRunOutcome.BudgetExhausted:
                Interlocked.Increment(ref _subagentFailed);
                Interlocked.Increment(ref _subagentBudgetExhausted);
                break;
            case SubagentRunOutcome.Failed:
            case SubagentRunOutcome.Cancelled:
                Interlocked.Increment(ref _subagentFailed);
                break;
        }

        RaiseSnapshotUpdated();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _listener.Dispose();
        }
    }

    private void OnLongMeasurement(
        Instrument instrument,
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        _ = state;

        switch (instrument.Name)
        {
            case "arcanum_inference_tokens_total":
                if (TagEquals(tags, "direction", "prompt"))
                {
                    Interlocked.Add(ref _inputTokens, measurement);
                }
                else if (TagEquals(tags, "direction", "completion"))
                {
                    Interlocked.Add(ref _outputTokens, measurement);
                }

                break;

            case "arcanum_inference_reasoning_tokens_total":
                Interlocked.Add(ref _outputReasoningTokens, measurement);
                break;

            case "arcanum_prompt_cache_tokens_total":
                Interlocked.Add(ref _inputCacheHits, measurement);
                break;

            case "arcanum_web_research_requests_total":
                Interlocked.Add(ref _webRequests, measurement);

                if (TagEquals(tags, "outcome", "success"))
                {
                    Interlocked.Add(ref _webSuccessfulRequests, measurement);
                }
                else
                {
                    Interlocked.Add(ref _webFailedRequests, measurement);
                }

                break;

            case "arcanum_web_research_tokens_total":
                AddWebTokens(measurement, tags);
                break;

            case "arcanum_web_research_search_queries_total":
                Interlocked.Add(ref _webSearchQueries, measurement);
                break;

            default:
                // The listener is attached to the process-wide ArcanumMetrics meter, so it also
                // observes instruments this service does not roll up. Raising an identical snapshot
                // for them makes SnapshotUpdated fire without anything having changed.
                return;
        }

        RaiseSnapshotUpdated();
    }

    private void OnDoubleMeasurement(
        Instrument instrument,
        double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        _ = tags;
        _ = state;

        if (!double.IsFinite(measurement))
        {
            return;
        }

        switch (instrument.Name)
        {
            case "arcanum_inference_duration_seconds":
                Interlocked.Add(
                    ref _cumulativeLatencyTicks,
                    SecondsToTicks(measurement));
                break;

            case "arcanum_web_research_duration_seconds":
                Interlocked.Add(
                    ref _webCumulativeLatencyTicks,
                    SecondsToTicks(measurement));
                break;

            case "arcanum_web_research_cost_usd_total":
                lock (_decimalGate)
                {
                    _webCostUsd += (decimal)measurement;
                }

                break;

            default:
                // See OnLongMeasurement: an unrelated instrument on the shared meter must not
                // publish a snapshot.
                return;

        }

        RaiseSnapshotUpdated();
    }

    private void AddWebTokens(
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (TagEquals(tags, "kind", "prompt"))
        {
            Interlocked.Add(ref _webPromptTokens, measurement);
        }
        else if (TagEquals(tags, "kind", "completion"))
        {
            Interlocked.Add(ref _webCompletionTokens, measurement);
        }
        else if (TagEquals(tags, "kind", "total"))
        {
            Interlocked.Add(ref _webTotalTokens, measurement);
        }
        else if (TagEquals(tags, "kind", "reasoning"))
        {
            Interlocked.Add(ref _webReasoningTokens, measurement);
        }
        else if (TagEquals(tags, "kind", "citation"))
        {
            Interlocked.Add(ref _webCitationTokens, measurement);
        }
    }

    private void RaiseSnapshotUpdated()
    {
        EventHandler<TelemetrySnapshot>? handler = SnapshotUpdated;

        if (handler is null)
        {
            return;
        }

        TelemetrySnapshot snapshot = GetSnapshot();

        foreach (Delegate subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<TelemetrySnapshot>)subscriber)(this, snapshot);
            }
            catch
            {
                // Telemetry observers are best-effort and must never break the
                // operation that recorded a metric.
            }
        }
    }

    private static bool TagEquals(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string name,
        string expected)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (string.Equals(tag.Key, name, StringComparison.Ordinal)
                && string.Equals(
                    tag.Value?.ToString(),
                    expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static long SecondsToTicks(double seconds)
    {
        double ticks = seconds * TimeSpan.TicksPerSecond;

        if (ticks >= long.MaxValue)
        {
            return long.MaxValue;
        }

        if (ticks <= long.MinValue)
        {
            return long.MinValue;
        }

        return (long)ticks;
    }
}
