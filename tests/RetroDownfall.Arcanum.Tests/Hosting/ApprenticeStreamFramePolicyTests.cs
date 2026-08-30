using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class ApprenticeStreamFramePolicyTests
{
    [Theory]
    [InlineData(IntelligenceEventType.Reasoning)]
    [InlineData(IntelligenceEventType.Token)]
    [InlineData(IntelligenceEventType.Status)]
    [InlineData(IntelligenceEventType.SessionBound)]
    [InlineData(IntelligenceEventType.ConversationBound)]
    public void Classify_keeps_nonterminal_content_out_of_Apprentice_persistence(
        IntelligenceEventType type)
    {
        Assert.Equal(
            ApprenticeStreamFrameDisposition.Ignore,
            ApprenticeStreamFramePolicy.Classify(type));
    }

    [Fact]
    public void Classify_ignores_unknown_future_type_so_stream_can_continue()
    {
        IntelligenceEventType future = (IntelligenceEventType)int.MaxValue;

        Assert.Equal(
            ApprenticeStreamFrameDisposition.Ignore,
            ApprenticeStreamFramePolicy.Classify(future));
    }

    [Theory]
    [InlineData(IntelligenceEventType.Result, nameof(ApprenticeStreamFrameDisposition.Result))]
    [InlineData(IntelligenceEventType.Error, nameof(ApprenticeStreamFrameDisposition.Error))]
    [InlineData(IntelligenceEventType.ToolCall, nameof(ApprenticeStreamFrameDisposition.ToolCall))]
    [InlineData(IntelligenceEventType.ToolResult, nameof(ApprenticeStreamFrameDisposition.ToolResult))]
    [InlineData(IntelligenceEventType.ToolError, nameof(ApprenticeStreamFrameDisposition.ToolError))]
    [InlineData(IntelligenceEventType.Warded, nameof(ApprenticeStreamFrameDisposition.Warded))]
    [InlineData(IntelligenceEventType.WardResolved, nameof(ApprenticeStreamFrameDisposition.WardResolved))]
    public void Classify_maps_only_explicit_Apprentice_stream_actions(
        IntelligenceEventType type,
        string expected)
    {
        Assert.Equal(expected, ApprenticeStreamFramePolicy.Classify(type).ToString());
    }

    [Fact]
    public void Legacy_denied_Ward_resolution_is_informational_not_terminal_evidence()
    {
        IntelligenceEvent frame = new(
            IntelligenceEventType.WardResolved,
            "write_file",
            WardId: "legacy-ward",
            WardToolName: "write_file",
            WardAllowed: false);

        Assert.False(ApprenticeStreamFramePolicy.IsTerminalToolDenial(frame));
    }

    [Fact]
    public void Structured_tool_denial_remains_terminal_evidence()
    {
        IntelligenceEvent frame = new(
            IntelligenceEventType.ToolResult,
            "write_file",
            ToolCall: new IntelligenceToolCallEvent("call-1", "write_file", "{}"),
            ToolDenied: true);

        Assert.True(ApprenticeStreamFramePolicy.IsTerminalToolDenial(frame));
    }
}
