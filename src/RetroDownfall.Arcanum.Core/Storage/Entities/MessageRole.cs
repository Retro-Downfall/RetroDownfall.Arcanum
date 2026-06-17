using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Storage.Entities;

[JsonConverter(typeof(JsonStringEnumConverter<MessageRole>))]
public enum MessageRole
{

    [JsonStringEnumMemberName("system")]
    System,

    [JsonStringEnumMemberName("user")]
    User,

    [JsonStringEnumMemberName("assistant")]
    Assistant,

    [JsonStringEnumMemberName("tool")]
    Tool,

}
