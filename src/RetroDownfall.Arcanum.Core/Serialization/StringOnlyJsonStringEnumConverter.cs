using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Serialization;

/// <summary>
/// AOT-safe closed-generic enum converter that preserves explicit wire names and rejects integers.
/// </summary>
public sealed class StringOnlyJsonStringEnumConverter<TEnum>
    : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public StringOnlyJsonStringEnumConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}
