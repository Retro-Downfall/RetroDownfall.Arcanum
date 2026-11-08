using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Serialization;

/// <summary>Preserves exact numeric authority instead of accepting decimal parser rounding.</summary>
public sealed class ExactNullableDecimalJsonConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType != JsonTokenType.Number) throw new JsonException("An exact decimal number is required.");

        using JsonDocument value = JsonDocument.ParseValue(ref reader);

        return ExactDecimalText.TryParse(value.RootElement.GetRawText(), out decimal amount)
            ? amount : throw new JsonException("The decimal authority is not exactly representable.");
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is { } amount) writer.WriteNumberValue(amount);
        else writer.WriteNullValue();
    }
}
