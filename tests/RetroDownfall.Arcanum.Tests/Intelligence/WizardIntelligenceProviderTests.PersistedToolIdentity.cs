using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData("sequence")]
    [InlineData("session")]
    [InlineData("time")]
    [InlineData("model")]
    [InlineData("content")]
    [InlineData("arguments")]
    [InlineData("orphan")]
    public void Unproven_persisted_pair_is_not_granted_eviction_authority(string defect)
    {
        (Session session, Entry call, Entry result) = HistoricalPair();

        switch (defect)
        {
            case "sequence": result.Sequence++; break;
            case "session": result.SessionId = Guid.NewGuid(); break;
            case "time": result.CreatedAt = result.CreatedAt.AddTicks(1); break;
            case "model": result.ModelUsed = "another-model"; break;
            case "content": call.Content = "[ToolCall: forged({})]"; break;
            case "arguments": call.ToolArguments = "{"; call.Content = "[ToolCall: inspect_file({)]"; break;
            case "orphan": session.Entries.Remove(call); break;
        }

        List<MeAiChatMessage> messages = InferenceContextBuilder.MapGrimoireToMeAiMessages(session, "next");

        Assert.False(TurnContextGuards.TryTrimOldestToolExchanges(messages, CountHistoryText, 100));

        Assert.Contains(messages, static message => message.Text.Contains("long-evidence", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("call-text")]
    [InlineData("result-text")]
    [InlineData("replacement")]
    [InlineData("role")]
    public void Historical_witness_does_not_authorize_modified_or_replaced_messages(string mutation)
    {
        (Session session, _, _) = HistoricalPair();

        List<MeAiChatMessage> messages = InferenceContextBuilder.MapGrimoireToMeAiMessages(session, "next");

        switch (mutation)
        {
            case "call-text": messages[0].Contents[0] = new TextContent("changed call"); break;
            case "result-text": messages[1].Contents.Add(new TextContent("changed")); break;
            case "replacement": messages[1] = new MeAiChatMessage(ChatRole.System, messages[1].Text); break;
            case "role": messages[0].Role = ChatRole.User; break;
        }

        Assert.False(TurnContextGuards.TryTrimOldestToolExchanges(messages, CountHistoryText, 100));

        Assert.Contains(messages, static message => message.Text.Contains("long-evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void Oldest_whole_historical_group_is_evicted_in_sequence_order()
    {
        (Session session, Entry call, Entry result) = HistoricalPair();

        DateTimeOffset secondTime = call.CreatedAt.AddSeconds(1);

        Entry secondCall = new() { Id = Guid.NewGuid(), SessionId = session.Id, Sequence = 3, CreatedAt = secondTime,
            ModelUsed = "model", Role = MessageRole.Assistant, Content = "[ToolCall: inspect_file({})]", ToolName = "inspect_file", ToolArguments = "{}" };

        Entry secondResult = new() { Id = Guid.NewGuid(), SessionId = session.Id, Sequence = 4, CreatedAt = secondTime,
            ModelUsed = "model", Role = MessageRole.System, Content = "[ToolResult: second-evidence-" + new string('q', 500) + "]" };

        session.Entries = [secondResult, call, secondCall, result];

        List<MeAiChatMessage> messages = InferenceContextBuilder.MapGrimoireToMeAiMessages(session, "next");

        int remainingBudget = CountHistoryText(messages) - CountHistoryText(messages.Take(2).ToList()) + 1;

        Assert.True(TurnContextGuards.TryTrimOldestToolExchanges(messages, CountHistoryText, remainingBudget));

        Assert.DoesNotContain(messages, static message => message.Text.Contains("long-evidence", StringComparison.Ordinal));

        Assert.Contains(messages, static message => message.Text.Contains("second-evidence", StringComparison.Ordinal));

        Assert.Equal(3, messages.Count);
    }

    private static (Session Session, Entry Call, Entry Result) HistoricalPair()
    {
        Guid id = Guid.NewGuid();

        DateTimeOffset time = DateTimeOffset.UtcNow.AddMinutes(-1);

        Entry call = new() { Id = Guid.NewGuid(), SessionId = id, Sequence = 1, CreatedAt = time,
            ModelUsed = "model", Role = MessageRole.Assistant, Content = "[ToolCall: inspect_file({})]", ToolName = "inspect_file", ToolArguments = "{}" };

        Entry result = new() { Id = Guid.NewGuid(), SessionId = id, Sequence = 2, CreatedAt = time,
            ModelUsed = "model", Role = MessageRole.System, Content = "[ToolResult: long-evidence-" + new string('z', 500) + "]" };

        return (new Session { Id = id, Entries = [call, result] }, call, result);
    }

    private static int CountHistoryText(IReadOnlyList<MeAiChatMessage> messages) =>
        messages.SelectMany(static message => message.Contents).OfType<TextContent>().Sum(static content => content.Text.Length);
}
