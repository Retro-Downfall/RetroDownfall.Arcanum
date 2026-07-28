using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>Describes the code-owned token-accounting strategy selected at runtime.</summary>
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
