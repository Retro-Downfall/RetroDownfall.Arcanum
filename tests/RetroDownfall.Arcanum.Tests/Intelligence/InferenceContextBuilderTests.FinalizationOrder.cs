using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class InferenceContextBuilderTests
{
    private static readonly DateTimeOffset HistoryFixtureTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Each_native_answer_follows_its_own_tool_pairs_and_retains_pair_evidence(bool preserveTools)
    {
        Session thread = new() { Id = Guid.NewGuid() };

        var firstUser = AppendHistoryEntry(thread, 1, MessageRole.User, "first question");

        var firstAnswer = AppendHistoryEntry(thread, 2, MessageRole.Assistant, "first answer");

        var first = AppendHistoryExchange(thread, 3, "first");

        var second = AppendHistoryExchange(thread, 5, "second");

        firstAnswer.FinalizedThroughSequence = 6;

        var secondUser = AppendHistoryEntry(thread, 7, MessageRole.User, "second question");

        var secondAnswer = AppendHistoryEntry(thread, 8, MessageRole.Assistant, "second answer");

        var third = AppendHistoryExchange(thread, 9, "third");

        secondAnswer.FinalizedThroughSequence = 10;

        Entry[] physical = [.. thread.Entries];

        PingRequest request = new("follow-up", SessionId: thread.Id);

        var messages = InferenceContextBuilder.BuildInitialMeAiChatMessages(request, thread, request.Prompt, preserveTools);

        Assert.Equal(
            new[] { firstUser.Content, first.Call.Content, first.Result.Content, second.Call.Content, second.Result.Content,
                firstAnswer.Content, secondUser.Content, third.Call.Content, third.Result.Content, secondAnswer.Content, request.Prompt },
            messages.Select(message => message.Text));

        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.System, ChatRole.Assistant, ChatRole.System,
            ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.System, ChatRole.Assistant, ChatRole.User },
            messages.Select(message => message.Role));

        foreach (int call in new[] { 1, 3, 7 })
            Assert.Equal(preserveTools ? 0 : 1, TurnContextGuards.HistoricalToolCallCount(messages[call]));

        Assert.Equal(physical, thread.Entries);
    }

    [Theory]
    [InlineData("unknown-frontier", true)]
    [InlineData("synthetic-answer", true)]
    [InlineData("foreign-exchange", false)]
    [InlineData("malformed-arguments", false)]
    [InlineData("missing-result", false)]
    [InlineData("intervening-user", true)]
    public void Incomplete_or_ambiguous_native_evidence_preserves_the_physical_order(string condition, bool firstPairRecognized)
    {
        Session thread = new() { Id = Guid.NewGuid() };

        _ = AppendHistoryEntry(thread, 1, MessageRole.User, "first question");

        var answer = AppendHistoryEntry(thread, 2, MessageRole.Assistant, "first answer");

        var first = AppendHistoryExchange(thread, 3, "first");

        var second = AppendHistoryExchange(thread, 5, "second");

        _ = AppendHistoryEntry(thread, 7, MessageRole.User, "next recorded question");

        answer.FinalizedThroughSequence = second.Result.Sequence;

        switch (condition)
        {
            case "unknown-frontier":
                answer.FinalizedThroughSequence = null;
                break;
            case "synthetic-answer":
                answer.Sequence = 0;
                break;
            case "foreign-exchange":
                first.Call.SessionId = first.Result.SessionId = Guid.NewGuid();
                break;
            case "malformed-arguments":
                first.Call.ToolArguments = "[]";
                first.Call.Content = "[ToolCall: first([])]";
                break;
            case "missing-result":
                Assert.True(thread.Entries.Remove(first.Result));
                break;
            case "intervening-user":
                second.Call.Role = MessageRole.User;
                second.Call.Content = "a different question";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(condition));
        }

        var messages = InferenceContextBuilder.MapGrimoireToMeAiMessages(thread, "follow-up");

        Assert.Equal(thread.Entries.OrderBy(entry => entry.Sequence).Select(entry => entry.Content).Append("follow-up"),
            messages.Select(message => message.Text));

        var firstCall = Assert.Single(messages, message => message.Text == first.Call.Content);

        Assert.Equal(firstPairRecognized ? 1 : 0, TurnContextGuards.HistoricalToolCallCount(firstCall));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compression_preserves_both_halves_of_a_pinned_pair_before_the_final_answer(bool pinCall)
    {
        const string summary = "Earlier conversation summary.";

        Session thread = new()
        {
            Id = Guid.NewGuid(),
            Summary = summary,
            LastSummarizedMessageAt = HistoryFixtureTime.AddMinutes(1).UtcDateTime,
        };

        _ = AppendHistoryEntry(thread, 1, MessageRole.User, string.Concat(Enumerable.Repeat("old context ", 2048)));

        var answer = AppendHistoryEntry(thread, 2, MessageRole.Assistant, "final answer");

        answer.IsPinned = true;

        var pinned = AppendHistoryExchange(thread, 3, "pinned");

        (pinCall ? pinned.Call : pinned.Result).IsPinned = true;

        _ = AppendHistoryExchange(thread, 5, "old");

        var recent = AppendHistoryExchange(thread, 7, "recent", HistoryFixtureTime.AddMinutes(2));

        answer.FinalizedThroughSequence = recent.Result.Sequence;

        PingRequest request = new("follow-up", SessionId: thread.Id);

        using ChatClientLease lease = CreateLease();

        var builder = CreateBuilder(new NullGrimoireRepository());

        var (compressed, messages) = builder.TryApplyContextCompressionIfNeeded(new ContextCompressionRequest
        {
            Request = request,
            Thread = thread,
            NewUserPrompt = request.Prompt,
            Messages = InferenceContextBuilder.BuildInitialMeAiChatMessages(request, thread, request.Prompt),
            Lease = lease,
            ChatOptions = new ChatOptions(),
        });

        Assert.True(compressed);

        Assert.Contains(summary, messages[0].Text, StringComparison.Ordinal);

        Assert.Equal(new[] { pinned.Call.Content, pinned.Result.Content, recent.Call.Content, recent.Result.Content, answer.Content, request.Prompt },
            messages.Skip(1).Select(message => message.Text));

        Assert.Equal(0, TurnContextGuards.HistoricalToolCallCount(messages[1]));

        Assert.Equal(1, TurnContextGuards.HistoricalToolCallCount(messages[3]));
    }

    private static Entry AppendHistoryEntry(Session thread, int sequence, MessageRole role, string content, DateTimeOffset? created = null)
    {
        Entry entry = new()
        {
            Id = Guid.NewGuid(),
            SessionId = thread.Id,
            Sequence = sequence,
            Role = role,
            Content = content,
            ModelUsed = "fixture-model",
            CreatedAt = created ?? HistoryFixtureTime,
        };

        thread.Entries.Add(entry);

        return entry;
    }

    private static (Entry Call, Entry Result) AppendHistoryExchange(Session thread, int sequence, string name, DateTimeOffset? created = null)
    {
        var call = AppendHistoryEntry(thread, sequence, MessageRole.Assistant, $"[ToolCall: {name}()]", created);

        call.ToolName = name;

        call.ToolArguments = string.Empty;

        var result = AppendHistoryEntry(thread, sequence + 1, MessageRole.System, $"[ToolResult: {name} output]", created);

        return (call, result);
    }
}
