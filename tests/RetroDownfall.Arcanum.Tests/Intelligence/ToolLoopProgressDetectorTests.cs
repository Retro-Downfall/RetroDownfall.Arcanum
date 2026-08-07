using System.Globalization;
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

    private static IReadOnlyList<ToolLoopProgressEntry> Round(string path) =>
        [new ToolLoopProgressEntry("read_file", $$"""{"path":"{{path}}"}""", $"contents of {path}")];

}
