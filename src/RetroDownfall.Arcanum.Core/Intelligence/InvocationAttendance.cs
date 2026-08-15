using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<InvocationAttendance>))]
public enum InvocationAttendance : byte
{
    Attended = 1,
    Unattended = 2
}
