using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
/// <summary>
/// OpenAI-compatible chat completion request body.
/// Field names follow OpenAI's snake_case (set explicitly via <see cref="JsonPropertyNameAttribute"/>
/// because the surrounding source-generated context uses camelCase defaults).
/// </summary>
/// <remarks>
/// <c>reasoning_output</c> is an Arcanum-local reasoning exposure preference and a
/// Microsoft.Extensions.AI best-effort hint. It is not guaranteed to become a provider wire field.
/// When omitted, Arcanum derives full-versus-summary projection from the resolved model capability.
/// </remarks>
public sealed record OpenAiChatRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("messages")] List<OpenAiChatMessage>? Messages,
    [property: JsonPropertyName("stream")] bool Stream = false,
    [property: JsonPropertyName("temperature")] float? Temperature = null,
    [property: JsonPropertyName("top_p")] float? TopP = null,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null,
    [property: JsonPropertyName("max_completion_tokens")] int? MaxCompletionTokens = null,
    [property: JsonPropertyName("presence_penalty")] float? PresencePenalty = null,
    [property: JsonPropertyName("frequency_penalty")] float? FrequencyPenalty = null,
    [property: JsonPropertyName("seed")] long? Seed = null,
    [property: JsonPropertyName("n")] int? N = null,
    [property: JsonPropertyName("user")] string? User = null,
    [property: JsonPropertyName("stop")] JsonElement? Stop = null,
    [property: JsonPropertyName("response_format")] OpenAiResponseFormat? ResponseFormat = null,
    [property: JsonPropertyName("stream_options")] OpenAiStreamOptions? StreamOptions = null,
    [property: JsonPropertyName("tools")] OpenAiToolDefinition[]? Tools = null,
    [property: JsonPropertyName("tool_choice")] JsonElement? ToolChoice = null,
    [property: JsonPropertyName("parallel_tool_calls")] bool? ParallelToolCalls = null,
    [property: JsonPropertyName("logprobs")] bool? Logprobs = null,
    [property: JsonPropertyName("top_logprobs")] int? TopLogprobs = null,
    [property: JsonPropertyName("reasoning_effort"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OpenAiReasoningEffort? ReasoningEffort = null,
    [property: JsonPropertyName("reasoning_budget"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ReasoningBudget = null,
    [property: JsonPropertyName("reasoning_output"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReasoningOutputMode? ReasoningOutput = null);

public sealed record OpenAiStreamOptions(
    [property: JsonPropertyName("include_usage")] bool? IncludeUsage = null);

public sealed record OpenAiResponseFormat(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("json_schema")] JsonElement? JsonSchema = null);
