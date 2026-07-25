using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Pure-logic tests for the <c>POST /api/intelligence/mana</c> building blocks:
/// <see cref="InferenceContextBuilder.MapToAiChatMessages"/>.
/// HTTP-level behavior (validation, envelope shape) is covered by
/// <c>IntelligenceEndpointTests.PostMana_*</c>.
/// </summary>
public sealed class ManaCountingTests
{

    [Fact]
    public void MapToAiChatMessages_MapsRolesAndContent()
    {

        List<CoreChatMessage> messages =
        [
            new CoreChatMessage("system", "You are helpful."),
            new CoreChatMessage("user", "hello world"),
            new CoreChatMessage("assistant", "hi there"),
        ];

        List<MeAiChatMessage> mapped = InferenceContextBuilder.MapToAiChatMessages(messages);

        Assert.Equal(3, mapped.Count);

        Assert.Equal(ChatRole.System, mapped[0].Role);

        Assert.Equal(ChatRole.User, mapped[1].Role);

        Assert.Equal("hello world", mapped[1].Text);

        Assert.Equal(ChatRole.Assistant, mapped[2].Role);

    }

    [Fact]
    public void MapToAiChatMessages_ToolMessage_MapsToFunctionResultContent()
    {

        List<CoreChatMessage> messages =
        [
            new CoreChatMessage("tool", "42", ToolCallId: "call-1"),
        ];

        List<MeAiChatMessage> mapped = InferenceContextBuilder.MapToAiChatMessages(messages);

        Assert.Single(mapped);

        Assert.Equal(ChatRole.Tool, mapped[0].Role);

        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(mapped[0].Contents));

        Assert.Equal("call-1", result.CallId);

    }

}
