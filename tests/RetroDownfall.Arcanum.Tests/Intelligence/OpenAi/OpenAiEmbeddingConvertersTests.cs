using System.Text.Json;

using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

using RetroDownfall.Arcanum.Api.Serialization;

namespace RetroDownfall.Arcanum.Tests.Intelligence.OpenAi;

/// <summary>
/// Pure-logic coverage for the hand-authored <see cref="OpenAiEmbeddingInputConverter"/> and
/// <see cref="OpenAiEmbeddingVectorConverter"/> — every OpenAI <c>input</c>/<c>embedding</c> wire
/// shape, parsed via the same source-generated <see cref="ArcanumJsonContext"/> the production
/// endpoint uses (not reflection-based binding), without going through the HTTP pipeline.
/// HTTP-level behavior is covered by <c>OpenAiV1EmbeddingsEndpointTests</c>.
/// </summary>
public sealed class OpenAiEmbeddingConvertersTests
{

    [Fact]
    public void Input_PlainString_ParsesAsSingleElementStringsArray()
    {

        OpenAiEmbeddingInput? input = JsonSerializer.Deserialize("\"hello world\"", ArcanumJsonContext.Default.OpenAiEmbeddingInput);

        Assert.NotNull(input);

        Assert.NotNull(input.Strings);

        Assert.Equal(["hello world"], input.Strings);

        Assert.Null(input.TokenArrays);

    }

    [Fact]
    public void Input_ArrayOfStrings_ParsesAsStringsArray()
    {

        OpenAiEmbeddingInput? input = JsonSerializer.Deserialize("""["a","b","c"]""", ArcanumJsonContext.Default.OpenAiEmbeddingInput);

        Assert.NotNull(input);

        Assert.NotNull(input.Strings);

        Assert.Equal(["a", "b", "c"], input.Strings);

    }

    [Fact]
    public void Input_ArrayOfIntegers_ParsesAsSingleTokenArray()
    {

        OpenAiEmbeddingInput? input = JsonSerializer.Deserialize("[1,2,3]", ArcanumJsonContext.Default.OpenAiEmbeddingInput);

        Assert.NotNull(input);

        Assert.Null(input.Strings);

        Assert.NotNull(input.TokenArrays);

        Assert.Single(input.TokenArrays);

        Assert.Equal([1, 2, 3], input.TokenArrays[0]);

    }

    [Fact]
    public void Input_ArrayOfArraysOfIntegers_ParsesAsMultipleTokenArrays()
    {

        OpenAiEmbeddingInput? input = JsonSerializer.Deserialize("[[1,2],[3,4,5]]", ArcanumJsonContext.Default.OpenAiEmbeddingInput);

        Assert.NotNull(input);

        Assert.NotNull(input.TokenArrays);

        Assert.Equal(2, input.TokenArrays.Length);

        Assert.Equal([1, 2], input.TokenArrays[0]);

        Assert.Equal([3, 4, 5], input.TokenArrays[1]);

    }

    [Fact]
    public void Input_EmptyArray_ParsesAsEmptyStringsArray()
    {

        OpenAiEmbeddingInput? input = JsonSerializer.Deserialize("[]", ArcanumJsonContext.Default.OpenAiEmbeddingInput);

        Assert.NotNull(input);

        Assert.NotNull(input.Strings);

        Assert.Empty(input.Strings);

    }

    [Fact]
    public void Input_Null_ParsesAsNull()
    {

        OpenAiEmbeddingInput? input = JsonSerializer.Deserialize("null", ArcanumJsonContext.Default.OpenAiEmbeddingInput);

        Assert.Null(input);

    }

    [Fact]
    public void Input_BooleanTopLevel_Throws()
    {

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("true", ArcanumJsonContext.Default.OpenAiEmbeddingInput));

    }

    [Fact]
    public void Input_MixedArrayOfStringAndNumber_Throws()
    {

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""["a",1]""", ArcanumJsonContext.Default.OpenAiEmbeddingInput));

    }

    [Fact]
    public void Vector_FromFloats_WritesJsonNumberArray()
    {

        OpenAiEmbeddingVector vector = OpenAiEmbeddingVector.FromFloats([0.1f, -0.2f, 0.3f]);

        string json = JsonSerializer.Serialize(vector, ArcanumJsonContext.Default.OpenAiEmbeddingVector);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);

        Assert.Equal(3, document.RootElement.GetArrayLength());

    }

    [Fact]
    public void Vector_FromBase64_WritesJsonString()
    {

        OpenAiEmbeddingVector vector = OpenAiEmbeddingVector.FromBase64("QUJD");

        string json = JsonSerializer.Serialize(vector, ArcanumJsonContext.Default.OpenAiEmbeddingVector);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.String, document.RootElement.ValueKind);

        Assert.Equal("QUJD", document.RootElement.GetString());

    }

}
