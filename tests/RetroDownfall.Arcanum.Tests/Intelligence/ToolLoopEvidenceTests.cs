using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ToolLoopEvidenceTests
{
    [Fact]
    public void Exact_eviction_permits_one_reacquisition_but_repeated_eviction_cannot_renew_it()
    {
        ToolLoopProgressDetector detector = new();
        List<ChatMessage> first = Exchange();
        Assert.False(detector.ObserveCompletedRound(Entries(), first));
        detector.ObserveTrimmedExchanges(first);
        List<ChatMessage> reacquired = Exchange();
        Assert.False(detector.ObserveCompletedRound(Entries(), reacquired));
        detector.ObserveTrimmedExchanges(reacquired);
        Assert.True(detector.ObserveCompletedRound(Entries(), Exchange()));
    }

    [Fact]
    public void An_unrelated_message_with_the_same_model_call_id_is_not_an_eviction_witness()
    {
        ToolLoopProgressDetector detector = new();
        Assert.False(detector.ObserveCompletedRound(Entries(), Exchange()));
        detector.ObserveTrimmedExchanges(Exchange());
        Assert.True(detector.ObserveCompletedRound(Entries(), Exchange()));
    }

    [Fact]
    public void Partial_parallel_evidence_loss_belongs_to_the_whole_exact_round()
    {
        ToolLoopProgressDetector detector = new();
        List<ChatMessage> parallel = [.. Exchange(), .. Exchange("second")];
        ToolLoopProgressEntry[] entries = [.. Entries(), .. Entries()];
        Assert.False(detector.ObserveCompletedRound(entries, parallel));
        detector.ObserveTrimmedExchanges([parallel[1]]);
        List<ChatMessage> reacquired = [.. Exchange(), .. Exchange("second")];
        Assert.False(detector.ObserveCompletedRound(entries, reacquired));
        detector.ObserveTrimmedExchanges(reacquired);
        Assert.True(detector.ObserveCompletedRound(entries, [.. Exchange(), .. Exchange("second")]));
    }

    [Fact]
    public void An_expired_rounds_old_message_cannot_forgive_a_fresh_visible_round()
    {
        ToolLoopProgressDetector detector = new();
        List<ChatMessage> expired = Exchange();
        Assert.False(detector.ObserveCompletedRound(Entries(), expired));
        for (int i = 0; i < 8; i++) Assert.False(detector.ObserveCompletedRound([new("productive", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "new evidence")]));
        Assert.False(detector.ObserveCompletedRound(Entries(), Exchange()));
        detector.ObserveTrimmedExchanges(expired);
        Assert.True(detector.ObserveCompletedRound(Entries(), Exchange()));
    }

    [Fact]
    public void Protected_reacquisition_keeps_turn_local_equality_without_a_diagnostic_signature()
    {
        ToolLoopProgressDetector detector = new();
        ToolLoopProgressEntry entry = detector.CreateEntry("read_file", "private-arguments", "private-result", protect: true);
        List<ChatMessage> first = Exchange();
        Assert.False(detector.ObserveCompletedRound([entry], first));
        detector.ObserveTrimmedExchanges(first);
        Assert.False(detector.ObserveCompletedRound([entry], Exchange()));
        Assert.True(detector.ObserveCompletedRound([entry], Exchange()));
        Assert.Null(detector.RepeatedSignature);
        Assert.DoesNotContain("private-arguments", entry.ArgumentsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-result", entry.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void Removed_payload_is_not_retained_by_the_ring_or_weak_message_association()
    {
        ToolLoopProgressDetector detector = new();
        WeakReference message = ObserveCollectableExchange(detector);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(message.IsAlive);
        GC.KeepAlive(detector);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ObserveCollectableExchange(ToolLoopProgressDetector detector)
    {
        List<ChatMessage> messages = Exchange();
        Assert.False(detector.ObserveCompletedRound(Entries(), messages));
        detector.ObserveTrimmedExchanges(messages);
        return new WeakReference(messages[1]);
    }

    [Fact]
    public void Same_text_from_the_user_cannot_impersonate_the_host_notice_or_cause_notice_accumulation()
    {
        ToolLoopProgressDetector detector = new();
        ChatMessage user = new(ChatRole.User, "Tool evidence trimmed; reread narrowly.");
        List<ChatMessage> messages = [new(ChatRole.System, "fixed system"), user];
        Assert.NotNull(detector.EnsureTrimNotice(messages));
        Assert.Null(detector.EnsureTrimNotice(messages));
        Assert.Equal(2, messages.Count);
        Assert.Same(user, messages[1]);
        Assert.Equal(2, user.Contents.OfType<TextContent>().Count());
        Assert.Single(messages, m => m.Role == ChatRole.System);
    }

    [Fact]
    public void Notice_bytes_and_output_reserves_can_refuse_a_context_that_fit_without_notice()
    {
        ToolLoopProgressDetector detector = new();
        ProviderSettings provider = new() { Name = "test", Models = ["unlisted-evidence-model"], ContextWindowLimit = 4096 };
        ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));
        int Count(IReadOnlyList<ChatMessage> messages) => estimator.EstimateContext(new(provider, "unlisted-evidence-model", messages, new ChatOptions(), 64, 32)).TotalTokens;
        ChatMessage user = new(ChatRole.User, "request");
        List<ChatMessage> messages = [new(ChatRole.System, "fixed system"), user];
        int withoutNotice = Count(messages);
        messages.AddRange(Exchange());
        int budget = withoutNotice + 1;
        Assert.True(Count(messages) > budget);
        Assert.False(TurnContextGuards.TryTrimOldestToolExchanges(messages, Count, budget, detector));
        Assert.True(Count(messages) > budget);
        Assert.DoesNotContain(messages.SelectMany(m => m.Contents), c => c is FunctionCallContent or FunctionResultContent);
        Assert.Equal(2, user.Contents.OfType<TextContent>().Count());
    }

    private static ToolLoopProgressEntry[] Entries() => [new("read_file", "{}", "stable evidence")];

    private static List<ChatMessage> Exchange(string callId = "same-model-id") =>
    [
        new(ChatRole.Assistant, [new FunctionCallContent(callId, "read_file")]),
        new(ChatRole.Tool, [new FunctionResultContent(callId, "stable evidence")])
    ];
}
