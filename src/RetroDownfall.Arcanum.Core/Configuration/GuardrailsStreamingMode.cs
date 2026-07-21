using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Guardrails streaming output-filter mode. When <see cref="GuardrailsSettings.Enabled"/> is true and
/// this value is left at the default (<see cref="Buffered"/>), tokens are held until the output filter
/// passes. An operator who explicitly sets <see cref="Passthrough"/> is honored with a warning —
/// toxic text may reach the client before persistence is blocked.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GuardrailsStreamingMode>))]
public enum GuardrailsStreamingMode
{

    [JsonStringEnumMemberName("buffered")]
    Buffered = 0,

    [JsonStringEnumMemberName("passthrough")]
    Passthrough = 1,

}
