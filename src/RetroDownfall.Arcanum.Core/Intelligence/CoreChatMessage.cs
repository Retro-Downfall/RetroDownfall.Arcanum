namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// A single message in a stateless multi-turn transcript (for example, OpenAI-compatible callers
/// posting to <c>/v1/chat/completions</c> with no Grimoire thread).
/// </summary>
/// <param name="Role">
/// One of <c>system</c>, <c>user</c>, <c>assistant</c>, <c>tool</c>, or <c>developer</c>.
/// </param>
/// <param name="Content">
/// Flattened text representation (used for storage/logging). When <see cref="ContentParts"/>
/// is non-null, <see cref="Content"/> still contains the concatenated text for fallback paths.
/// </param>
/// <param name="Name">Optional message author name (OpenAI <c>name</c> field).</param>
/// <param name="ToolCallId">
/// For <c>role = "tool"</c> messages, the id of the assistant tool call this is the result for.
/// </param>
/// <param name="ToolCalls">
/// For <c>role = "assistant"</c> messages that triggered tool execution, the list of issued
/// tool calls in OpenAI shape.
/// </param>
/// <param name="ContentParts">
/// Optional structured content (multimodal). When set, the hub composes a multi-part message.
/// </param>
public sealed record CoreChatMessage(
    string Role,
    string Content,
    string? Name = null,
    string? ToolCallId = null,
    IReadOnlyList<CoreToolCall>? ToolCalls = null,
    IReadOnlyList<CoreContentPart>? ContentParts = null);

/// <summary>
/// OpenAI-shaped tool call: <c>function.name</c> + <c>function.arguments</c> (verbatim JSON string).
/// </summary>
public sealed record CoreToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// Single content part in a multimodal message. <see cref="Kind"/> is <c>"text"</c> or
/// <c>"image_url"</c>; the corresponding sibling carries the payload.
/// </summary>
public sealed record CoreContentPart(string Kind, string? Text, string? ImageUrl, string? ImageDetail);
