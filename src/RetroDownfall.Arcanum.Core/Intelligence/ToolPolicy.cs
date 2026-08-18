using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Which tools a turn may advertise. Bound string-only: every consumer treats an unrecognized
/// member as the permissive arm, so an integer wire value that landed on an undefined member would
/// fail open — discarding the request's <c>disableMcpTools</c> and skipping the advertisement
/// filters. Rejecting integers at the JSON boundary keeps the only reachable values the four below.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ToolPolicy>))]
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
