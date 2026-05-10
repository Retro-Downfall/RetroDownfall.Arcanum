namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>
/// Payload surfaced by <c>POST /api/intelligence/ping</c> inside <c>ApiResponse&lt;T&gt;</c>.
/// Carries the assistant text, optional reported usage, optional tool calls, and finish reason.
/// </summary>
public sealed record PromptResponseDto(
    string Text,
    ChatCompletionUsage? Usage,
    List<PromptToolCall>? ToolCalls = null,
    string? FinishReason = null);
