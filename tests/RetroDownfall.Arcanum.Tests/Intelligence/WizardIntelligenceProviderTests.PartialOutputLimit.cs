using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false, true, "", "length")]
    [InlineData(true, true, "", "length")]
    [InlineData(false, false, "partial final answer", "length")]
    [InlineData(true, false, "partial final answer", "length")]
    [InlineData(false, true, "partial final answer", "length")]
    [InlineData(true, true, "partial final answer", "length")]
    [InlineData(false, true, "ordinary final answer", "stop")]
    [InlineData(true, true, "ordinary final answer", "stop")]
    public async Task PartialOutputLimit_preserves_progress_tools_final_text_and_usage_without_replaying(
        bool streaming, bool toolRound, string finalAnswer, string finishReason)
    {
        const string progress = "I will start the implementation and then run the tests.\n";

        const string callId = "partial-output-limit-real-tool";

        ScriptingChatClient chat = new();

        if (toolRound)
        {
            List<AIContent> content = [new TextContent(progress),
                new FunctionCallContent(callId, "get_local_system_time", new Dictionary<string, object?>())];

            UsageDetails usage = new() { InputTokenCount = 3, OutputTokenCount = 2, TotalTokenCount = 5,
                CachedInputTokenCount = 1, ReasoningTokenCount = 1 };

            if (streaming)
                chat.EnqueueStreamUpdates(new ChatResponseUpdate(ChatRole.Assistant, content),
                    new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]) { FinishReason = ChatFinishReason.ToolCalls });
            else
                chat.EnqueueResponse(new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, content))
                { FinishReason = ChatFinishReason.ToolCalls, Usage = usage });
        }

        EnqueueOutputLimitResponse(chat, streaming, finalAnswer, new ChatFinishReason(finishReason), includePrivateReasoning: true);

        FakeGrimoireRepository grimoire = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, SettingsWithReasoning(), grimoire: grimoire);

        OutputLimitObservation observed = await ObserveOutputLimitTurnAsync(wizard, streaming,
            OutputLimitRequest() with { DisableAllTools = !toolRound, DisableMcpTools = true });

        string expectedText = (toolRound ? progress : "") + finalAnswer;

        Assert.True(observed.Success, observed.WireJson);

        Assert.Equal(expectedText, observed.Text);

        Assert.Equal(expectedText, grimoire.LastFinalizedContent);

        Assert.Equal(finishReason, observed.FinishReason);

        Assert.Equal(toolRound ? 13 : 10, observed.Usage?.PromptTokens);

        Assert.Equal(toolRound ? 10 : 8, observed.Usage?.CompletionTokens);

        Assert.Equal(toolRound ? 24 : 19, observed.Usage?.TotalTokens);

        Assert.Equal(toolRound ? 4 : 3, observed.Usage?.CachedTokens);

        Assert.Equal(toolRound ? 8 : 7, observed.Usage?.ReasoningTokens);

        Assert.Equal(toolRound ? 2 : 1, streaming ? chat.StreamingCallCount : chat.BufferedCallCount);

        Assert.Equal(toolRound ? 1 : 0, grimoire.AppendToolInteractionCallCount);

        if (toolRound)
        {
            Assert.Equal(callId, Assert.Single(observed.ToolCalls).CallId);

            Assert.Contains(chat.LastBufferedMessages.SelectMany(static message => message.Contents),
                content => content is FunctionResultContent result && result.CallId == callId);
        }
        else Assert.Empty(observed.ToolCalls);

        Assert.DoesNotContain(OutputLimitPrivateReasoning, observed.WireJson, StringComparison.Ordinal);

        Assert.DoesNotContain(OutputLimitProtectedReasoning, observed.WireJson, StringComparison.Ordinal);

        Assert.DoesNotContain(observed.Events, static frame => frame.Type == IntelligenceEventType.Reasoning);

        if (finishReason == "stop")
        {
            Assert.Empty(observed.Notices);

            return;
        }

        InferenceNotice notice = Assert.Single(observed.Notices);

        Assert.Equal(4, (int)notice.Code);

        Assert.Equal("OutputLimitWithPartialAnswer", Enum.GetName(notice.Code));

        Assert.Contains("incomplete", notice.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("does not confirm", notice.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(notice.Message, expectedText, StringComparison.Ordinal);

        using JsonDocument wire = JsonDocument.Parse(observed.TerminalJson);

        JsonElement serialized = Assert.Single(wire.RootElement.GetProperty("notices").EnumerateArray());

        Assert.Equal(4, serialized.GetProperty("code").GetInt32());

        Assert.Equal(notice.Message, serialized.GetProperty("message").GetString());

        if (streaming)
        {
            Assert.Equal(notice.Message, Assert.Single(observed.Events,
                frame => frame.Type == IntelligenceEventType.Status && frame.Notices.Any(item => (int)item.Code == 4)).Message);

            Assert.Equal(expectedText, string.Concat(observed.Events.Where(static frame => frame.Type == IntelligenceEventType.Token)
                .Select(static frame => frame.Data)));
        }
    }
}
