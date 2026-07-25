using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;

namespace RetroDownfall.Arcanum.Tests.Intelligence.OpenAi;

public sealed class OpenAiModerationInputConverterTests
{

    [Fact]
    public void Read_Null_ReturnsNull()
    {

        OpenAiModerationInput? input = JsonSerializer.Deserialize(
            "null",
            ArcanumJsonContext.Default.OpenAiModerationInput);

        Assert.Null(input);

    }

    [Fact]
    public void Read_String_ReturnsSingleValue()
    {

        OpenAiModerationInput? input = JsonSerializer.Deserialize(
            "\"hello world\"",
            ArcanumJsonContext.Default.OpenAiModerationInput);

        Assert.NotNull(input);
        Assert.Equal(["hello world"], input.Values);

    }

    [Fact]
    public void Read_StringArray_PreservesWireOrderAndEmptyStrings()
    {

        OpenAiModerationInput? input = JsonSerializer.Deserialize(
            """["first","","third"]""",
            ArcanumJsonContext.Default.OpenAiModerationInput);

        Assert.NotNull(input);
        Assert.Equal(["first", string.Empty, "third"], input.Values);

    }

    [Fact]
    public void Read_EmptyArray_ReturnsEmptyValues()
    {

        OpenAiModerationInput? input = JsonSerializer.Deserialize(
            "[]",
            ArcanumJsonContext.Default.OpenAiModerationInput);

        Assert.NotNull(input);
        Assert.Empty(input.Values);

    }

    [Fact]
    public void Read_ArrayContainingNonString_ThrowsSpecificJsonError()
    {

        JsonException exception = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                """["valid",42]""",
                ArcanumJsonContext.Default.OpenAiModerationInput));

        Assert.Contains(
            "'input' array elements must all be strings.",
            exception.Message,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Read_NumericTopLevel_ThrowsSpecificJsonError()
    {

        JsonException exception = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                "42",
                ArcanumJsonContext.Default.OpenAiModerationInput));

        Assert.Contains(
            "'input' must be a string or an array of strings (got Number).",
            exception.Message,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Write_SingleValue_UsesStringWireShape()
    {

        string json = JsonSerializer.Serialize(
            new OpenAiModerationInput { Values = ["only"] },
            ArcanumJsonContext.Default.OpenAiModerationInput);

        Assert.Equal("\"only\"", json);

    }

    [Fact]
    public void Write_MultipleValues_UsesArrayWireShape()
    {

        string json = JsonSerializer.Serialize(
            new OpenAiModerationInput { Values = ["first", "second"] },
            ArcanumJsonContext.Default.OpenAiModerationInput);

        Assert.Equal("""["first","second"]""", json);

    }

    [Fact]
    public void Write_EmptyValues_UsesEmptyArrayWireShape()
    {

        string json = JsonSerializer.Serialize(
            new OpenAiModerationInput { Values = [] },
            ArcanumJsonContext.Default.OpenAiModerationInput);

        Assert.Equal("[]", json);

    }

}
