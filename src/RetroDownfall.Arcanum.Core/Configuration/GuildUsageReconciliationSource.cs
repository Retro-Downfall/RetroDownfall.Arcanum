using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>Explicit provider metadata source for new Guild physical calls.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildUsageReconciliationSource>))]
public enum GuildUsageReconciliationSource
{
    None,

    OpenRouterGeneration,
}
