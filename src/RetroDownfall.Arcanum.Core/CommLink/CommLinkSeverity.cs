using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.CommLink;

[JsonConverter(typeof(JsonStringEnumConverter<CommLinkSeverity>))]

public enum CommLinkSeverity
{

    Info,

    Warning,

    Critical,

}
