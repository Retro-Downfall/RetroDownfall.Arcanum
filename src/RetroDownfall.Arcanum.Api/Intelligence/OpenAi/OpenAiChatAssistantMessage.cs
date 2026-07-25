using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
/// <summary>
/// Assistant message body in a non-streaming response. <see cref="ToolCalls"/> is populated when
/// the model requested tools during the turn (Arcanum surfaces calls executed server-side as well
/// as any unconsumed final calls, so OpenAI-compatible clients see what happened).
/// </summary>
public sealed record OpenAiChatAssistantMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] OpenAiToolCall[]? ToolCalls = null,
    [property: JsonPropertyName("refusal")] string? Refusal = null,
    [property: JsonPropertyName("reasoning_content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReasoningContent = null,
    [property: JsonPropertyName("reasoning_summary")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReasoningSummary = null);
