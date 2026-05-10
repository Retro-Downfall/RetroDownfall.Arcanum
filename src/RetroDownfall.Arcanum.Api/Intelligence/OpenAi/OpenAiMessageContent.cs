using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Api.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// Polymorphic wrapper for an OpenAI message <c>content</c> field. The wire shape is either a
/// plain JSON string (simple text) or an array of <see cref="OpenAiContentPart"/> (multimodal).
/// Only one of <see cref="Text"/> or <see cref="Parts"/> is populated after parsing.
/// </summary>
[JsonConverter(typeof(OpenAiMessageContentConverter))]
public sealed class OpenAiMessageContent
{
    public string? Text { get; init; }

    public OpenAiContentPart[]? Parts { get; init; }

    public static OpenAiMessageContent FromText(string? text) => new() { Text = text ?? string.Empty };

    public static OpenAiMessageContent FromParts(OpenAiContentPart[] parts) => new() { Parts = parts };
}

/// <summary>
/// AOT-safe converter for <see cref="OpenAiMessageContent"/>. Reads JSON null, JSON string,
/// or JSON array; writes the field that was populated. Array deserialization uses the
/// source-generated <see cref="ArcanumJsonContext"/> entry for <see cref="OpenAiContentPart"/>[].
/// </summary>
public sealed class OpenAiMessageContentConverter : JsonConverter<OpenAiMessageContent>
{
    public override OpenAiMessageContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return new OpenAiMessageContent { Text = reader.GetString() ?? string.Empty };

            case JsonTokenType.StartArray:
                OpenAiContentPart[]? parts = JsonSerializer.Deserialize(
                    ref reader,
                    ArcanumJsonContext.Default.OpenAiContentPartArray);

                return new OpenAiMessageContent { Parts = parts ?? [] };

            default:
                throw new JsonException(
                    $"OpenAI message 'content' must be a string, an array of content parts, or null (got {reader.TokenType}).");
        }
    }

    public override void Write(Utf8JsonWriter writer, OpenAiMessageContent value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();

            return;
        }

        if (value.Parts is { Length: > 0 } parts)
        {
            JsonSerializer.Serialize(writer, parts, ArcanumJsonContext.Default.OpenAiContentPartArray);

            return;
        }

        writer.WriteStringValue(value.Text ?? string.Empty);
    }
}
