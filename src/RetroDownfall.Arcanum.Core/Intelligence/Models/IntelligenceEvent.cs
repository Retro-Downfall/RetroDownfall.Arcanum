using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record IntelligenceEvent(
    IntelligenceEventType Type,
    string Message,
    string? Data = null,
    ChatCompletionUsage? Usage = null,
    IntelligenceToolCallEvent? ToolCall = null,
    [property: JsonPropertyName("wardId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WardId = null,
    [property: JsonPropertyName("toolName")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WardToolName = null,
    [property: JsonPropertyName("arguments")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? WardArguments = null,
    [property: JsonPropertyName("allowed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? WardAllowed = null,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WardReason = null,
    [property: JsonPropertyName("timestamp")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? Timestamp = null);

/// <summary>
/// Structured payload for <see cref="IntelligenceEventType.ToolCall"/> and
/// <see cref="IntelligenceEventType.ToolResult"/> frames. Lets OpenAI-compatible bridges
/// emit <c>delta.tool_calls</c> chunks with the same id used to correlate the result, while
/// the legacy <c>Message</c> + <c>Data</c> fields stay populated for human-readable transcripts.
/// </summary>
public sealed record IntelligenceToolCallEvent(
    string CallId,
    string Name,
    string ArgumentsJson,
    int Index = 0);
