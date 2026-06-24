using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;

namespace RetroDownfall.Arcanum.Tests.Api.OpenAi;

public sealed class OpenAiMessageContentConverterTests
{

    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = ArcanumJsonContext.Default,
    };

    private readonly OpenAiMessageContentConverter _converter = new();

    [Fact]
    public void Read_String_ReturnsTextContent()
    {
        string json = "\"hello\"";

        Utf8JsonReader reader = CreateReader(json);

        reader.Read();

        OpenAiMessageContent? content = _converter.Read(ref reader, typeof(OpenAiMessageContent), Options);

        Assert.Equal("hello", content!.Text);

        Assert.Null(content.Parts);
    }

    [Fact]
    public void Read_Null_ReturnsNull()
    {
        Utf8JsonReader reader = CreateReader("null");

        reader.Read();

        OpenAiMessageContent? content = _converter.Read(ref reader, typeof(OpenAiMessageContent), Options);

        Assert.Null(content);
    }

    [Fact]
    public void Read_Array_ReturnsParts()
    {
        string json = """[{"type":"text","text":"hi"}]""";

        Utf8JsonReader reader = CreateReader(json);

        reader.Read();

        OpenAiMessageContent? content = _converter.Read(ref reader, typeof(OpenAiMessageContent), Options);

        Assert.NotNull(content!.Parts);

        Assert.Single(content.Parts!);

        Assert.Equal("text", content.Parts![0].Type);
    }

    [Fact]
    public void Read_InvalidToken_ThrowsJsonException()
    {
        Utf8JsonReader reader = CreateReader("42");

        JsonException ex = Assert.Throws<JsonException>(() =>
        {
            Utf8JsonReader local = CreateReader("42");

            local.Read();

            _ = _converter.Read(ref local, typeof(OpenAiMessageContent), Options);
        });

        Assert.Contains("content", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Parts_SerializesArray()
    {
        OpenAiMessageContent content = OpenAiMessageContent.FromParts(
            [new OpenAiContentPart("text", Text: "part")]);

        using MemoryStream stream = new();

        using Utf8JsonWriter writer = new(stream);

        _converter.Write(writer, content, Options);

        writer.Flush();

        string json = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("\"type\":\"text\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Text_SerializesString()
    {
        OpenAiMessageContent content = OpenAiMessageContent.FromText("wire");

        using MemoryStream stream = new();

        using Utf8JsonWriter writer = new(stream);

        _converter.Write(writer, content, Options);

        writer.Flush();

        string json = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        Assert.Equal("\"wire\"", json);
    }

    [Fact]
    public void Write_Null_WritesJsonNull()
    {
        using MemoryStream stream = new();

        using Utf8JsonWriter writer = new(stream);

        _converter.Write(writer, null!, Options);

        writer.Flush();

        string json = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        Assert.Equal("null", json);
    }

    private static Utf8JsonReader CreateReader(string json)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);

        return new Utf8JsonReader(bytes);
    }

}
