using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// Polymorphic wrapper for the OpenAI <c>/v1/moderations</c> request's <c>input</c> field. The wire
/// shape is either a single JSON string or an array of strings — always materialized into
/// <see cref="Values"/> in wire order (a lone string becomes a one-element array) so the handler
/// produces exactly one <c>OpenAiModerationResult</c> per input item, matching OpenAI's contract.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; converter tests cover wire parsing.
[JsonConverter(typeof(OpenAiModerationInputConverter))]
public sealed class OpenAiModerationInput
{

    public required string[] Values { get; init; }

}

/// <summary>
/// AOT-safe converter for <see cref="OpenAiModerationInput"/> — hand-authored <see cref="Utf8JsonReader"/>
/// walk, not reflection-based binding (matches <c>OpenAiEmbeddingInputConverter</c>).
/// </summary>
public sealed class OpenAiModerationInputConverter : JsonConverter<OpenAiModerationInput>
{

    public override OpenAiModerationInput? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {

        switch (reader.TokenType)
        {

            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return new OpenAiModerationInput { Values = [reader.GetString() ?? string.Empty] };

            case JsonTokenType.StartArray:
                using (JsonDocument document = JsonDocument.ParseValue(ref reader))
                {

                    JsonElement root = document.RootElement;

                    string[] values = new string[root.GetArrayLength()];

                    int i = 0;

                    foreach (JsonElement element in root.EnumerateArray())
                    {

                        values[i++] = element.ValueKind == JsonValueKind.String
                            ? element.GetString() ?? string.Empty
                            : throw new JsonException("'input' array elements must all be strings.");

                    }

                    return new OpenAiModerationInput { Values = values };

                }

            default:
                throw new JsonException($"'input' must be a string or an array of strings (got {reader.TokenType}).");

        }

    }

    public override void Write(Utf8JsonWriter writer, OpenAiModerationInput value, JsonSerializerOptions options)
    {

        if (value.Values.Length == 1)
        {

            writer.WriteStringValue(value.Values[0]);

            return;

        }

        writer.WriteStartArray();

        foreach (string s in value.Values)
        {

            writer.WriteStringValue(s);

        }

        writer.WriteEndArray();

    }

}
