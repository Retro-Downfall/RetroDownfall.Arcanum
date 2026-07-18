using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class InferenceTraceViewModelTests
{

    [Fact]
    public void Capture_GroupsToolRounds_AndExportsJson()
    {

        InferenceTraceViewModel trace = new();

        trace.BeginCapture("spell", "echo");

        trace.Capture(new IntelligenceEvent(
            IntelligenceEventType.ToolCall,
            "calling",
            ToolCall: new IntelligenceToolCallEvent("call-1", "lookup", "{}")));

        trace.Capture(new IntelligenceEvent(
            IntelligenceEventType.ToolResult,
            "ok",
            ToolCall: new IntelligenceToolCallEvent("call-1", "lookup", "{}")));

        trace.Capture(new IntelligenceEvent(
            IntelligenceEventType.SessionBound,
            Guid.NewGuid().ToString("D")));

        Assert.Equal(3, trace.Entries.Count);

        Assert.Equal("call-1", trace.Entries[0].ToolRoundId);

        Assert.Equal("call-1", trace.Entries[1].ToolRoundId);

        Assert.False(string.IsNullOrWhiteSpace(trace.SessionId));

        string json = trace.BuildExportJson();

        Assert.Contains("lookup", json, StringComparison.Ordinal);

        Assert.Contains(InferenceTraceViewModel.LimitationsText.Split('.')[0], trace.LimitationsBanner, StringComparison.Ordinal);

    }

    [Fact]
    public void DryRunButtons_WithoutHooks_SetHonestStatus()
    {

        InferenceTraceViewModel trace = new();

        trace.OpenSpellCastPreviewCommand.Execute(null);

        Assert.Contains("Cast", trace.StatusText, StringComparison.OrdinalIgnoreCase);

        trace.OpenPromptTestPreviewCommand.Execute(null);

        Assert.Contains("Test", trace.StatusText, StringComparison.OrdinalIgnoreCase);

    }

}
