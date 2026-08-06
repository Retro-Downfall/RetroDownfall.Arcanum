using System.Text.Json;

using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// How <c>arcanum watch apprentice</c> renders the four A2A Sending Chronicle frames (issue #61).
/// </summary>
/// <remarks>
/// The generic Apprentice renderer reads <c>description</c> before <c>result</c>/<c>error</c>, and every
/// Sending frame carries the agent URL in <c>description</c> — so all four frames used to render as the
/// same line and neither the remote response nor the failure reason was ever visible.
/// </remarks>
public sealed class WatchSendingViewTests
{

    private static WatchEventView Render(string json) =>
        WatchEventView.Create(
            WatchSource.Apprentice,
            JsonDocument.Parse(json).RootElement.Clone(),
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void DispatchedProgressCompletedAndFailed_RenderDistinguishably()
    {

        string dispatched = Render("""
            {"type":"sendingDispatched","description":"https://peer.example.test/","summary":"t-1"}
            """).Message;

        string progress = Render("""
            {"type":"sendingProgress","description":"https://peer.example.test/","summary":"t-1","sendingState":"working"}
            """).Message;

        string completed = Render("""
            {"type":"sendingCompleted","description":"https://peer.example.test/","summary":"t-1","result":"the remote answer"}
            """).Message;

        string failed = Render("""
            {"type":"sendingFailed","description":"https://peer.example.test/","summary":"t-1","error":"the remote refused"}
            """).Message;

        Assert.Equal(
            4,
            new HashSet<string>([dispatched, progress, completed, failed], StringComparer.Ordinal).Count);

        Assert.Contains("dispatched", dispatched, StringComparison.Ordinal);

        Assert.Contains("working", progress, StringComparison.Ordinal);

        Assert.Contains("the remote answer", completed, StringComparison.Ordinal);

        Assert.Contains("the remote refused", failed, StringComparison.Ordinal);

    }

    [Fact]
    public void EveryFrame_NamesThePeerAndTheRemoteTask()
    {

        foreach (string type in new[] { "sendingDispatched", "sendingProgress", "sendingCompleted", "sendingFailed" })
        {

            string message = Render($$"""
                {"type":"{{type}}","description":"https://peer.example.test/","summary":"t-1"}
                """).Message;

            Assert.Contains("https://peer.example.test/", message, StringComparison.Ordinal);

            Assert.Contains("t-1", message, StringComparison.Ordinal);

        }

    }

    [Fact]
    public void TerminalFrameWithNoReportedCost_SaysUnknownRatherThanZero()
    {

        string message = Render("""
            {"type":"sendingCompleted","description":"https://peer.example.test/","result":"done"}
            """).Message;

        Assert.Contains("cost unknown", message, StringComparison.Ordinal);

        Assert.DoesNotContain("0 tokens", message, StringComparison.Ordinal);

    }

    [Fact]
    public void TerminalFrameWithReportedCost_LabelsItAsExternal()
    {

        string message = Render("""
            {"type":"sendingCompleted","description":"https://peer.example.test/","result":"done",
             "remoteCostKnown":true,"remoteTotalTokens":4321,"remoteCostUsd":0.0125,"durationMs":1500}
            """).Message;

        // Remote spend must never read as local spend in the same pane.
        Assert.Contains("external", message, StringComparison.Ordinal);

        Assert.Contains("4,321 tokens", message, StringComparison.Ordinal);

        Assert.Contains("$0.0125", message, StringComparison.Ordinal);

        Assert.Contains("1.5 s", message, StringComparison.Ordinal);

    }

    [Fact]
    public void FailedFrameWithNoReason_StillRendersSomethingActionable()
    {

        string message = Render("""
            {"type":"sendingFailed","description":"https://peer.example.test/"}
            """).Message;

        Assert.Contains("no reason reported", message, StringComparison.Ordinal);

    }

    [Fact]
    public void NonSendingApprenticeFrames_AreUnaffected()
    {

        string message = Render("""
            {"type":"stepCompleted","description":"ran the tests","result":"green"}
            """).Message;

        Assert.Equal("ran the tests", message);

    }

}
