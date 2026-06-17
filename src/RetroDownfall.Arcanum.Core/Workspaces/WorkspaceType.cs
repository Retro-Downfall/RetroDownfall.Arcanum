using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Workspaces;

[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceType>))]
public enum WorkspaceType
{

    [JsonStringEnumMemberName("spell")]
    Spell,

    [JsonStringEnumMemberName("campaign")]
    Campaign,

    [JsonStringEnumMemberName("data")]
    Data,

    [JsonStringEnumMemberName("custom")]
    Custom,

}
