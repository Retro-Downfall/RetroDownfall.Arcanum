using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

[JsonConverter(typeof(JsonStringEnumConverter<SpellSource>))]
public enum SpellSource
{

    [JsonStringEnumMemberName("builtin")]
    Builtin,

    [JsonStringEnumMemberName("workspace")]
    Workspace,

    [JsonStringEnumMemberName("campaign")]
    Campaign,

}
