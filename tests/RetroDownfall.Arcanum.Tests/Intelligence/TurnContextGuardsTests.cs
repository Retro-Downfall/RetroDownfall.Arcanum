using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
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
            messageTokens: 9000,
            tools: null,
            contextWindowLimit: 8192,
            reservedOutputTokens: 1024);

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
