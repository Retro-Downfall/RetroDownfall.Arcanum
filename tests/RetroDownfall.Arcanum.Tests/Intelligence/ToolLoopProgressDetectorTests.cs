using System.Globalization;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The tool loop has no round, duration or model-call ceiling (DESIGN §2.1), so this detector is the
/// only structural stop. It must therefore catch an oscillation, not merely an immediate repeat.
/// </summary>
public sealed class ToolLoopProgressDetectorTests
{
    [Fact]
    public void ObserveCompletedRound_flags_an_immediately_repeated_round()
    {
        ToolLoopProgressDetector detector = new();

        Assert.False(detector.ObserveCompletedRound(Round("a.cs")));

        Assert.True(detector.ObserveCompletedRound(Round("a.cs")));
    }

    [Fact]
    public void ObserveCompletedRound_flags_a_two_round_oscillation()
    {
        ToolLoopProgressDetector detector = new();

        // The classic confused-model cycle: read a, read b, read a, … Comparing only against the
        // adjacent round never trips, so the loop would run until the operator killed it.
        Assert.False(detector.ObserveCompletedRound(Round("a.cs")));

        Assert.False(detector.ObserveCompletedRound(Round("b.cs")));

        Assert.True(detector.ObserveCompletedRound(Round("a.cs")));

        Assert.NotNull(detector.RepeatedSignature);
    }

    [Fact]
    public void ObserveCompletedRound_flags_a_three_round_oscillation()
    {
        ToolLoopProgressDetector detector = new();

        Assert.False(detector.ObserveCompletedRound(Round("a.cs")));

        Assert.False(detector.ObserveCompletedRound(Round("b.cs")));

        Assert.False(detector.ObserveCompletedRound(Round("c.cs")));

        Assert.True(detector.ObserveCompletedRound(Round("a.cs")));
    }

    [Fact]
    public void ObserveCompletedRound_never_stops_a_productive_loop()
    {
        ToolLoopProgressDetector detector = new();

        for (int round = 0; round < 64; round++)
        {
            Assert.False(
                detector.ObserveCompletedRound(
                    Round($"file-{round.ToString(CultureInfo.InvariantCulture)}.cs")));
        }

        Assert.Null(detector.RepeatedSignature);
    }

    [Fact]
    public void Protected_entries_keep_only_turn_local_keyed_equality_and_no_diagnostic_signature()
    {
        ToolLoopProgressDetector first = new();
        ToolLoopProgressDetector second = new();
        ToolLoopProgressEntry entry = first.CreateEntry("read_file", "protected-argument", "protected-result", protect: true);
        Assert.Equal(entry, first.CreateEntry("read_file", "protected-argument", "protected-result", protect: true));
        Assert.NotEqual(entry, second.CreateEntry("read_file", "protected-argument", "protected-result", protect: true));
        Assert.DoesNotContain("protected-argument", entry.ArgumentsJson);
        Assert.DoesNotContain("protected-result", entry.ResultText);
        Assert.False(first.ObserveCompletedRound([entry]));
        Assert.True(first.ObserveCompletedRound([entry]));
        Assert.Null(first.RepeatedSignature);
    }

    [Fact]
    public void Eviction_counts_complete_parallel_exchanges_and_bounds_round_correlation_without_payloads()
    {
        ToolLoopProgressDetector detector = new();

        Assert.Null(detector.Eviction);

        for (int round = 1; round <= 20; round++)
        {
            ChatMessage call = new(ChatRole.Assistant, [new FunctionCallContent("private-id", "private-tool"), new FunctionCallContent("private-id2", "private-tool")]);

            ChatMessage results = new(ChatRole.Tool, [new FunctionResultContent("private-id", "private-result"), new FunctionResultContent("private-id2", "private-result")]);

            Assert.False(detector.ObserveCompletedRound(Round(round.ToString(CultureInfo.InvariantCulture)), [call, results]));

            detector.ObserveTrimmedExchanges([call, results]);
        }

        ToolEvidenceEviction eviction = Assert.IsType<ToolEvidenceEviction>(detector.Eviction);

        Assert.Equal(20, eviction.Exchanges);
        Assert.Equal(40, eviction.Messages);
        Assert.Equal(40, eviction.Calls);
        Assert.Equal(Enumerable.Range(13, 8), eviction.RecentRoundOrdinals);
        Assert.Equal(12, eviction.OmittedRoundOrdinals);
        Assert.Equal(0, eviction.HistoricalExchanges);

        detector.ObserveTrimmedExchanges([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("old-private", "tool")]), new ChatMessage(ChatRole.Tool, [new FunctionResultContent("old-private", "old-result")])]);

        Assert.Equal(1, detector.Eviction!.HistoricalExchanges);
        Assert.Equal(21, detector.Eviction.Exchanges);
        Assert.Equal(8, detector.Eviction.RecentRoundOrdinals.Count);
        Assert.Equal(20, eviction.Exchanges);
    }

    private static IReadOnlyList<ToolLoopProgressEntry> Round(string path) =>
        [new ToolLoopProgressEntry("read_file", $$"""{"path":"{{path}}"}""", $"contents of {path}")];
}
