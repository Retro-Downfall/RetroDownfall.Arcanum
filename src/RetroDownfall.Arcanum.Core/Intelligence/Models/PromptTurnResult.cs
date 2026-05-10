namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record PromptTurnResult(
    string Text,
    ChatCompletionUsage? Usage,
    List<PromptToolCall>? ToolCalls = null,
    string? FinishReason = null);

/// <summary>
/// One assistant-issued tool call recorded during a buffered <c>ExecutePromptAsync</c> turn,
/// used to populate OpenAI-shaped <c>choices[].message.tool_calls</c>.
/// </summary>
public sealed record PromptToolCall(string CallId, string Name, string ArgumentsJson);
