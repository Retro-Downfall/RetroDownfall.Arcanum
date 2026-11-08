using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildLocalExecutionMode>))]
internal enum GuildLocalExecutionMode
{
    [JsonStringEnumMemberName("apprentice")]
    Apprentice = 0,

    [JsonStringEnumMemberName("sterile")]
    Sterile = 1,
}

internal sealed record GuildLocalExecutionFile(string Name, GuildDataRef Content);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GuildLocalExecutionInput(int Version, GuildLocalExecutionMode Mode, GuildDataRef Goal,
    string Provider, string Model, ImmutableArray<GuildLocalExecutionFile> Files, int? MaxOutputTokens);

internal sealed record GuildLocalExecutionBinding(Guid QuestId, Guid ApprenticeId, Guid RunId, Guid SessionId,
    Guid? ParentQuestId, Guid RootQuestId, Guid? ParentApprenticeId, GuildDataRef Input);
