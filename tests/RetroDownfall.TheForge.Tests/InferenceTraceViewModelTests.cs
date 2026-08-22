using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.TheForge.Core.Models.Traces;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using System.Text.Json;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class InferenceTraceViewModelTests
{

    [Fact]
    public void Capture_GroupsToolRounds_AndExportsJson()
    {

        InferenceTraceViewModel trace = new(
            ImmediateTheForgeLocalMutationRunner.Instance);

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
    public async Task ExportAsync_WhenTheWriteFails_ReportsInsteadOfEscaping()
    {

        // A directory that does not exist: File.WriteAllTextAsync throws, exactly as a read-only
        // volume, a vanished removable drive, or a permission denial would.
        InferenceTraceViewModel trace = new(
            ImmediateTheForgeLocalMutationRunner.Instance,
            fileDialog: new FixedPathArtifactFileDialogService(FixedPathArtifactFileDialogService.UnwritablePath()));

        trace.BeginCapture("spell", "echo");

        await trace.ExportAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(trace.LastError));

        Assert.NotEqual("Trace exported.", trace.StatusText);

    }

    [Fact]
    public async Task PersistAsync_WhenTheStoreFails_ReportsInsteadOfEscaping()
    {

        InferenceTraceViewModel trace = new(
            ImmediateTheForgeLocalMutationRunner.Instance,
            store: new ThrowingInferenceTraceStore());

        trace.BeginCapture("spell", "echo");

        await trace.PersistAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(trace.LastError));

        Assert.NotEqual("Trace saved locally.", trace.StatusText);

    }

    [Fact]
    public void DryRunButtons_WithoutHooks_SetHonestStatus()
    {

        InferenceTraceViewModel trace = new(
            ImmediateTheForgeLocalMutationRunner.Instance);

        trace.OpenSpellCastPreviewCommand.Execute(null);

        Assert.Contains("Cast", trace.StatusText, StringComparison.OrdinalIgnoreCase);

        trace.OpenPromptTestPreviewCommand.Execute(null);

        Assert.Contains("Test", trace.StatusText, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Reasoning_capture_and_export_retain_event_metadata_but_redact_body()
    {
        const string sensitive = "sensitive client-safe reasoning body";
        InferenceTraceViewModel trace = new(
            ImmediateTheForgeLocalMutationRunner.Instance);
        trace.BeginCapture("session", Guid.NewGuid().ToString("D"));

        trace.Capture(new IntelligenceEvent(
            IntelligenceEventType.Reasoning,
            sensitive,
            sensitive,
            Usage: new ChatCompletionUsage(
                PromptTokens: 11,
                CompletionTokens: 13,
                TotalTokens: 24,
                CachedTokens: 3,
                ReasoningTokens: 7),
            Reasoning: new RetroDownfall.Arcanum.Core.Intelligence.ReasoningContentSegment(
                sensitive,
                RetroDownfall.Arcanum.Core.Intelligence.ReasoningOutputMode.Summary)));

        InferenceTraceEntryViewModel entry = Assert.Single(trace.Entries);
        Assert.Equal(nameof(IntelligenceEventType.Reasoning), entry.Type);
        Assert.DoesNotContain(sensitive, entry.DisplayLine, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Data);
        Assert.Equal("Summary", entry.ReasoningOutputMode);
        Assert.Equal(7, entry.ReasoningTokens);

        string json = trace.BuildExportJson();
        Assert.Contains(nameof(IntelligenceEventType.Reasoning), json, StringComparison.Ordinal);
        Assert.Contains("\"reasoningOutputMode\":\"Summary\"", json, StringComparison.Ordinal);
        Assert.Contains("\"reasoningTokens\":7", json, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, json, StringComparison.Ordinal);

        InferenceTraceRecord? exported = JsonSerializer.Deserialize(
            json,
            TheForgeInferenceTracesJsonContext.Default.InferenceTraceRecord);
        InferenceTraceEventRecord exportedReasoning = Assert.Single(exported!.Events);
        Assert.Equal("Summary", exportedReasoning.ReasoningOutputMode);
        Assert.Equal(7, exportedReasoning.ReasoningTokens);
        Assert.Null(exportedReasoning.Data);
    }

    private sealed class ThrowingInferenceTraceStore : RetroDownfall.TheForge.Core.Services.IInferenceTraceStore
    {

        public string StorePath { get; } = Path.Combine(Path.GetTempPath(), "forge-throwing-traces.json");

        public Task<InferenceTraceStoreDocument> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InferenceTraceStoreDocument(1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []));

        public Task SaveAsync(InferenceTraceStoreDocument document, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("the-forge traces file is read-only"));

        public Task<InferenceTraceStoreDocument> UpdateAsync(
            Func<InferenceTraceStoreDocument, CancellationToken, Task<InferenceTraceStoreDocument>> update,
            CancellationToken cancellationToken = default) =>
            Task.FromException<InferenceTraceStoreDocument>(new IOException("the-forge traces file is read-only"));

    }

}
