using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
/// <summary>
/// Inbound OpenAI-shaped chat message. <c>content</c> is polymorphic (string, content-part
/// array, or null) via <see cref="OpenAiMessageContent"/>'s converter.
/// </summary>
public sealed record OpenAiChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] OpenAiMessageContent? Content = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null,
    [property: JsonPropertyName("tool_calls")] OpenAiToolCall[]? ToolCalls = null,
    [property: JsonPropertyName("reasoning_content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReasoningContent = null,
    [property: JsonPropertyName("reasoning_summary")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReasoningSummary = null);
