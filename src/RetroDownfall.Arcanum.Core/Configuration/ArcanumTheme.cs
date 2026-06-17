using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<ArcanumTheme>))]

public enum ArcanumTheme
{

    Light,

    Dark,

    SystemDefault,

}
