using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// Response for <c>POST /v1/embeddings</c> — the OpenAI embeddings list shape.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; endpoint tests cover wire serialization.
public sealed record OpenAiEmbeddingResponse(
    [property: JsonPropertyName("object")] string ObjectKind,
    [property: JsonPropertyName("data")] List<OpenAiEmbeddingData> Data,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("usage")] OpenAiEmbeddingUsage Usage);

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; endpoint tests cover wire serialization.
public sealed record OpenAiEmbeddingData(
    [property: JsonPropertyName("object")] string ObjectKind,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("embedding")] OpenAiEmbeddingVector Embedding);

/// <summary>
/// OpenAI's <c>/v1/embeddings</c> usage shape has only <c>prompt_tokens</c> and <c>total_tokens</c>
/// (no <c>completion_tokens</c> — there is no completion). Deliberately a separate type from
/// <see cref="ChatCompletionUsage"/> rather than reusing it with a hardcoded zero, so the wire shape
/// stays byte-for-byte spec compliant.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; endpoint tests cover wire serialization.
public sealed record OpenAiEmbeddingUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

/// <summary>
/// Polymorphic embedding vector: a JSON array of floats when <c>encoding_format: "float"</c>
/// (default), or a base64 string of little-endian float32 bytes when
/// <c>encoding_format: "base64"</c>. A hand-authored converter (not reflection-based) keeps this
/// Native AOT-safe — mirrors <see cref="OpenAiEmbeddingInputConverter"/> and
/// <c>OpenAiMessageContentConverter</c>.
/// </summary>
[JsonConverter(typeof(OpenAiEmbeddingVectorConverter))]
public sealed class OpenAiEmbeddingVector
{

    public float[]? Values { get; init; }

    public string? Base64 { get; init; }

    public static OpenAiEmbeddingVector FromFloats(float[] values) => new() { Values = values };

    public static OpenAiEmbeddingVector FromBase64(string base64) => new() { Base64 = base64 };

}

public sealed class OpenAiEmbeddingVectorConverter : JsonConverter<OpenAiEmbeddingVector>
{

    public override OpenAiEmbeddingVector? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {

        // Response-only type in practice (no /v1/embeddings caller round-trips this back to
        // Arcanum), but implemented for completeness/round-trip safety rather than throwing.
        switch (reader.TokenType)
        {

            case JsonTokenType.String:
                return OpenAiEmbeddingVector.FromBase64(reader.GetString() ?? string.Empty);

            case JsonTokenType.StartArray:
                using (JsonDocument document = JsonDocument.ParseValue(ref reader))
                {

                    JsonElement root = document.RootElement;

                    float[] values = new float[root.GetArrayLength()];

                    int i = 0;

                    foreach (JsonElement element in root.EnumerateArray())
                    {

                        values[i++] = element.GetSingle();

                    }

                    return OpenAiEmbeddingVector.FromFloats(values);

                }

            default:
                throw new JsonException($"'embedding' must be a base64 string or an array of numbers (got {reader.TokenType}).");

        }

    }

    public override void Write(Utf8JsonWriter writer, OpenAiEmbeddingVector value, JsonSerializerOptions options)
    {

        if (value.Base64 is { } base64)
        {

            writer.WriteStringValue(base64);

            return;

        }

        writer.WriteStartArray();

        foreach (float f in value.Values ?? [])
        {

            writer.WriteNumberValue(f);

        }

        writer.WriteEndArray();

    }

}
