using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildReasoningBoundaryTests
{
    [Fact]
    public void Frozen_provider_payload_refuses_reasoning_and_protected_state_before_followup_dispatch()
    {
        TextReasoningContent reasoning = new("hidden-reasoning-canary") { ProtectedData = "protected-state-canary" };

        ChatMessage[] messages =
        [
            new(ChatRole.User, "A permitted request"),
            new(ChatRole.Assistant, [new TextContent("Public answer"), reasoning]),
        ];

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => new GuildProviderPayload(messages, []));

        Assert.DoesNotContain("hidden-reasoning-canary", failure.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("protected-state-canary", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Guild_public_response_text_excludes_distinct_provider_reasoning_content()
    {
        TextReasoningContent reasoning = new("hidden-reasoning-canary") { ProtectedData = "protected-state-canary" };

        ChatResponse response = new(new ChatMessage(ChatRole.Assistant,
            [reasoning, new TextContent("Public answer")]));

        // This is the SDK projection used by WizardIntelligenceProvider's Guild completion path.
        Assert.Equal("Public answer", response.Text);

        GuildProviderPayload frozen = new(
            [new(ChatRole.User, "A permitted request"), new(ChatRole.Assistant, response.Text)], []);

        ChatMessage[] restored = [.. frozen.CreateMessages()];

        Assert.Equal("Public answer", restored[1].Text);

        Assert.DoesNotContain(restored.SelectMany(message => message.Contents), item => item is TextReasoningContent);
    }
}
