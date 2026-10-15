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
    /// Ward outcomes by who supplied them. Labels are strictly <c>tool_name</c> and <c>origin</c>
    /// (<c>human</c> | <c>auto_approved</c> | <c>auto_denied</c> | <c>timed_out</c> |
    /// <c>cancelled</c> | <c>host_restarted</c> | <c>ungated</c>). Tool arguments, paths, prompt
    /// content, and free-text resolution reasons are never recorded as labels.
    /// </summary>
    public static readonly Counter<long> WardDecisionsTotal = Meter.CreateCounter<long>(
        "arcanum_ward_decisions_total", description: "Total ward decisions by resolution origin");

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
    /// Startup and manual durable-operation recovery outcomes. Labels are bounded to the registered
    /// operation <c>kind</c> and a closed <c>outcome</c> vocabulary.
    /// </summary>
    public static readonly Counter<long> OperationReconciliationTotal = Meter.CreateCounter<long>(
        "arcanum_operation_reconciliation_total",
        "{operations}",
        "Durable operation reconciliation outcomes");

    /// <summary>
    /// Provider-reported web-research cost. Labels: <c>provider</c> and <c>model</c>.
    /// Recorded only when the provider response includes cost.
    /// </summary>
    public static readonly Counter<double> WebResearchCostUsdTotal = Meter.CreateCounter<double>(
        "arcanum_web_research_cost_usd_total",
        "USD",
        "Cost reported by native web-research providers");

    /// <summary>
    /// Outbound A2A Sendings that settled. Labels: closed <c>outcome</c> (<c>completed</c>,
    /// <c>failed</c>, <c>continuation</c>) and closed <c>cost</c> (<c>known</c>, <c>unknown</c>).
    /// </summary>
    /// <remarks>
    /// The <c>cost=unknown</c> series is the point: delegated work whose price nobody reported is
    /// visible as unpriced rather than folded into the totals as zero (issue #60). Peer identity is
    /// deliberately not a label — allowlist entries can name private partner endpoints.
    /// </remarks>
    public static readonly Counter<long> ConclaveSendingsTotal = Meter.CreateCounter<long>(
        "arcanum_conclave_sendings_total",
        "{sendings}",
        "Outbound A2A Sendings by outcome and whether the peer reported cost");

    /// <summary>
    /// Tokens a remote A2A peer reported for delegated work. Recorded only when the peer reports them,
    /// so this counter never implies that unreported Sendings were free.
    /// </summary>
    public static readonly Counter<long> ConclaveSendingRemoteTokensTotal = Meter.CreateCounter<long>(
        "arcanum_conclave_sending_remote_tokens_total",
        "{tokens}",
        "Tokens reported by remote A2A agents for delegated Sendings");

    /// <summary>Cost a remote A2A peer reported for delegated work, when it reports any.</summary>
    public static readonly Counter<double> ConclaveSendingRemoteCostUsdTotal = Meter.CreateCounter<double>(
        "arcanum_conclave_sending_remote_cost_usd_total",
        "USD",
        "Cost reported by remote A2A agents for delegated Sendings");

    /// <summary>Remote wall-clock of a settled outbound Sending. Label: closed <c>outcome</c>.</summary>
    public static readonly Histogram<double> ConclaveSendingDuration = Meter.CreateHistogram<double>(
        "arcanum_conclave_sending_duration_seconds",
        "s",
        "Wall-clock duration of outbound A2A Sendings measured from dispatch to settle");

    /// <summary>
    /// Long Rest sweeps parked by the owning worker or a fresh Session request. Label: <c>class</c>, the fault class of the code
    /// the park was stamped with (<c>resume_later</c>, <c>restart_generation</c> or <c>needs_operator</c>). A restart-generation
    /// park of a clean sweep is recorded as it is retired. Never carries a code, an identity or any content.
    /// </summary>
    public static readonly Counter<long> LongRestSweepParkedTotal = Meter.CreateCounter<long>(
        "arcanum.long_rest.sweep.parked",
        "{sweeps}",
        "Long Rest sweeps parked, by fault class");

    /// <summary>Parked Long Rest sweeps readmitted to Running by their own authority class (the worker for clean, a fresh capability for protected).</summary>
    public static readonly Counter<long> LongRestSweepReadmittedTotal = Meter.CreateCounter<long>(
        "arcanum.long_rest.sweep.readmitted",
        "{sweeps}",
        "Parked Long Rest sweeps readmitted");

    /// <summary>
    /// Nominated neighbours the group packer left out of an anchor's adjudication packs. Label: closed <c>reason</c>
    /// (<c>oversize</c>: the neighbour cannot fit a request with its anchor within the recipe's input budget; <c>unpaired</c>: the Pairing Gate
    /// did not pair it with the anchor, because it is below the anchor's Background Floor or has no vector to judge; <c>incohesive</c>: it is paired
    /// with the anchor but not with a member of every pack that had room; <c>pack_limit</c>: the anchor already has its three clique packs).
    /// Never carries an identity or any content.
    /// </summary>
    public static readonly Counter<long> LongRestPackSkippedCandidatesTotal = Meter.CreateCounter<long>(
        "arcanum.long_rest.sweep.skipped_candidates",
        "{candidates}",
        "Long Rest neighbours the group packer skipped, by reason");

    /// <summary>
    /// The Background Floor, in thousandths of cosine, of each anchor that had at least one nominated neighbour: the cosine a nominee needed
    /// to be paired with it. Fixed buckets from 500 to 1000; a distribution that sits at the top says the embedding model does not separate
    /// topics in this installation's memories. Never carries an identity or any content.
    /// </summary>
    public static readonly Histogram<int> LongRestPairingFloor = Meter.CreateHistogram<int>(
        "arcanum.long_rest.discovery.pairing_floor",
        "{permille}",
        "Long Rest Background Floor of anchors with nominated neighbours, in thousandths of cosine");

    /// <summary>
    /// Hierarchy children too large for any summary request of the recipe's input budget, carried unsummarized instead of folded. Counted
    /// once, when the child first reaches a layer's plan, and only after the plan's cursor is durable. Never carries an identity or any content.
    /// </summary>
    public static readonly Counter<long> LongRestHierarchyOversizeChildrenTotal = Meter.CreateCounter<long>(
        "arcanum.long_rest.hierarchy.oversize_children",
        "{children}",
        "Long Rest hierarchy children carried unsummarized because no request can hold them");

    /// <summary>
    /// Adjudication groups the sweep did not queue because the identical set of memories was already decided NoChange in the same generation,
    /// so no request is made for it. Never carries an identity or any content.
    /// </summary>
    public static readonly Counter<long> LongRestReusedDecisionsTotal = Meter.CreateCounter<long>(
        "arcanum.long_rest.sweep.reused_decisions",
        "{decisions}",
        "Long Rest adjudications reused from an identical decided set in the same generation");

    /// <summary>
    /// Leaves the lexical discovery lane fully verified (source row, snapshot, history head, vector and digest) because a lexical hit
    /// named them, counted once per anchor scan. Lazy verification in score order keeps this at the eight eligible neighbours plus
    /// the ineligible hits that outranked them, whatever the corpus size. Never carries an identity or any content.
    /// </summary>
    public static readonly Counter<long> LongRestLexicalVerificationsTotal = Meter.CreateCounter<long>(
        "arcanum.long_rest.discovery.lexical_verifications",
        "{leaves}",
        "Leaves the Long Rest lexical lane verified in full");

    /// <summary>Resumable Long Rest faults that spent the last of the Sweep Failure Budget and became needs-operator parks.</summary>
    public static readonly Counter<long> LongRestSweepBudgetExhaustedTotal = Meter.CreateCounter<long>(
        "arcanum.long_rest.sweep.budget_exhausted",
        "{sweeps}",
        "Long Rest sweeps whose failure budget was spent");

    /// <summary>
    /// Paid Long Rest summaries the embedding provider's context window refused at their embedding (the last guard of Embedding-Bounded Summary).
    /// The summary request states the recipe's summary bound (its output cap stays the fixed answer reservation), so a refusal here means the model ignored the bound.
    /// Never carries an identity or any content.
    /// </summary>
    public static readonly Counter<long> LongRestEmbeddingWindowRefusedTotal = Meter.CreateCounter<long>(
        "arcanum.long_rest.summary.embedding_window_refused",
        "{summaries}",
        "Paid Long Rest summaries refused at their embedding because they exceed the embedding window");

    /// <summary>
    /// Session turns whose clean Long Rest scheduling was refused with a Long Rest error and which proceeded anyway. Label:
    /// <c>code</c>, the closed <c>LongRest.*</c> error code. Never carries an identity or any content.
    /// </summary>
    public static readonly Counter<long> LongRestCleanScheduleRefusedTotal = Meter.CreateCounter<long>(
        "arcanum.long_rest.clean_schedule_refused",
        "{turns}",
        "Session turns that proceeded after clean Long Rest scheduling was refused");
}
