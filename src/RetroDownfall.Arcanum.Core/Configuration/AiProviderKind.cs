using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<AiProviderKind>))]

public enum AiProviderKind
{

    OpenAICompatible,

}
