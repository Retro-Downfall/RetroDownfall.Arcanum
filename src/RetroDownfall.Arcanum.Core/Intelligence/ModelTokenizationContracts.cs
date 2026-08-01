using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>How trustworthy a token value is and where it came from.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<TokenEstimateClassification>))]
public enum TokenEstimateClassification
{
    [JsonStringEnumMemberName("exact")]
    Exact,

    [JsonStringEnumMemberName("estimated")]
    Estimated,

    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("reserved")]
    Reserved,

    [JsonStringEnumMemberName("providerReported")]
    ProviderReported,
}

/// <summary>Stable source categories used by admission, diagnostics, and telemetry.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ContextTokenSource>))]
public enum ContextTokenSource
{
    [JsonStringEnumMemberName("history")]
    History,

    [JsonStringEnumMemberName("systemCodexSpell")]
    SystemCodexSpell,

    [JsonStringEnumMemberName("tools")]
    Tools,

    [JsonStringEnumMemberName("lexiconSaga")]
    LexiconSaga,

    [JsonStringEnumMemberName("workspaceRag")]
    WorkspaceRag,

    [JsonStringEnumMemberName("attachmentRag")]
    AttachmentRag,

    [JsonStringEnumMemberName("explicitAttachments")]
    ExplicitAttachments,

    [JsonStringEnumMemberName("refreshedFiles")]
    RefreshedFiles,

    [JsonStringEnumMemberName("currentPrompt")]
    CurrentPrompt,

    [JsonStringEnumMemberName("structuredOutput")]
    StructuredOutput,

    [JsonStringEnumMemberName("providerFraming")]
    ProviderFraming,

    [JsonStringEnumMemberName("safetyMargin")]
    SafetyMargin,

    [JsonStringEnumMemberName("reservedAnswer")]
    ReservedAnswer,

    [JsonStringEnumMemberName("reservedReasoning")]
    ReservedReasoning,
}

/// <summary>A single classified token value.</summary>
public sealed record TokenEstimate(
    int TokenCount,
    TokenEstimateClassification Classification,
    string ProfileId,
    double Confidence = 1d,
    int SafetyMarginTokens = 0);

/// <summary>The fully resolved, immutable profile used for one estimate.</summary>
public sealed record ResolvedModelTokenizationProfile
{
    public required string ProfileId { get; init; }

    public required ModelTokenizationProfileType Type { get; init; }

    public required string TokenizerId { get; init; }

    public required int SafetyMarginPercent { get; init; }

    public required int PerMessageOverheadTokens { get; init; }

    public required int PerToolOverheadTokens { get; init; }

    public required int ProviderFramingTokens { get; init; }

    public required int StopTokenOverheadTokens { get; init; }

    public required int UnknownImageReserveTokens { get; init; }

    public required double Confidence { get; init; }
}

/// <summary>One source row in a <see cref="ContextTokenBreakdown"/>.</summary>
public sealed record ContextTokenComponent(
    ContextTokenSource Source,
    TokenEstimate Estimate);

/// <summary>
/// Reusable pre-call context accounting. <see cref="InputTokens"/> remains the original local
/// estimate after reconciliation; provider-reported usage is retained separately as the post-call
/// authority.
/// </summary>
public sealed record ContextTokenBreakdown
{
    public required string Provider { get; init; }

    public required string Model { get; init; }

    public required ResolvedModelTokenizationProfile Profile { get; init; }

    public required IReadOnlyList<ContextTokenComponent> Components { get; init; }

    public IReadOnlyList<int> MessageTokenCounts { get; init; } = [];

    public required int InputTokens { get; init; }

    public required int ReservedTokens { get; init; }

    public int ReservedAnswerTokens { get; init; }

    public int ReservedReasoningTokens { get; init; }

    public required int TotalTokens { get; init; }

    public required TokenEstimateClassification OverallClassification { get; init; }

    public required int SafetyMarginTokens { get; init; }

    [JsonIgnore]
    public string PayloadFingerprint { get; init; } = string.Empty;

    public long? ProviderReportedInputTokens { get; init; }

    public long? EstimationVarianceTokens { get; init; }

    public bool? ProviderReportedInputValid { get; init; }

    public int HistoryTokens => Source(ContextTokenSource.History).TokenCount;

    public int ExplicitAttachmentTokens =>
        Source(ContextTokenSource.ExplicitAttachments).TokenCount;

    public int RefreshedFileTokens => Source(ContextTokenSource.RefreshedFiles).TokenCount;

    public int AttachmentRagTokens => Source(ContextTokenSource.AttachmentRag).TokenCount;

    public int WorkspaceRagTokens => Source(ContextTokenSource.WorkspaceRag).TokenCount;

    public TokenEstimate Source(ContextTokenSource source)
    {
        for (int i = 0; i < Components.Count; i++)
        {
            if (Components[i].Source == source)
            {
                return Components[i].Estimate;
            }
        }

        return new TokenEstimate(
            0,
            source is ContextTokenSource.ReservedAnswer or ContextTokenSource.ReservedReasoning
                ? TokenEstimateClassification.Reserved
                : TokenEstimateClassification.Exact,
            Profile.ProfileId,
            Profile.Confidence);
    }

    public ContextTokenBreakdown ReconcileProviderReportedInput(long? providerReportedInputTokens)
    {
        if (providerReportedInputTokens is null)
        {
            return this;
        }

        long reported = providerReportedInputTokens.Value;
        long variance = long.CreateSaturating((Int128)reported - InputTokens);
        bool valid = reported >= 0;
        if (ProviderReportedInputTokens == reported
            && EstimationVarianceTokens == variance
            && ProviderReportedInputValid == valid)
        {
            return this;
        }

        return this with
        {
            ProviderReportedInputTokens = reported,
            EstimationVarianceTokens = variance,
            ProviderReportedInputValid = valid,
        };
    }

    public static int SaturatingInt(long value) =>
        int.CreateSaturating(Math.Max(0L, value));
}

/// <summary>All material required to estimate one provider call.</summary>
public sealed record ModelTokenizationRequest(
    ProviderSettings Provider,
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions Options,
    int ReservedAnswerTokens,
    int ReservedReasoningTokens);

/// <summary>Provider/model admission settings supplied to <see cref="IModelCallExecutor"/>.</summary>
public sealed record ModelCallContext(
    ProviderSettings Provider,
    string Model,
    int ReservedAnswerTokens,
    int ReservedReasoningTokens,
    ContextTokenBreakdown? PrecomputedBreakdown = null,
    PromptCachePlan? PromptCachePlan = null);

/// <summary>Resolves profiles and estimates provider-facing context without performing model I/O.</summary>
public interface IModelTokenEstimator
{
    ResolvedModelTokenizationProfile ResolveProfile(ProviderSettings provider, string canonicalModel);

    ResolvedModelTokenizationProfile ResolveEffectiveProfile(
        ProviderSettings provider,
        string canonicalModel);

    TokenEstimate EstimateText(ProviderSettings provider, string canonicalModel, string? text);

    ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request);
}
