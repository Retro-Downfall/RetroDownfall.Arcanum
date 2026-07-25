using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Provider-neutral reasoning effort requested for an inference turn. <see langword="null"/> on
/// <see cref="ReasoningRequestOptions.Effort"/> means the caller did not supply an effort control;
/// <see cref="None"/> is an explicit request to disable reasoning effort.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ReasoningEffortLevel>))]
public enum ReasoningEffortLevel
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("minimal")]
    Minimal,

    [JsonStringEnumMemberName("low")]
    Low,

    [JsonStringEnumMemberName("medium")]
    Medium,

    [JsonStringEnumMemberName("high")]
    High,

    [JsonStringEnumMemberName("extraHigh")]
    ExtraHigh,
}

/// <summary>
/// Controls whether provider-returned, client-safe reasoning is projected separately from the
/// assistant answer. <see langword="null"/> on <see cref="ReasoningRequestOptions.Output"/> means
/// the caller did not supply an output preference; Arcanum then derives the local projection mode
/// from the resolved model capability (full when supported, otherwise summary when supported).
/// This is an exposure preference and provider hint, not a guaranteed provider wire control.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ReasoningOutputMode>))]
public enum ReasoningOutputMode
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("summary")]
    Summary,

    [JsonStringEnumMemberName("full")]
    Full,
}

/// <summary>
/// Normalized, provider-neutral reasoning controls for one inference request. Effort and a numeric
/// budget are alternative controls and are validated as mutually exclusive. Output controls local
/// Arcanum projection and is passed through Microsoft.Extensions.AI only as a best-effort hint.
/// </summary>
public sealed record ReasoningRequestOptions(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReasoningEffortLevel? Effort = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? BudgetTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReasoningOutputMode? Output = null);

/// <summary>
/// A client-safe reasoning segment returned by a provider. It intentionally carries no provider
/// protected data and remains distinct from assistant answer text.
/// </summary>
public sealed record ReasoningContentSegment(
    string Text,
    ReasoningOutputMode Output);
