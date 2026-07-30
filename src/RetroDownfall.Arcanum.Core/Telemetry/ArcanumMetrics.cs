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

    /// <summary>
    /// Bounded cleanup attempts after a mandatory apply_patch receipt commits. Label:
    /// <c>outcome</c> (<c>complete</c> | <c>retained</c>).
    /// </summary>
    public static readonly Counter<long> ApplyPatchArtifactCleanupTotal =
        Meter.CreateCounter<long>(
            "arcanum_apply_patch_artifact_cleanup_total",
            description: "Post-commit apply_patch recovery artifact cleanup attempts");

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
    /// Completed provider calls that reported a non-zero prompt-cache hit.
    /// </summary>
    public static readonly Counter<long> PromptCacheHitsTotal = Meter.CreateCounter<long>(
        "arcanum_prompt_cache_hits_total", description: "Provider calls that reported a non-zero prompt-cache hit");

    /// <summary>
    /// Completed provider calls classified by prompt-cache mode and eligibility. Labels are bounded
    /// to provider, model, purpose, control mode, eligibility, and a closed reason.
    /// </summary>
    public static readonly Counter<long> PromptCacheCallsTotal = Meter.CreateCounter<long>(
        "arcanum_prompt_cache_calls_total",
        "{calls}",
        "Completed inference provider calls by prompt-cache eligibility");

    /// <summary>Potential savings from the eligible prefix at configured cached-input pricing.</summary>
    public static readonly Counter<double> PromptCachePotentialSavingsUsdTotal =
        Meter.CreateCounter<double>(
            "arcanum_prompt_cache_potential_savings_usd_total",
            "USD",
            "Potential prompt-cache savings from eligible input prefixes");

    /// <summary>Realized savings from provider-reported cached input tokens.</summary>
    public static readonly Counter<double> PromptCacheActualSavingsUsdTotal =
        Meter.CreateCounter<double>(
            "arcanum_prompt_cache_actual_savings_usd_total",
            "USD",
            "Actual prompt-cache savings from provider-reported cached input tokens");

    /// <summary>
    /// Pre-call locally estimated input tokens. Labels: <c>provider</c>, <c>model</c>.
    /// </summary>
    public static readonly Histogram<long> EstimatedInputTokens = Meter.CreateHistogram<long>(
        "arcanum_estimated_input_tokens",
        "{tokens}",
        "Locally estimated input tokens before provider calls");

    /// <summary>
    /// Post-call provider-reported input tokens. Labels: <c>provider</c>, <c>model</c>.
    /// </summary>
    public static readonly Histogram<long> ProviderReportedInputTokens = Meter.CreateHistogram<long>(
        "arcanum_provider_reported_input_tokens",
        "{tokens}",
        "Provider-reported input tokens after provider calls");

    /// <summary>
    /// Absolute provider-reported versus locally estimated input-token variance. Labels:
    /// <c>provider</c>, <c>model</c>, <c>direction</c>
    /// (<c>underestimated</c> | <c>overestimated</c> | <c>exact</c> | <c>inconsistent</c>).
    /// </summary>
    public static readonly Histogram<long> InputTokenEstimationVariance = Meter.CreateHistogram<long>(
        "arcanum_input_token_estimation_variance",
        "{tokens}",
        "Absolute input-token estimation variance by direction");

    /// <summary>
    /// Calls rejected before provider I/O because accounted input plus reserves exceeded the
    /// context window. Labels: <c>provider</c>, <c>model</c>.
    /// </summary>
    public static readonly Counter<long> ContextBudgetRejectionsTotal = Meter.CreateCounter<long>(
        "arcanum_context_budget_rejections_total",
        "{rejections}",
        "Context-budget rejections before provider calls");

    /// <summary>
    /// Completed web-research provider operations. Labels: <c>provider</c>, <c>operation</c>
    /// (<c>search</c> | <c>read_url</c>), and a closed <c>outcome</c> value. Query text, URLs, and
    /// provider error text must never be attached.
    /// </summary>
    public static readonly Counter<long> WebResearchRequestsTotal = Meter.CreateCounter<long>(
        "arcanum_web_research_requests_total",
        "{requests}",
        "Completed native web-research provider operations");

    /// <summary>
    /// Web-research operation duration in seconds. Labels: <c>provider</c>, <c>operation</c>, and
    /// closed <c>outcome</c>.
    /// </summary>
    public static readonly Histogram<double> WebResearchDuration = Meter.CreateHistogram<double>(
        "arcanum_web_research_duration_seconds",
        "s",
        "Native web-research operation duration");

    /// <summary>
    /// Provider-reported web-research tokens. Labels: <c>provider</c>, <c>model</c>, and closed
    /// <c>kind</c> (<c>prompt</c>, <c>completion</c>, <c>total</c>, <c>reasoning</c>, or
    /// <c>citation</c>). Total is reported separately and must not be summed with its component
    /// kinds.
    /// </summary>
    public static readonly Counter<long> WebResearchTokensTotal = Meter.CreateCounter<long>(
        "arcanum_web_research_tokens_total",
        "{tokens}",
        "Tokens reported by native web-research providers");

    /// <summary>
    /// Search queries reported by a web-research provider. Labels: <c>provider</c> and
    /// <c>model</c>.
    /// </summary>
    public static readonly Counter<long> WebResearchSearchQueriesTotal = Meter.CreateCounter<long>(
        "arcanum_web_research_search_queries_total",
        "{queries}",
        "Search queries reported by native web-research providers");

    /// <summary>
    /// Provider-reported web-research cost. Labels: <c>provider</c> and <c>model</c>.
    /// Recorded only when the provider response includes cost.
    /// </summary>
    public static readonly Counter<double> WebResearchCostUsdTotal = Meter.CreateCounter<double>(
        "arcanum_web_research_cost_usd_total",
        "USD",
        "Cost reported by native web-research providers");

}
