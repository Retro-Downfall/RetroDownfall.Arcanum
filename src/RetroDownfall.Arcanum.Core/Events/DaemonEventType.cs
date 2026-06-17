using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Events;

/// <summary>
/// Discriminator for <see cref="DaemonEvent"/> SSE frames.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DaemonEventType>))]
public enum DaemonEventType
{

    [JsonStringEnumMemberName("started")]
    Started,

    [JsonStringEnumMemberName("completed")]
    Completed,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("intervalChanged")]
    IntervalChanged,

}
