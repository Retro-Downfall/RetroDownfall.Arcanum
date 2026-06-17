using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Logging;

[JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))]
public enum LogLevel
{

    [JsonStringEnumMemberName("trace")]
    Trace,

    [JsonStringEnumMemberName("debug")]
    Debug,

    [JsonStringEnumMemberName("information")]
    Information,

    [JsonStringEnumMemberName("warning")]
    Warning,

    [JsonStringEnumMemberName("error")]
    Error,

    [JsonStringEnumMemberName("critical")]
    Critical,

}
