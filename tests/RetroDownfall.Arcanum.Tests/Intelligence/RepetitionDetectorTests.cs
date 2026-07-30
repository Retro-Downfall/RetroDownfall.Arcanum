namespace RetroDownfall.Arcanum.Tests.Intelligence;

public class RepetitionDetectorTests
{
    [Fact]
    public void RepetitionDetector_Identical_ToolCall_Detected()
    {
        var detector = new RetroDownfall.Arcanum.Core.Intelligence.RepetitionDetector(
            maxIdenticalToolCalls: 2, maxFailedPatchCycles: 2, maxNoProgressRounds: 2);

        detector.AnalyzeToolCall("read_file", "{}", "content");
        var result1 = detector.AnalyzeToolCall("read_file", "{}", "content");
        Assert.Equal(RetroDownfall.Arcanum.Core.Intelligence.RepetitionVerdict.IdenticalToolCall, result1);
    }

    [Fact]
    public void RepetitionDetector_No_Progress_Round_Detected()
    {
        var detector = new RetroDownfall.Arcanum.Core.Intelligence.RepetitionDetector(
            maxIdenticalToolCalls: 5, maxFailedPatchCycles: 2, maxNoProgressRounds: 2);

        detector.AnalyzeRound(0, 0, null, System.Array.Empty<RetroDownfall.Arcanum.Core.Intelligence.ToolCallSignature>());
        var result = detector.AnalyzeRound(1, 0, null, System.Array.Empty<RetroDownfall.Arcanum.Core.Intelligence.ToolCallSignature>());
        Assert.Equal(RetroDownfall.Arcanum.Core.Intelligence.RepetitionVerdict.NoProgressRound, result);
    }
}
