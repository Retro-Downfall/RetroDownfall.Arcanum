using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>
/// Read-only request for the effective context that would be assembled for a turn.
/// </summary>
public sealed record ContextPreviewRequest(

    string Prompt = "",

    string? Model = null,

    string WorkingDirectory = "",

    Guid? SessionId = null,

    Guid? CampaignId = null,

    bool ShowContent = false,

    bool NoRetrieval = false);

public sealed record ContextPreviewTool(

    string Name,

    string Source,

    bool Included,

    string Reason);

public sealed record ContextPreviewSource(

    ContextTokenSource Source,

    int TokenCount,

    TokenEstimateClassification Classification,

    bool Included,

    string Reason,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]

    string? Content = null);

public sealed record ContextPreviewCompression(

    bool Applied,

    int ThresholdTokens,

    string Reason);

public sealed record ContextPreviewAuxiliaryCall(

    string Purpose,

    bool Invoked,

    string? Provider,

    string? Model,

    int? Tokens,

    TokenEstimateClassification Classification,

    string Reason);

public sealed record ContextPreviewTokenSummary(

    int InputTokens,

    int TotalTokens,

    int ReservedOutputTokens,

    TokenEstimateClassification Classification,

    string ProfileId);

public sealed record ContextPreviewContent(

    string SystemPrompt,

    List<CoreChatMessage> Messages);

public sealed record ContextPreviewResult(

    string Provider,

    string Model,

    int ContextWindowLimit,

    string? SelectedSpell,

    string RoutingMode,

    string SpellReason,

    IReadOnlyList<string> ResonantDependencies,

    IReadOnlyList<ContextPreviewTool> Tools,

    IReadOnlyList<ContextPreviewSource> Sources,

    ContextPreviewCompression Compression,

    IReadOnlyList<ContextPreviewAuxiliaryCall> AuxiliaryCalls,

    ContextPreviewTokenSummary Tokens,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]

    ContextPreviewContent? Content = null);
