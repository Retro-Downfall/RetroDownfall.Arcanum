using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Daemons;

[JsonConverter(typeof(JsonStringEnumConverter<DaemonJobStatus>))]
public enum DaemonJobStatus
{

    [JsonStringEnumMemberName("pending")]
    Pending,

    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("completed")]
    Completed,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

}
