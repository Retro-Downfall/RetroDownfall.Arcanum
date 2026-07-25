using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Api.OpenAi;

public sealed class OpenAiChatCompletionMapperTests
{

    [Fact]
    public void ToPingRequest_MapsInferenceParametersAndStatelessMessages()
    {
        OpenAiChatRequest request = new(
            Model: "gpt-test",
            Messages:
            [
                new OpenAiChatMessage("user", OpenAiMessageContent.FromText("hello")),
            ],
            Temperature: 0.7f,
            TopP: 0.9f,
            MaxCompletionTokens: 128,
            Seed: 42,
            PresencePenalty: 0.1f,
            FrequencyPenalty: 0.2f,
            User: "user-1",
            ParallelToolCalls: false,
            Stop: JsonDocument.Parse("\"END\"").RootElement,
            ResponseFormat: new OpenAiResponseFormat("json_object"));

        PingRequest ping = OpenAiChatCompletionMapper.ToPingRequest(request);

        Assert.Equal("gpt-test", ping.Model);

        Assert.True(ping.UnattendedMode);

        Assert.Single(ping.StatelessMessages!);

        Assert.Equal(0.7f, ping.Temperature);

        Assert.Equal(0.9f, ping.TopP);

        Assert.Equal(128, ping.MaxOutputTokens);

        Assert.Equal(42, ping.Seed);

        Assert.Equal("json_object", ping.ResponseFormat);

        Assert.Single(ping.Stop!);

        Assert.Equal("END", ping.Stop![0]);
    }

    [Fact]
    public void ToPingRequest_StopArray_FiltersNonStringEntries()
    {
        OpenAiChatRequest request = new(
            Model: "m",
            Messages: [new OpenAiChatMessage("user", OpenAiMessageContent.FromText("x"))],
            Stop: JsonDocument.Parse("""["a", 1, "b", ""]""").RootElement);

        PingRequest ping = OpenAiChatCompletionMapper.ToPingRequest(request);

        Assert.Equal(["a", "b"], ping.Stop);
    }

    [Fact]
    public void ToPingRequest_MapsNormalizedReasoningEffortAndOutput()
    {
        OpenAiChatRequest request = JsonSerializer.Deserialize(
            """
            {
              "model": "reasoner",
              "messages": [{ "role": "user", "content": "solve" }],
              "reasoning_effort": "minimal",
              "reasoning_output": "summary"
            }
            """,
            ArcanumJsonContext.Default.OpenAiChatRequest)!;

        PingRequest ping = OpenAiChatCompletionMapper.ToPingRequest(request);

        Assert.Equal(
            new ReasoningRequestOptions(
                Effort: ReasoningEffortLevel.Minimal,
                BudgetTokens: null,
                Output: ReasoningOutputMode.Summary),
            ping.Reasoning);
    }

    [Fact]
    public void ToPingRequest_MapsOpenAiXHighToNormalizedExtraHigh()
    {
        OpenAiChatRequest request = JsonSerializer.Deserialize(
            """
            {
              "model": "reasoner",
              "messages": [{ "role": "user", "content": "solve" }],
              "reasoning_effort": "xhigh"
            }
            """,
            ArcanumJsonContext.Default.OpenAiChatRequest)!;

        PingRequest ping = OpenAiChatCompletionMapper.ToPingRequest(request);

        Assert.Equal(ReasoningEffortLevel.ExtraHigh, ping.Reasoning?.Effort);
    }

    [Fact]
    public void ToPingRequest_MapsAdditiveReasoningBudget()
    {
        OpenAiChatRequest request = JsonSerializer.Deserialize(
            """
            {
              "model": "reasoner",
              "messages": [{ "role": "user", "content": "solve" }],
              "reasoning_budget": 4096,
              "reasoning_output": "full"
            }
            """,
            ArcanumJsonContext.Default.OpenAiChatRequest)!;

        PingRequest ping = OpenAiChatCompletionMapper.ToPingRequest(request);

        Assert.Equal(
            new ReasoningRequestOptions(
                Effort: null,
                BudgetTokens: 4096,
                Output: ReasoningOutputMode.Full),
            ping.Reasoning);
    }

    [Fact]
    public void ToPingRequest_WithoutReasoningFields_LeavesReasoningUnset()
    {
        OpenAiChatRequest request = new(
            Model: "m",
            Messages: [new OpenAiChatMessage("user", OpenAiMessageContent.FromText("x"))]);

        PingRequest ping = OpenAiChatCompletionMapper.ToPingRequest(request);

        Assert.Null(ping.Reasoning);
    }

    [Fact]
    public void ToCoreChatMessage_MapsToolCallsAndSkipsNullFunction()
    {
        OpenAiChatMessage message = new(
            Role: "assistant",
            Content: OpenAiMessageContent.FromText("done"),
            ToolCalls:
            [
                new OpenAiToolCall("c1", "function", new OpenAiFunctionCall("get_time", "{}")),
                new OpenAiToolCall("c2", "function", null!),
            ]);

        CoreChatMessage core = OpenAiChatCompletionMapper.ToCoreChatMessage(message);

        Assert.Single(core.ToolCalls!);

        Assert.Equal("c1", core.ToolCalls![0].Id);

        Assert.Equal("get_time", core.ToolCalls[0].Name);
    }

    [Fact]
    public void ResolveContent_TextOnly_ReturnsFlatText()
    {
        (string flat, IReadOnlyList<CoreContentPart>? parts) = OpenAiChatCompletionMapper.ResolveContent(
            OpenAiMessageContent.FromText("plain"));

        Assert.Equal("plain", flat);

        Assert.Null(parts);
    }

    [Fact]
    public void ResolveContent_Multimodal_FlattensTextAndImageReferences()
    {
        OpenAiMessageContent content = OpenAiMessageContent.FromParts(
        [
            new OpenAiContentPart("text", Text: "line one"),
            new OpenAiContentPart("image_url", ImageUrl: new OpenAiImageUrl("https://img.test/a.png", "high")),
            new OpenAiContentPart("text", Text: "line two"),
        ]);

        (string flat, IReadOnlyList<CoreContentPart>? parts) = OpenAiChatCompletionMapper.ResolveContent(content);

        Assert.Contains("line one", flat, StringComparison.Ordinal);

        Assert.Contains("[image: https://img.test/a.png]", flat, StringComparison.Ordinal);

        Assert.Contains("line two", flat, StringComparison.Ordinal);

        Assert.Equal(3, parts!.Count);
    }

    [Fact]
    public void ResolveContent_NullContent_ReturnsEmpty()
    {
        (string flat, IReadOnlyList<CoreContentPart>? parts) = OpenAiChatCompletionMapper.ResolveContent(null);

        Assert.Equal(string.Empty, flat);

        Assert.Null(parts);
    }

}
