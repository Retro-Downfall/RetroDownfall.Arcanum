using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Selects how Arcanum accounts for a provider/model's input context before a model call.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ModelTokenizationProfileType>))]
public enum ModelTokenizationProfileType
{
    [JsonStringEnumMemberName("unknownFallback")]
    UnknownFallback,

    [JsonStringEnumMemberName("exactLocalTokenizer")]
    ExactLocalTokenizer,

    [JsonStringEnumMemberName("providerTokenizerApi")]
    ProviderTokenizerApi,

    [JsonStringEnumMemberName("calibratedApproximation")]
    CalibratedApproximation,
}

/// <summary>
/// Optional typed token-accounting override. A model-level profile overrides its provider profile;
/// when both are absent Arcanum resolves a built-in model profile or a conservative fallback.
/// </summary>
/// <remarks>
/// Nullable numeric values inherit the corresponding safe global default. Exact local tokenizers
/// never apply an estimation safety margin.
/// </remarks>
public sealed record ModelTokenizationProfile
{
    public ModelTokenizationProfileType Type { get; set; } = ModelTokenizationProfileType.UnknownFallback;

    /// <summary>Tokenizer/estimator identifier, such as <c>o200k_base</c>.</summary>
    public string? TokenizerId { get; set; }

    /// <summary>Percentage added to estimated textual input. Ignored by exact profiles.</summary>
    public int? SafetyMarginPercent { get; set; }

    /// <summary>Provider chat-template framing added for every message.</summary>
    public int? PerMessageOverheadTokens { get; set; }

    /// <summary>Provider function/tool framing added for every declared tool.</summary>
    public int? PerToolOverheadTokens { get; set; }

    /// <summary>Provider-level priming/framing tokens added once per call.</summary>
    public int? ProviderFramingTokens { get; set; }

    /// <summary>Known stop/end-marker overhead added once per call.</summary>
    public int? StopTokenOverheadTokens { get; set; }

    /// <summary>
    /// Conservative reserve for an image whose provider-specific token formula cannot be applied.
    /// This is never reported as an exact image-token count.
    /// </summary>
    public int? UnknownImageReserveTokens { get; set; }

    /// <summary>Optional confidence in the calibrated estimate, from 0 through 1.</summary>
    public double? Confidence { get; set; }
}
