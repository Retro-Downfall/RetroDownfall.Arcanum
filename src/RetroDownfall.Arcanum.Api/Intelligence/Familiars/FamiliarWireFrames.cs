using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.Familiars;

/// <summary>
/// The NDJSON a Familiar writes to stdout.
/// </summary>
/// <remarks>
/// These are external CLI wire types, not Arcanum <c>/api</c> payloads, so — like the OpenAI
/// <c>/v1</c> and MCP JSON-RPC types — they carry vendor-chosen names rather than Arcanum's. The
/// names are snake_case throughout, which the context's naming policy handles, so no property needs
/// a <c>[JsonPropertyName]</c> of its own. Every member is optional: a frame set is heterogeneous
/// and versions independently of Arcanum, so an unknown or absent field must degrade rather than
/// throw. Binding goes through <see cref="FamiliarWireJsonContext"/> — no reflection-based
/// serializer overload, so the AOT gate stays clean.
/// </remarks>
internal sealed record ClaudeCodeFrame
{

    public string? Type { get; init; }

    public string? Subtype { get; init; }

    public ClaudeCodeStreamEvent? Event { get; init; }

    public ClaudeCodeMessage? Message { get; init; }

    /// <summary>Present on the terminal <c>result</c> frame; the complete answer as one string.</summary>
    public string? Result { get; init; }

    /// <summary>
    /// The terminal frame reports failure here, and keeps <c>subtype: "success"</c> even for an API
    /// error — so this, not the subtype, is the field that decides whether the turn succeeded.
    /// </summary>
    public bool IsError { get; init; }

    public ClaudeCodeUsage? Usage { get; init; }

    public double? TotalCostUsd { get; init; }

}

internal sealed record ClaudeCodeStreamEvent
{

    public string? Type { get; init; }

    public ClaudeCodeDelta? Delta { get; init; }

}

internal sealed record ClaudeCodeDelta
{

    public string? Type { get; init; }

    public string? Text { get; init; }

    public string? Thinking { get; init; }

}

internal sealed record ClaudeCodeMessage
{

    public IReadOnlyList<ClaudeCodeContentBlock>? Content { get; init; }

    public ClaudeCodeUsage? Usage { get; init; }

    public string? StopReason { get; init; }

}

internal sealed record ClaudeCodeContentBlock
{

    public string? Type { get; init; }

    public string? Text { get; init; }

    public string? Thinking { get; init; }

}

internal sealed record ClaudeCodeUsage
{

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public long? CacheReadInputTokens { get; init; }

    public long? CacheCreationInputTokens { get; init; }

}

internal sealed record CodexFrame
{

    public string? Type { get; init; }

    public CodexItem? Item { get; init; }

    public CodexUsage? Usage { get; init; }

    public CodexError? Error { get; init; }

    /// <summary>Set on a bare <c>{"type":"error","message":"…"}</c> frame.</summary>
    public string? Message { get; init; }

}

internal sealed record CodexItem
{

    public string? Id { get; init; }

    public string? Type { get; init; }

    public string? Text { get; init; }

    public string? Message { get; init; }

}

internal sealed record CodexUsage
{

    public long? InputTokens { get; init; }

    public long? CachedInputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public long? ReasoningOutputTokens { get; init; }

}

internal sealed record CodexError
{

    public string? Message { get; init; }

}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ClaudeCodeFrame))]
[JsonSerializable(typeof(CodexFrame))]
internal partial class FamiliarWireJsonContext : JsonSerializerContext;
