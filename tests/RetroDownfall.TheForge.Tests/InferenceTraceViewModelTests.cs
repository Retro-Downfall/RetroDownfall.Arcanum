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
            ImmediateTheForgeLocalMutationRunner.Instance,
            new InMemoryInferenceTraceStore(),
            new NullArtifactFileDialogService());

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
            new InMemoryInferenceTraceStore(),
            new FixedPathArtifactFileDialogService(FixedPathArtifactFileDialogService.UnwritablePath()));

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
            new ThrowingInferenceTraceStore(),
            new NullArtifactFileDialogService());

        trace.BeginCapture("spell", "echo");

        await trace.PersistAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(trace.LastError));

        Assert.NotEqual("Trace saved locally.", trace.StatusText);
    }

    [Fact]
    public void DryRunButtons_WithoutHooks_SetHonestStatus()
    {
        InferenceTraceViewModel trace = new(
            ImmediateTheForgeLocalMutationRunner.Instance,
            new InMemoryInferenceTraceStore(),
            new NullArtifactFileDialogService());

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
            ImmediateTheForgeLocalMutationRunner.Instance,
            new InMemoryInferenceTraceStore(),
            new NullArtifactFileDialogService());
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

    /// <summary>
    /// A long agentic turn streams tens of thousands of token frames; the trace keeps the latest
    /// <see cref="InferenceTraceViewModel.MaxEntries"/> of them, as the Tome bounds its messages, and says
    /// that older events were dropped.
    /// </summary>
    [Fact]
    public void Capture_keeps_the_latest_entries_within_the_retention_bound()
    {
        InferenceTraceViewModel trace = new(
            ImmediateTheForgeLocalMutationRunner.Instance,
            new InMemoryInferenceTraceStore(),
            new NullArtifactFileDialogService());

        trace.BeginCapture("session", Guid.NewGuid().ToString("D"));

        int total = InferenceTraceViewModel.MaxEntries + 25;

        for (int i = 0; i < total; i++)
        {
            trace.Capture(new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, $"t{i}"));
        }

        Assert.Equal(InferenceTraceViewModel.MaxEntries, trace.Entries.Count);

        Assert.Equal("t25", trace.Entries[0].Data);

        Assert.Equal($"t{total - 1}", trace.Entries[^1].Data);

        Assert.Contains("dropped", trace.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Capture_caps_the_data_kept_for_one_event()
    {
        InferenceTraceViewModel trace = new(
            ImmediateTheForgeLocalMutationRunner.Instance,
            new InMemoryInferenceTraceStore(),
            new NullArtifactFileDialogService());

        trace.BeginCapture("session", Guid.NewGuid().ToString("D"));

        trace.Capture(new IntelligenceEvent(
            IntelligenceEventType.ToolResult,
            "ok",
            new string('x', InferenceTraceViewModel.MaxEntryDataChars * 4)));

        InferenceTraceEntryViewModel entry = Assert.Single(trace.Entries);

        Assert.NotNull(entry.Data);

        Assert.True(entry.Data!.Length <= InferenceTraceViewModel.MaxEntryDataChars + 64);

        Assert.Contains("truncated", entry.Data, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Capture_never_cuts_an_astral_character_in_half_so_the_trace_still_serializes()
    {
        InferenceTraceViewModel trace = new(
            ImmediateTheForgeLocalMutationRunner.Instance,
            new InMemoryInferenceTraceStore(),
            new NullArtifactFileDialogService());

        trace.BeginCapture("session", Guid.NewGuid().ToString("D"));

        // The emoji's surrogate pair straddles the cap: its high surrogate is the last kept char.
        string data = new string('x', InferenceTraceViewModel.MaxEntryDataChars - 1) + "\U0001F600" + new string('y', 32);

        trace.Capture(new IntelligenceEvent(IntelligenceEventType.ToolResult, "ok", data));

        InferenceTraceEntryViewModel entry = Assert.Single(trace.Entries);

        Assert.NotNull(entry.Data);

        int marker = entry.Data!.IndexOf('\u2026', StringComparison.Ordinal);

        Assert.True(marker > 0);

        Assert.False(char.IsHighSurrogate(entry.Data[marker - 1]));

        string json = trace.BuildExportJson();

        Assert.Contains("truncated", json, StringComparison.OrdinalIgnoreCase);
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
