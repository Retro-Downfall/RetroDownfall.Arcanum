using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantContextPolicy>))]
public enum CovenantContextPolicy : byte
{
    Default = 1,
    None = 2
}
