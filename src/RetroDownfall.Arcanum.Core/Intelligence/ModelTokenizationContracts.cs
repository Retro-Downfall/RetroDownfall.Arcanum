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

    /// <summary>Hierarchical context retrieved from The Tapestry's summary trees (DESIGN §21.11).</summary>
    [JsonStringEnumMemberName("tapestryRag")]
    TapestryRag,

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

    /// <summary>
    /// Operator-authored Covenant content rendered in CONTEXT (§10.13).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SystemCodexSpell"/> because Confirmed Covenant is non-evictable
    /// within memory admission while Codex and Spell content is not, and an operator inspecting
    /// <c>mana</c> has to be able to see what the profile actually costs.
    /// </remarks>
    [JsonStringEnumMemberName("covenantConfirmed")]
    CovenantConfirmed,

    /// <summary>
    /// Agent-authored Covenant content rendered as fenced DATA (§10.13).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="LexiconSaga"/> because Proposed Covenant has the earliest eviction
    /// tier of any prompt material, so its cost and its removals are the first thing a pressure
    /// investigation needs to see.
    /// </remarks>
    [JsonStringEnumMemberName("covenantProposed")]
    CovenantProposed,
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

    /// <summary>Attachment-retrieval chunks evicted by context-window pressure this turn.</summary>
    public int DroppedAttachmentRagChunks { get; init; }

    /// <summary>Estimated attachment-retrieval tokens evicted by context-window pressure.</summary>
    public int DroppedAttachmentRagTokens { get; init; }

    /// <summary>Workspace-retrieval chunks evicted by context-window pressure this turn.</summary>
    public int DroppedWorkspaceRagChunks { get; init; }

    /// <summary>Estimated workspace-retrieval tokens evicted by context-window pressure.</summary>
    public int DroppedWorkspaceRagTokens { get; init; }

    /// <summary>Tapestry hierarchical nodes evicted by context-window pressure this turn.</summary>
    public int DroppedTapestryNodes { get; init; }

    /// <summary>Estimated Tapestry tokens evicted by context-window pressure.</summary>
    public int DroppedTapestryTokens { get; init; }

    /// <summary>Proposed Covenant entries pressured out of this attempt's admission.</summary>
    /// <remarks>
    /// Counted separately from the semantic sources rather than folded in with them. Confirmed
    /// content ranks with the operator's own Codex and is never evicted, so a single "dropped memory"
    /// total would let a reader conclude the operator's standing agreement had been trimmed when only
    /// the agent's unreviewed suggestions were.
    /// </remarks>
    public int DroppedCovenantProposed { get; init; }

    /// <summary>Estimated tokens those pressured Proposed entries would have occupied.</summary>
    public int DroppedCovenantProposedTokens { get; init; }

    /// <summary>Whether this attempt withheld the entire Covenant section for want of head-room.</summary>
    /// <remarks>
    /// Confirmed content is admitted all-or-fail, so a section that does not fit is not trimmed but
    /// dropped whole. That outcome has to be reportable on its own: an operator who sees only a
    /// Proposed trim count would read a total withholding as a mostly-honored agreement.
    /// </remarks>
    public bool CovenantConfirmedNoFit { get; init; }

    [JsonIgnore]
    public bool HasCovenantPressure => DroppedCovenantProposed > 0;

    [JsonIgnore]
    public int DroppedSemanticRagChunks =>
        SaturatingInt((long)DroppedAttachmentRagChunks + DroppedWorkspaceRagChunks + DroppedTapestryNodes);

    [JsonIgnore]
    public int DroppedSemanticRagTokens =>
        SaturatingInt((long)DroppedAttachmentRagTokens + DroppedWorkspaceRagTokens + DroppedTapestryTokens);

    [JsonIgnore]
    public bool HasSemanticRagPressure => DroppedSemanticRagChunks > 0;

    public int HistoryTokens => Source(ContextTokenSource.History).TokenCount;

    public int ExplicitAttachmentTokens =>
        Source(ContextTokenSource.ExplicitAttachments).TokenCount;

    public int RefreshedFileTokens => Source(ContextTokenSource.RefreshedFiles).TokenCount;

    public int AttachmentRagTokens => Source(ContextTokenSource.AttachmentRag).TokenCount;

    public int WorkspaceRagTokens => Source(ContextTokenSource.WorkspaceRag).TokenCount;

    public int TapestryRagTokens => Source(ContextTokenSource.TapestryRag).TokenCount;

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
/// <remarks>
/// <paramref name="SystemPromptAttribution"/> is the typed partition of the rendered system prompt
/// when one exists. Supplying it is what keeps Covenant token counts out of the heading-and-fence
/// classifier: without it the estimator would have to recognize <c>### The Covenant, Proposed</c> as
/// a heading, and any untrusted content able to write that line would be able to move its own tokens
/// into an operator-authored source (§10.13).
/// </remarks>
public sealed record ModelTokenizationRequest(
    ProviderSettings Provider,
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions Options,
    int ReservedAnswerTokens,
    int ReservedReasoningTokens,
    SystemPromptAttributionMap? SystemPromptAttribution = null);

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
