using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Which tools a turn may advertise. Guarded twice over, because an unrecognized member must never
/// be read as permission. Binding is string-only, so an integer wire value can no longer land on an
/// undefined member and the only values reachable from the wire are the four below; and every
/// consumer independently resolves an unrecognized member to the restrictive arm rather than the
/// permissive one — <c>ShouldDisableMcpTools</c> disables MCP tools, <c>ApplyToolPolicyFilters</c>
/// advertises nothing, and <c>ArcanumInvocationContexts.ResolveToolPolicy</c> reports
/// <see cref="NoTools"/>. The closed default covers in-process construction, which the JSON boundary
/// cannot reach.
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
