using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// Polymorphic wrapper for the OpenAI <c>/v1/embeddings</c> request's <c>input</c> field. The wire
/// shape is one of: a JSON string, an array of strings, an array of integers (a single
/// pre-tokenized input), or an array of arrays of integers (multiple pre-tokenized inputs). Exactly
/// one of <see cref="Strings"/> or <see cref="TokenArrays"/> is populated after parsing.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; converter tests cover wire parsing.
[JsonConverter(typeof(OpenAiEmbeddingInputConverter))]
public sealed class OpenAiEmbeddingInput
{

    public string[]? Strings { get; init; }

    public int[][]? TokenArrays { get; init; }

}

/// <summary>
/// AOT-safe converter for <see cref="OpenAiEmbeddingInput"/>. Uses <see cref="JsonDocument"/> to
/// peek the shape of a JSON array (string vs. number vs. nested array) before deciding how to
/// materialize it — this is a hand-authored <see cref="JsonDocument"/> walk, not reflection-based
/// binding, so it stays Native AOT-safe (matches the same pattern as
/// <c>OpenAiMessageContentConverter</c>).
/// </summary>
public sealed class OpenAiEmbeddingInputConverter : JsonConverter<OpenAiEmbeddingInput>
{

    public override OpenAiEmbeddingInput? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {

        switch (reader.TokenType)
        {

            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return new OpenAiEmbeddingInput { Strings = [reader.GetString() ?? string.Empty] };

            case JsonTokenType.StartArray:
                using (JsonDocument document = JsonDocument.ParseValue(ref reader))
                {

                    return ReadArray(document.RootElement);

                }

            default:
                throw new JsonException(
                    $"'input' must be a string, an array of strings, an array of integers, or an array of arrays of integers (got {reader.TokenType}).");

        }

    }

    private static OpenAiEmbeddingInput ReadArray(JsonElement root)
    {

        if (root.GetArrayLength() == 0)
        {

            return new OpenAiEmbeddingInput { Strings = [] };

        }

        JsonElement first = root[0];

        switch (first.ValueKind)
        {

            case JsonValueKind.String:
                {

                    string[] strings = new string[root.GetArrayLength()];

                    int i = 0;

                    foreach (JsonElement element in root.EnumerateArray())
                    {

                        strings[i++] = element.ValueKind == JsonValueKind.String
                            ? element.GetString() ?? string.Empty
                            : throw new JsonException("'input' array elements must all be strings when the first element is a string.");

                    }

                    return new OpenAiEmbeddingInput { Strings = strings };

                }

            case JsonValueKind.Number:
                {

                    int[] ids = new int[root.GetArrayLength()];

                    int i = 0;

                    foreach (JsonElement element in root.EnumerateArray())
                    {

                        ids[i++] = element.GetInt32();

                    }

                    return new OpenAiEmbeddingInput { TokenArrays = [ids] };

                }

            case JsonValueKind.Array:
                {

                    int[][] tokenArrays = new int[root.GetArrayLength()][];

                    int i = 0;

                    foreach (JsonElement outer in root.EnumerateArray())
                    {

                        int[] inner = new int[outer.GetArrayLength()];

                        int j = 0;

                        foreach (JsonElement element in outer.EnumerateArray())
                        {

                            inner[j++] = element.GetInt32();

                        }

                        tokenArrays[i++] = inner;

                    }

                    return new OpenAiEmbeddingInput { TokenArrays = tokenArrays };

                }

            default:
                throw new JsonException(
                    $"'input' array elements must be strings, integers, or arrays of integers (got {first.ValueKind}).");

        }

    }

    public override void Write(Utf8JsonWriter writer, OpenAiEmbeddingInput value, JsonSerializerOptions options)
    {

        if (value.Strings is { } strings)
        {

            if (strings.Length == 1)
            {

                writer.WriteStringValue(strings[0]);

                return;

            }

            writer.WriteStartArray();

            foreach (string s in strings)
            {

                writer.WriteStringValue(s);

            }

            writer.WriteEndArray();

            return;

        }

        int[][] tokenArrays = value.TokenArrays ?? [];

        writer.WriteStartArray();

        foreach (int[] inner in tokenArrays)
        {

            writer.WriteStartArray();

            foreach (int id in inner)
            {

                writer.WriteNumberValue(id);

            }

            writer.WriteEndArray();

        }

        writer.WriteEndArray();

    }

}
