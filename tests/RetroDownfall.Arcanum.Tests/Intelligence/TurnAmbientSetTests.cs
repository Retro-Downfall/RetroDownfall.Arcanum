using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// How the turn's captured ambient set carries Covenant staging material from one provider round to
/// the next.
/// </summary>
public sealed class TurnAmbientSetTests
{
    /// <summary>
    /// A round whose dispatch earned no admission receipt has nothing to stage under. The captured set
    /// outlives the round, so the previous round's receipt must not ride into this round's tool calls,
    /// neither through the push nor through what <see cref="TurnAmbientSet.Apply"/> publishes before
    /// each tool call.
    /// </summary>
    [Fact]
    public void StageCovenantRound_ARoundWithNoReceiptClearsThePreviousRoundsStaging()
    {
        CovenantToolStagingAmbient.Current = null;
        TurnAmbientSet turn = new();
        CovenantToolStagingContext admitted = StagingContext();

        turn.StageCovenantRound(admitted);

        Assert.Same(admitted, turn.CovenantStaging);
        Assert.Same(admitted, CovenantToolStagingAmbient.Current);

        turn.StageCovenantRound(null);

        Assert.Null(turn.CovenantStaging);
        Assert.Null(CovenantToolStagingAmbient.Current);

        turn.Apply();

        Assert.Null(CovenantToolStagingAmbient.Current);

        turn.EndCovenantStaging();
    }

    /// <summary>
    /// Each admitted round replaces the last one's material rather than stacking on it, and the end
    /// of the turn restores whatever was ambient before the turn staged anything.
    /// </summary>
    [Fact]
    public void StageCovenantRound_ReplacesThePreviousRoundAndTheTurnsEndRestoresTheOuterValue()
    {
        CovenantToolStagingContext outer = StagingContext();
        CovenantToolStagingAmbient.Current = outer;
        TurnAmbientSet turn = new();
        CovenantToolStagingContext first = StagingContext();
        CovenantToolStagingContext second = StagingContext();

        try
        {
            turn.StageCovenantRound(first);
            turn.StageCovenantRound(second);

            Assert.Same(second, turn.CovenantStaging);
            Assert.Same(second, CovenantToolStagingAmbient.Current);

            turn.EndCovenantStaging();

            Assert.Same(outer, CovenantToolStagingAmbient.Current);
        }
        finally
        {
            CovenantToolStagingAmbient.Current = null;
        }
    }

    /// <summary>
    /// Only identity matters here: the set carries the material, it never reads it.
    /// </summary>
    private static CovenantToolStagingContext StagingContext() =>
        new(
            Collector: default!,
            Campaign: default!,
            ProducingAdmission: default!,
            Materialization: default!,
            HeadProbe: default!,
            CanStageProposal: true,
            Registry: default!,
            TurnCancellation: CancellationToken.None);
}
