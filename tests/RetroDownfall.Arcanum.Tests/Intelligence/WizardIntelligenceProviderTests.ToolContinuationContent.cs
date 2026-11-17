using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ToolContinuation_preserves_each_round_visible_text_and_reasoning_once_with_multiple_calls(bool streaming, bool firstCallDenied)
    {
        ScriptingChatClient chat = new();

        TextReasoningContent firstThinking = new("synthetic first thinking");

        TextReasoningContent secondThinking = new("synthetic second thinking");

        QueueRound("one", "synthetic first visible", firstThinking);

        QueueRound("two", "synthetic second visible", secondThinking);

        if (streaming) chat.EnqueueStreamTokens("synthetic final answer");
        else chat.EnqueueText("synthetic final answer");

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(AIFunctionFactory.Create((string label) => $"synthetic result {label}", "continuation_probe"));

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        PingRequest request = BaseRequest() with
        {
            Prompt = "continue the synthetic tool exchange",

            SkipSpellRouting = true,

            UnattendedMode = true,
        };

        if (streaming)
        {
            List<IntelligenceEvent> events = await CollectStreamAsync(wizard, request);

            Assert.Contains(events, static item => item.Type == IntelligenceEventType.Result);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }

        List<IReadOnlyList<ChatMessage>> calls = streaming ? chat.AllStreamingCalls : chat.AllBufferedCalls;

        Assert.Equal(3, calls.Count);

        ChatMessage[] afterFirst = calls[1].Where(static message => message.Role == ChatRole.Assistant).ToArray();

        Assert.Equal(2, afterFirst.Length);

        Assert.Equal("synthetic first visible", afterFirst[0].Text);

        Assert.Empty(afterFirst[1].Contents.OfType<TextContent>());

        Assert.Same(firstThinking, Assert.Single(afterFirst.SelectMany(static message => message.Contents).OfType<TextReasoningContent>()));

        ChatMessage[] afterSecond = calls[2].Where(static message => message.Role == ChatRole.Assistant).ToArray();

        Assert.Equal(4, afterSecond.Length);

        Assert.Equal(["synthetic first visible", "synthetic second visible"], afterSecond.SelectMany(static message => message.Contents).OfType<TextContent>().Select(static content => content.Text));

        Assert.Equal([firstThinking, secondThinking], afterSecond.SelectMany(static message => message.Contents).OfType<TextReasoningContent>());

        Assert.Equal(["one-first", "one-second", "two-first", "two-second"], afterSecond.SelectMany(static message => message.Contents).OfType<FunctionCallContent>().Select(static call => call.CallId));

        Assert.Equal(["one-first", "one-second", "two-first", "two-second"], calls[2].SelectMany(static message => message.Contents).OfType<FunctionResultContent>().Select(static result => result.CallId));

        if (firstCallDenied)
        {
            Assert.All(calls[2].SelectMany(static message => message.Contents).OfType<FunctionResultContent>().Where(static result => result.CallId.EndsWith("first", StringComparison.Ordinal)),
                result => Assert.Contains("only available during attended streaming turns", result.Result?.ToString(), StringComparison.Ordinal));
        }

        void QueueRound(string round, string visible, TextReasoningContent thinking)
        {
            List<AIContent> contents =
            [
                thinking,
                new TextContent(visible),
                new FunctionCallContent($"{round}-first", firstCallDenied ? "ask_human" : "continuation_probe", new Dictionary<string, object?> { ["label"] = $"{round}-first" }),
                new FunctionCallContent($"{round}-second", "continuation_probe", new Dictionary<string, object?> { ["label"] = $"{round}-second" }),
            ];

            if (streaming) chat.EnqueueStreamUpdates(new ChatResponseUpdate(ChatRole.Assistant, contents));
            else chat.EnqueueResponse(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
        }
    }
}
