namespace RetroDownfall.Arcanum.Tests.Intelligence;

public class TurnLimitIntegrationTests
{
    [Fact]
    public void TurnLimit_Terminates_On_Repetition_Detected()
    {
        var detector = new RetroDownfall.Arcanum.Api.Intelligence.ToolLoopProgressDetector();
        var round = new[] { new RetroDownfall.Arcanum.Api.Intelligence.ToolLoopProgressEntry("t", "{}", "r") };
        Assert.False(detector.ObserveCompletedRound(round));
        Assert.True(detector.ObserveCompletedRound(round));
    }

    [Fact]
    public void Budget_Exhaustion_Produces_Correct_Error_Code()
    {
        Assert.Equal("Hub.RepetitionDetected", RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Hub.RepetitionDetected);
        Assert.Equal("Hub.TurnLimitExceeded", RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Hub.TurnLimitExceeded);
        Assert.Equal("Hub.NoProgressDetected", RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Hub.NoProgressDetected);
    }
}
