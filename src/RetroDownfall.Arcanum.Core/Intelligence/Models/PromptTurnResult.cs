using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record PromptTurnResult(
    string Text,
    ChatCompletionUsage? Usage,
    List<PromptToolCall>? ToolCalls = null,
    string? FinishReason = null)
{
    /// <summary>The canonical model on the successful inference lease, when known.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ModelUsed { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Transient operational observations, separate from structured-output warnings.</summary>
    public IReadOnlyList<InferenceNotice> Notices { get; init; } = [];

    public IReadOnlyList<ReasoningContentSegment> Reasoning { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public Core.Covenant.ProviderCallSensitivity? Sensitivity { get; init; }

    public bool PreserveProviderToolCallIds { get; init; }
}

/// <summary>
/// One assistant-issued tool call recorded during a buffered <c>ExecutePromptAsync</c> turn,
/// used to populate OpenAI-shaped <c>choices[].message.tool_calls</c>.
/// </summary>
public sealed record PromptToolCall(string CallId, string Name, string ArgumentsJson);

/// <summary>A content-free operational observation; it grants no source, execution, or network authority.</summary>
public enum InferenceNoticeCode
{
    ToolCompatibilityRestart = 1,
}

/// <summary>Only code-owned fixed copy is projected; undefined codes carry no message.</summary>
public sealed record InferenceNotice(InferenceNoticeCode Code)
{
    public string Message => Code switch
    {
        InferenceNoticeCode.ToolCompatibilityRestart => "This model does not support tools; continuing without local tools.",
        _ => string.Empty,
    };
}
