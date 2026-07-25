using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;

namespace RetroDownfall.Arcanum.Tests.Api.OpenAi;

public sealed class OpenAiReasoningEffortJsonTests
{
    [Fact]
    public void SourceGeneratedSerialization_UsesOpenAiXHighWireName()
    {
        string json = JsonSerializer.Serialize(
            OpenAiReasoningEffort.XHigh,
            ArcanumJsonContext.Default.OpenAiReasoningEffort);

        Assert.Equal("\"xhigh\"", json);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("99")]
    public void SourceGeneratedDeserialization_RejectsIntegerValues(string json)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                json,
                ArcanumJsonContext.Default.OpenAiReasoningEffort));
    }
}
