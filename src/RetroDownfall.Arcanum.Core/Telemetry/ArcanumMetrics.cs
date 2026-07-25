using System.Diagnostics.Metrics;

namespace RetroDownfall.Arcanum.Core.Telemetry;

/// <summary>
/// Central <see cref="Meter"/> and instrument set for Arcanum, backed exclusively by
/// <c>System.Diagnostics.Metrics</c> (in-box, AOT-safe — no OpenTelemetry SDK, no prometheus-net).
/// <see cref="RetroDownfall.Arcanum.Infrastructure.Telemetry.PrometheusMetricsExporter"/> attaches a
/// <see cref="MeterListener"/> to this meter (and the built-in runtime meters) and renders Prometheus
/// text format on scrape; it does not read these instruments' native aggregation.
/// </summary>
public static class ArcanumMetrics
{

    public static readonly Meter Meter = new("Arcanum", "0.1.0");

    /// <summary>Total HTTP requests processed. Labels: <c>endpoint</c> (route pattern, not raw URL), <c>method</c>, <c>status_code</c>.</summary>
    public static readonly Counter<long> HttpRequestsTotal = Meter.CreateCounter<long>(
        "arcanum_http_requests_total", description: "Total HTTP requests processed");

    /// <summary>
    /// Inference turn duration in seconds. Labels: <c>provider</c>, <c>model</c>. Recorded values are
    /// bucketed by <see cref="RetroDownfall.Arcanum.Infrastructure.Telemetry.PrometheusMetricsExporter"/>
    /// into manual Prometheus histogram buckets via a <see cref="MeterListener"/> — this instrument's own
    /// aggregation is not used for export.
    /// </summary>
    public static readonly Histogram<double> InferenceDuration = Meter.CreateHistogram<double>(
        "arcanum_inference_duration_seconds", "s", "Inference turn duration");

    /// <summary>Total tokens consumed. Labels: <c>provider</c>, <c>model</c>, <c>direction</c> (<c>prompt</c> | <c>completion</c>).</summary>
    public static readonly Counter<long> InferenceTokensTotal = Meter.CreateCounter<long>(
        "arcanum_inference_tokens_total", "{tokens}", "Total tokens consumed");

    /// <summary>
    /// Total reasoning tokens reported by providers. Reasoning is already included in completion
    /// tokens; this dedicated counter is observational only. Labels: <c>provider</c>, <c>model</c>.
    /// </summary>
    public static readonly Counter<long> ReasoningTokensTotal = Meter.CreateCounter<long>(
        "arcanum_inference_reasoning_tokens_total",
        "{tokens}",
        "Reasoning tokens reported by inference providers");

    /// <summary>Total tool invocations. Labels: <c>tool_name</c>, <c>outcome</c> (<c>success</c> | <c>denied</c> | <c>error</c>).</summary>
    public static readonly Counter<long> ToolInvocationsTotal = Meter.CreateCounter<long>(
        "arcanum_tool_invocations_total", description: "Total tool invocations");

    /// <summary>Current active SSE connections. Labels: <c>event_type</c> (see <c>SseEventTypes</c>).</summary>
    public static readonly UpDownCounter<long> SseConnectionsCurrent = Meter.CreateUpDownCounter<long>(
        "arcanum_sse_connections_current", description: "Current active SSE connections");

    /// <summary>Total Sanctum breaches recorded. Labels: <c>breach_type</c>.</summary>
    public static readonly Counter<long> SanctumBreachesTotal = Meter.CreateCounter<long>(
        "arcanum_sanctum_breaches_total", description: "Total sanctum breaches");

    /// <summary>
    /// Total prompt tokens served from a provider-side prompt cache (cache hit). Labels are strictly
    /// low-cardinality: <c>provider</c> (provider name) and <c>model</c> (model name). No session,
    /// request, or user identifiers are attached, so Prometheus cardinality stays bounded by the
    /// number of configured (provider, model) pairs.
    /// </summary>
    public static readonly Counter<long> PromptCacheTokensTotal = Meter.CreateCounter<long>(
        "arcanum_prompt_cache_tokens_total", "{tokens}", "Prompt tokens served from a provider-side prompt cache");

    /// <summary>
    /// Total inference turns that reported a non-zero prompt-cache hit. Labels: <c>provider</c>, <c>model</c>
    /// (low-cardinality only). Useful to compare against <c>arcanum_inference_turns_total</c>-style
    /// counts to estimate cache hit rate.
    /// </summary>
    public static readonly Counter<long> PromptCacheHitsTotal = Meter.CreateCounter<long>(
        "arcanum_prompt_cache_hits_total", description: "Inference turns that reported a non-zero prompt-cache hit");

}
