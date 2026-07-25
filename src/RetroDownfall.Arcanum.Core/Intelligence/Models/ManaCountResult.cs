using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>
/// Payload surfaced by <c>POST /api/intelligence/mana</c> inside <c>ApiResponse&lt;T&gt;</c>.
/// Mana is Arcanum's name for the token budget an inference turn consumes (see the CLI Mana bar
/// and model-aware context estimator) — this endpoint reports it ahead of time, without
/// spending any.
/// </summary>
/// <param name="ManaCount">Total Mana (tokens) for the request (messages or prompt).</param>
/// <param name="Encoding">The tokenizer encoding actually used (for example <c>o200k_base</c>).</param>
/// <param name="PerMessage">
/// Per-message content + message-framing counts, in request order. Call-wide tool schemas,
/// structured output, provider framing, and safety margin remain separate breakdown rows.
/// </param>
/// <param name="ToolManaEstimate">
/// Tool-schema Mana included when the request set <c>tools: true</c>.
/// </param>
public sealed record ManaCountResult(
    int ManaCount,
    string Encoding,
    List<int>? PerMessage = null,
    int? ToolManaEstimate = null,
    TokenEstimateClassification Classification = TokenEstimateClassification.Estimated,
    string? ProfileId = null,
    int SafetyMarginTokens = 0,
    ContextTokenBreakdown? Breakdown = null);
