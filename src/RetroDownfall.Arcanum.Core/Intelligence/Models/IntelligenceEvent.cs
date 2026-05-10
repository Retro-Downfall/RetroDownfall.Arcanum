namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record IntelligenceEvent(
    IntelligenceEventType Type,
    string Message,
    string? Data = null,
    ChatCompletionUsage? Usage = null,
    IntelligenceToolCallEvent? ToolCall = null);

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
