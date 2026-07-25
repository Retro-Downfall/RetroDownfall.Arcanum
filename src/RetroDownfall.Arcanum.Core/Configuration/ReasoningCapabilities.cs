using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Declares which explicit reasoning controls a configured model accepts.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ReasoningControlSupport>))]
public enum ReasoningControlSupport
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("effort")]
    Effort,

    [JsonStringEnumMemberName("budget")]
    Budget,

    [JsonStringEnumMemberName("effortAndBudget")]
    EffortAndBudget,
}

/// <summary>
/// Closed set of explicitly configured request-wire dialects. Arcanum never derives this value
/// from a provider or model name.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ReasoningWireDialect>))]
public enum ReasoningWireDialect
{
    /// <summary>Microsoft.Extensions.AI/OpenAI-standard reasoning options.</summary>
    [JsonStringEnumMemberName("standard")]
    Standard,

    /// <summary>OpenRouter's nested <c>reasoning.max_tokens</c> numeric-budget shape.</summary>
    [JsonStringEnumMemberName("openRouter")]
    OpenRouter,

    /// <summary>A top-level <c>reasoning_budget</c> numeric-budget field.</summary>
    [JsonStringEnumMemberName("topLevelReasoningBudget")]
    TopLevelReasoningBudget,

    /// <summary>Anthropic-style <c>thinking.budget_tokens</c> numeric-budget shape.</summary>
    [JsonStringEnumMemberName("anthropicThinking")]
    AnthropicThinking,
}

/// <summary>
/// Explicit reasoning metadata for one configured model. A <see langword="null"/> capability object
/// on <see cref="ModelEntry"/> means reasoning support has not been declared.
/// </summary>
/// <remarks>
/// Properties use <c>set</c> so the source-generated Microsoft.Extensions.Configuration binder can
/// populate nested model capabilities.
/// </remarks>
public sealed record ReasoningCapabilities
{
    public ReasoningControlSupport ControlSupport { get; set; }

    public bool SupportsSummary { get; set; }

    public bool SupportsFull { get; set; }

    public bool SupportsStreaming { get; set; }

    public bool ReportsReasoningTokens { get; set; }

    public bool AllowsClientOutput { get; set; }

    public ReasoningWireDialect WireDialect { get; set; } = ReasoningWireDialect.Standard;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxBudgetTokens { get; set; }
}
