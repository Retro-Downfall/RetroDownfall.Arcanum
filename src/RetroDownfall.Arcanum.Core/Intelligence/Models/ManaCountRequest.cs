namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>
/// Body for <c>POST /api/intelligence/mana</c> — a read-only diagnostic Mana (token) counter.
/// Exactly one of <see cref="Messages"/> or <see cref="Prompt"/> should be supplied; when both are
/// present, <see cref="Messages"/> takes precedence and <see cref="Prompt"/> is ignored.
/// </summary>
/// <param name="Messages">
/// Messages to count, OpenAI-shaped content parts supported (mirrors <c>PingRequest.StatelessMessages</c>).
/// </param>
/// <param name="Prompt">Raw text to count — an alternative to <see cref="Messages"/> for a single string.</param>
/// <param name="Model">
/// Optional canonical model name. Arcanum resolves the configured provider/model tokenization
/// profile; an unconfigured name uses the documented conservative fallback.
/// </param>
/// <param name="Tools">
/// When <see langword="true"/>, include an estimate of tool-schema Mana overhead
/// (<see cref="ManaCountResult.ToolManaEstimate"/>) for the currently registered native + MCP tools.
/// </param>
public sealed record ManaCountRequest(
    List<CoreChatMessage>? Messages = null,
    string? Prompt = null,
    string? Model = null,
    bool Tools = false);
