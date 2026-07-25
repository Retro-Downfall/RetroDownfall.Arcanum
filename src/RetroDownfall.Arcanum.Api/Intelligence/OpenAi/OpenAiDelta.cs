using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
/// <summary>
/// Per-chunk delta inside a streaming <c>choices[].delta</c>. All fields are optional because
/// most chunks carry only one (typically <c>content</c>); the first chunk often includes
/// <c>role</c>, and tool-call chunks include <c>tool_calls</c>.
/// </summary>
public sealed record OpenAiDelta(
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("role")] string? Role = null,
    [property: JsonPropertyName("tool_calls")] OpenAiStreamToolCall[]? ToolCalls = null,
    [property: JsonPropertyName("refusal")] string? Refusal = null,
    [property: JsonPropertyName("reasoning_content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReasoningContent = null,
    [property: JsonPropertyName("reasoning_summary")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReasoningSummary = null);
