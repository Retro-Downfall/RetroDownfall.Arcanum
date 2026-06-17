using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Workspaces;

[JsonConverter(typeof(JsonStringEnumConverter<FileEntryType>))]
public enum FileEntryType
{

    [JsonStringEnumMemberName("file")]
    File,

    [JsonStringEnumMemberName("directory")]
    Directory,

}
