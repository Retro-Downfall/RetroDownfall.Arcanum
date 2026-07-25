using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ReasoningContractsJsonTests
{

    [Theory]
    [InlineData(ReasoningEffortLevel.None, "\"none\"")]
    [InlineData(ReasoningEffortLevel.Minimal, "\"minimal\"")]
    [InlineData(ReasoningEffortLevel.Low, "\"low\"")]
    [InlineData(ReasoningEffortLevel.Medium, "\"medium\"")]
    [InlineData(ReasoningEffortLevel.High, "\"high\"")]
    [InlineData(ReasoningEffortLevel.ExtraHigh, "\"extraHigh\"")]
    public void Effort_UsesStableNormalizedWireName(ReasoningEffortLevel effort, string expectedJson)
    {

        string json = JsonSerializer.Serialize(effort, ArcanumJsonContext.Default.ReasoningEffortLevel);

        Assert.Equal(expectedJson, json);

    }

    [Theory]
    [InlineData(ReasoningOutputMode.None, "\"none\"")]
    [InlineData(ReasoningOutputMode.Summary, "\"summary\"")]
    [InlineData(ReasoningOutputMode.Full, "\"full\"")]
    public void Output_UsesStableNormalizedWireName(ReasoningOutputMode output, string expectedJson)
    {

        string json = JsonSerializer.Serialize(output, ArcanumJsonContext.Default.ReasoningOutputMode);

        Assert.Equal(expectedJson, json);

    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    public void Effort_SourceGeneratedContractRejectsDefinedAndUndefinedIntegers(string json)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ReasoningEffortLevel));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    public void Output_SourceGeneratedContractRejectsDefinedAndUndefinedIntegers(string json)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ReasoningOutputMode));
    }

    [Theory]
    [InlineData("""{"prompt":"hello","reasoning":{"effort":0}}""")]
    [InlineData("""{"prompt":"hello","reasoning":{"effort":99}}""")]
    [InlineData("""{"prompt":"hello","reasoning":{"output":0}}""")]
    [InlineData("""{"prompt":"hello","reasoning":{"output":99}}""")]
    public void PingRequest_RejectsNumericReasoningEnums(string json)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.PingRequest));
    }

    [Fact]
    public void RequestOptions_RoundTrip_UsesStableWireContract()
    {

        ReasoningRequestOptions original = new(
            ReasoningEffortLevel.ExtraHigh,
            BudgetTokens: null,
            ReasoningOutputMode.Summary);

        string json = JsonSerializer.Serialize(original, ArcanumJsonContext.Default.ReasoningRequestOptions);

        Assert.Equal("""{"effort":"extraHigh","output":"summary"}""", json);

        ReasoningRequestOptions? roundTripped = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ReasoningRequestOptions);

        Assert.Equal(original, roundTripped);

    }

    [Fact]
    public void ContentSegment_RoundTrip_ContainsOnlyClientSafeTextAndOutput()
    {

        ReasoningContentSegment original = new("visible summary", ReasoningOutputMode.Summary);

        string json = JsonSerializer.Serialize(original, ArcanumJsonContext.Default.ReasoningContentSegment);

        Assert.Equal("""{"text":"visible summary","output":"summary"}""", json);

        ReasoningContentSegment? roundTripped = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ReasoningContentSegment);

        Assert.Equal(original, roundTripped);

    }

    [Fact]
    public void PingRequest_RoundTrip_PreservesTypedReasoningOptions()
    {

        PingRequest original = new(
            Prompt: "hello",
            Reasoning: new ReasoningRequestOptions(
                Effort: null,
                BudgetTokens: 4096,
                Output: ReasoningOutputMode.Full));

        string json = JsonSerializer.Serialize(original, ArcanumJsonContext.Default.PingRequest);

        PingRequest? roundTripped = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.PingRequest);

        Assert.NotNull(roundTripped?.Reasoning);
        Assert.Null(roundTripped!.Reasoning!.Effort);
        Assert.Equal(4096, roundTripped.Reasoning.BudgetTokens);
        Assert.Equal(ReasoningOutputMode.Full, roundTripped.Reasoning.Output);

    }

}
