using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildRemoteDispatchMode>))]
public enum GuildRemoteDispatchMode
{
    [JsonStringEnumMemberName("blocking")]
    Blocking = 0,

    [JsonStringEnumMemberName("continuable")]
    Continuable = 1,

    [JsonStringEnumMemberName("callback")]
    Callback = 2,
}

/// <summary>Caller preferences for a configured remote destination; no peer identity or authority is accepted here.</summary>
public sealed record GuildRemoteInvocationOptions(GuildRemoteDispatchMode Mode = GuildRemoteDispatchMode.Blocking,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<string> AcceptedOutputModes = default, string? SkillId = null);
