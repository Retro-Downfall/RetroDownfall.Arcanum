namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>
/// Payload surfaced by <c>POST /api/intelligence/mana</c> inside <c>ApiResponse&lt;T&gt;</c>.
/// Mana is Arcanum's name for the token budget an inference turn consumes (see the CLI Mana bar
/// and <c>ManaMeter</c>/<c>ManaPreflight</c>) — this endpoint reports it ahead of time, without
/// spending any.
/// </summary>
/// <param name="ManaCount">Total Mana (tokens) for the request (messages or prompt).</param>
/// <param name="Encoding">The tokenizer encoding actually used (for example <c>o200k_base</c>).</param>
/// <param name="PerMessage">
/// Per-message Mana counts, in the same order as the request's <c>messages</c>, when messages
/// (rather than a raw prompt) were supplied.
/// </param>
/// <param name="ToolManaEstimate">
/// Approximate tool-schema Mana overhead when the request set <c>tools: true</c>. This is an
/// approximation — see <c>ToolSchemaManaEstimator</c> for method.
/// </param>
public sealed record ManaCountResult(
    int ManaCount,
    string Encoding,
    List<int>? PerMessage = null,
    int? ToolManaEstimate = null);
