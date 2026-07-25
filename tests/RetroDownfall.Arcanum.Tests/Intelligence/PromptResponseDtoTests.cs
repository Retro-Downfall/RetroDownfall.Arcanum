using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class PromptResponseDtoTests
{
    [Fact]
    public void From_PreservesEveryResponseFieldIncludingReasoning()
    {
        ChatCompletionUsage usage = new(11, 7, 18, CachedTokens: 3, ReasoningTokens: 2);
        List<PromptToolCall> toolCalls = [new("call-1", "lookup", """{"id":1}""")];
        ReasoningContentSegment[] reasoning =
        [
            new("client-safe summary", ReasoningOutputMode.Summary),
        ];
        PromptTurnResult turn = new("answer", usage, toolCalls, "stop")
        {
            Reasoning = reasoning,
        };

        PromptResponseDto response = PromptResponseDto.From(turn);

        Assert.Equal("answer", response.Text);
        Assert.Same(usage, response.Usage);
        Assert.Same(toolCalls, response.ToolCalls);
        Assert.Equal("stop", response.FinishReason);
        Assert.Same(reasoning, response.Reasoning);
    }
}
