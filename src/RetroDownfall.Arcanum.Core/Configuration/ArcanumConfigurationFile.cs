using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ArcanumConfigurationFile
{

    [JsonPropertyName("Arcanum")]

    public ArcanumSettings Arcanum { get; set; } = new();

}
