using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Retained per-model reasoning facts declared under a model entry's <c>reasoning</c> block.
/// Only the wire dialect and an optional budget ceiling remain configurable; every other
/// capability (control support, client-output flags, streaming, usage reporting) is derived by
/// Arcanum from this declaration, so removed facts are rejected at validation time.
/// </summary>
/// <remarks>
/// Properties use <c>set</c> (not <c>init</c>) so the configuration binding source generator can
/// assign them — <c>init</c>-only properties are silently skipped (dotnet/runtime#107856).
/// </remarks>
public sealed record ModelReasoningSettings
{

    /// <summary>
    /// Optional wire-shape hint for nonstandard reasoning budgets. <see langword="null"/> means the
    /// standard OpenAI-compatible wire dialect is sufficient; set only for third-party endpoints
    /// that require <c>openRouter</c>, <c>topLevelReasoningBudget</c>, or <c>anthropicThinking</c>.
    /// The adapter enforces this at request time: a numeric budget requires a nonstandard dialect,
    /// and standard rejects numeric budgets.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReasoningWireDialect? WireDialect { get; set; }

    /// <summary>
    /// Optional per-single-turn reasoning budget ceiling (1–2,097,152 tokens). Only meaningful
    /// alongside a nonstandard wire dialect.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxBudgetTokens { get; set; }

}
