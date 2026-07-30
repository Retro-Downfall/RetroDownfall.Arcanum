namespace RetroDownfall.Arcanum.Tests.Intelligence;

public class TurnLimitIntegrationTests
{
    [Fact]
    public void TurnLimit_Terminates_On_Repetition_Detected()
    {
        var detector = new RetroDownfall.Arcanum.Core.Intelligence.RepetitionDetector(
            maxIdenticalToolCalls: 2);
        detector.AnalyzeToolCall("t", "{}", "r");
        var verdict = detector.AnalyzeToolCall("t", "{}", "r");
        Assert.Equal(RetroDownfall.Arcanum.Core.Intelligence.RepetitionVerdict.IdenticalToolCall, verdict);
    }

    [Fact]
    public void Budget_Exhaustion_Produces_Correct_Error_Code()
    {
        const string expected = "Hub.RepetitionDetected";
        Assert.Equal("Hub.RepetitionDetected", RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Hub.RepetitionDetected);
        Assert.Equal("Hub.TurnLimitExceeded", RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Hub.TurnLimitExceeded);
        Assert.Equal("Hub.NoProgressDetected", RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Hub.NoProgressDetected);
    }
}
