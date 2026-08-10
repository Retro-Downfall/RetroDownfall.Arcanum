using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnContextGuardsTests
{

    [Fact]
    public void ExpandDeletionToCompleteToolGroups_IncludesToolResultPartner()
    {
        Guid callId = Guid.NewGuid();
        Guid resultId = Guid.NewGuid();
        Guid otherId = Guid.NewGuid();

        List<Entry> ordered =
        [
            new() { Id = callId, Role = MessageRole.Assistant, Content = "[ToolCall: x()]", ToolName = "x" },
            new() { Id = resultId, Role = MessageRole.System, Content = "[ToolResult: ok]" },
            new() { Id = otherId, Role = MessageRole.User, Content = "hi" },
        ];

        HashSet<Guid> expanded = TurnContextGuards.ExpandDeletionToCompleteToolGroups(ordered, [callId]);

        Assert.Contains(callId, expanded);
        Assert.Contains(resultId, expanded);
        Assert.DoesNotContain(otherId, expanded);
    }

    [Fact]
    public void DropOrphanToolHalves_RemovesLoneToolCall()
    {
        List<Entry> ordered =
        [
            new() { Id = Guid.NewGuid(), Role = MessageRole.User, Content = "hi" },
            new() { Id = Guid.NewGuid(), Role = MessageRole.Assistant, Content = "[ToolCall: x()]", ToolName = "x" },
        ];

        List<Entry> cleaned = TurnContextGuards.DropOrphanToolHalves(ordered);

        Assert.Single(cleaned);
        Assert.Equal(MessageRole.User, cleaned[0].Role);
    }

    [Fact]
    public void CheckContextBudget_FailsWhenOverWindow()
    {
        Result result = TurnContextGuards.CheckContextBudget(
            new ContextTokenBreakdown
            {
                Provider = "provider",
                Model = "model",
                Profile = new ResolvedModelTokenizationProfile
                {
                    ProfileId = "test",
                    Type = ModelTokenizationProfileType.UnknownFallback,
                    TokenizerId = "o200k_base",
                    SafetyMarginPercent = 15,
                    PerMessageOverheadTokens = 4,
                    PerToolOverheadTokens = 8,
                    ProviderFramingTokens = 3,
                    StopTokenOverheadTokens = 1,
                    UnknownImageReserveTokens = 2048,
                    Confidence = 0.5,
                },
                Components = [],
                InputTokens = 9_000,
                ReservedTokens = 1_024,
                TotalTokens = 10_024,
                OverallClassification = TokenEstimateClassification.Estimated,
                SafetyMarginTokens = 1_000,
            },
            contextWindowLimit: 8192);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Hub.ContextBudgetExceeded, result.Error.Code);
    }

    [Fact]
    public void TryTrimOldestToolExchanges_RemovesPairs()
    {
        List<MeAiChatMessage> messages =
        [
            new(ChatRole.System, "sys"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "tool_a")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", new string('x', 4000))]),
            new(ChatRole.User, "now"),
        ];

        _ = TurnContextGuards.TryTrimOldestToolExchanges(
            messages,
            static m => m.Sum(msg => 100 + (msg.Text?.Length ?? 0) + msg.Contents.Sum(c => 50 + (c.ToString()?.Length ?? 0))),
            maxTokens: 250);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.User, messages[1].Role);
    }

    /// <summary>
    /// A stateless <c>/v1</c> transcript maps N parallel tool calls to ONE assistant message
    /// followed by N tool messages. Removing a fixed pair splits that turn and leaves orphan tool
    /// results, which every OpenAI-compatible provider rejects.
    /// </summary>
    [Fact]
    public void TryTrimOldestToolExchanges_ParallelToolCalls_RemovesTheWholeExchange()
    {
        List<MeAiChatMessage> messages =
        [
            new(ChatRole.System, "sys"),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("c1", "tool_a"),
                new FunctionCallContent("c2", "tool_b"),
                new FunctionCallContent("c3", "tool_c"),
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", new string('x', 4000))]),
            new(ChatRole.Tool, [new FunctionResultContent("c2", new string('y', 4000))]),
            new(ChatRole.Tool, [new FunctionResultContent("c3", new string('z', 4000))]),
            new(ChatRole.User, "now"),
        ];

        _ = TurnContextGuards.TryTrimOldestToolExchanges(
            messages,
            static m => m.Sum(msg => 100 + (msg.Text?.Length ?? 0) + msg.Contents.Sum(c => 50 + (c.ToString()?.Length ?? 0))),
            maxTokens: 250);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.User, messages[1].Role);
    }

    [Fact]
    public void ResolveContinueThenReplay_AutoUsesIdempotencyHeader()
    {
        DefaultHttpContext withKey = new();
        withKey.Request.Headers[ArcanumApiHeaders.IdempotencyKey] = "k";

        DefaultHttpContext withoutKey = new();

        Assert.True(TurnContextGuards.ResolveContinueThenReplay(withKey, DisconnectPolicy.Auto));
        Assert.False(TurnContextGuards.ResolveContinueThenReplay(withoutKey, DisconnectPolicy.Auto));
        Assert.True(TurnContextGuards.ResolveContinueThenReplay(withoutKey, DisconnectPolicy.ContinueThenReplay));
        Assert.False(TurnContextGuards.ResolveContinueThenReplay(withKey, DisconnectPolicy.CancelAbandoned));
    }

}
