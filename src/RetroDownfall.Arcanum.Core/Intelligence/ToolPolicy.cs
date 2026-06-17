using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence;

[JsonConverter(typeof(JsonStringEnumConverter<ToolPolicy>))]
public enum ToolPolicy
{

    [JsonStringEnumMemberName("allTools")]
    AllTools,

    [JsonStringEnumMemberName("noTools")]
    NoTools,

    [JsonStringEnumMemberName("readOnlyTools")]
    ReadOnlyTools,

    [JsonStringEnumMemberName("noForbiddenArts")]
    NoForbiddenArts,

}
