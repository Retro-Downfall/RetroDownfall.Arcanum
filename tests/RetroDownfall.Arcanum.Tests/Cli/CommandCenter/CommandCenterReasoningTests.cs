using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterReasoningTests
{
    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Runner_streams_reasoning_into_separate_entry_and_keeps_answer_clean()
    {
        CommandCenterChatRunner runner = CreateRunner(new StaticNdjsonHandler(
            SerializeFrames(
                Reasoning("think "),
                Reasoning("carefully"),
                new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "answer"),
                new IntelligenceEvent(IntelligenceEventType.Result, "answer", "answer"))));
        CommandCenterState state = new(new SessionLogBuffer());
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        await runner.RunTurnAsync("question", state, updates.Writer, CancellationToken.None);

        IReadOnlyList<SessionLogEntry> entries = state.Log.Snapshot();
        Assert.Equal(
            [
                SessionLogEntryKind.User,
                SessionLogEntryKind.Reasoning,
                SessionLogEntryKind.Assistant,
            ],
            entries.Select(static entry => entry.Kind));
        Assert.Equal("think carefully", entries[1].Text);
        Assert.Equal("answer", entries[2].Text);
        Assert.DoesNotContain("think", entries[2].Text, StringComparison.Ordinal);
        Assert.False(state.ThinkingActive);
        Assert.All(entries, static entry => Assert.False(entry.Streaming));
    }

    [Fact]
    public async Task Runner_retains_latest_context_breakdown_for_mana_surfaces()
    {
        ContextTokenBreakdown breakdown = new()
        {
            Provider = "provider",
            Model = "model",
            Profile = new ResolvedModelTokenizationProfile
            {
                ProfileId = "fallback:test",
                Type = ModelTokenizationProfileType.UnknownFallback,
                TokenizerId = "o200k_base",
                SafetyMarginPercent = 15,
                PerMessageOverheadTokens = 4,
                PerToolOverheadTokens = 8,
                ProviderFramingTokens = 3,
                StopTokenOverheadTokens = 1,
                UnknownImageReserveTokens = 2048,
                Confidence = 0.5,
            },
            Components = [],
            InputTokens = 100,
            ReservedTokens = 32,
            TotalTokens = 132,
            OverallClassification = TokenEstimateClassification.Estimated,
            SafetyMarginTokens = 10,
            ProviderReportedInputTokens = 107,
            EstimationVarianceTokens = 7,
        };
        CommandCenterChatRunner runner = CreateRunner(new StaticNdjsonHandler(
            SerializeFrames(
                new IntelligenceEvent(
                    IntelligenceEventType.Context,
                    "Context token accounting",
                    ContextBreakdown: breakdown),
                new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "answer"),
                new IntelligenceEvent(IntelligenceEventType.Result, "answer", "answer"))));
        CommandCenterState state = new(new SessionLogBuffer());
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        await runner.RunTurnAsync("question", state, updates.Writer, CancellationToken.None);

        Assert.NotNull(state.LastContextBreakdown);
        Assert.Equal(breakdown.InputTokens, state.LastContextBreakdown!.InputTokens);
        Assert.Equal(breakdown.Profile, state.LastContextBreakdown.Profile);
        Assert.Equal(breakdown.ProviderReportedInputTokens, state.LastContextBreakdown.ProviderReportedInputTokens);
        Assert.Contains("Estimated", state.SidebarText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            state.Log.Snapshot(),
            static entry => entry.Text.Contains("Context token accounting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runner_coalesces_high_delta_reasoning_into_one_transcript_entry()
    {
        IntelligenceEvent[] frames =
        [
            .. Enumerable.Range(0, 5_000).Select(static _ => Reasoning("0123456789")),
            new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "answer"),
            new IntelligenceEvent(IntelligenceEventType.Result, "answer", "answer"),
        ];
        CommandCenterChatRunner runner = CreateRunner(new StaticNdjsonHandler(SerializeFrames(frames)));
        CommandCenterState state = new(new SessionLogBuffer(maxReasoningChars: 256));
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        await runner.RunTurnAsync("question", state, updates.Writer, CancellationToken.None);

        SessionLogEntry reasoning = Assert.Single(
            state.Log.Snapshot(),
            static entry => entry.Kind == SessionLogEntryKind.Reasoning);
        Assert.True(reasoning.Text.Length <= 256);
        Assert.EndsWith(SessionLogBuffer.ReasoningTruncationMarker, reasoning.Text, StringComparison.Ordinal);
        List<CommandCenterUiUpdate> emitted = [];
        while (updates.Reader.TryRead(out CommandCenterUiUpdate? update))
        {
            emitted.Add(update);
        }

        Assert.True(
            emitted.Count(static update => update.Kind == CommandCenterUiUpdateKind.RefreshLog) < 25,
            $"Expected coalesced refreshes, got {emitted.Count} total updates.");
    }

    [Fact]
    public async Task First_reasoning_updates_the_actual_window_header_once_and_refreshes_log()
    {
        CommandCenterChatRunner runner = CreateRunner(new StaticNdjsonHandler(
            SerializeFrames(Reasoning("first"), Reasoning(" second"))));
        CommandCenterState state = new(new SessionLogBuffer());
        CommandCenterWindow window = new();
        window.ApplyAbsoluteLayout(120, 40);
        ObservingUiWriter updates = new(state, window);

        await runner.RunTurnAsync("question", state, updates, CancellationToken.None);

        ObservedUiUpdate initial = updates.Updates[0];
        Assert.Equal(CommandCenterUiUpdateKind.RefreshAll, initial.Kind);
        Assert.True(initial.ThinkingVisible);
        Assert.Equal(
            1,
            updates.Updates.Count(static update =>
                update.Kind == CommandCenterUiUpdateKind.RefreshHeader));
        Assert.Contains(
            updates.Updates,
            static update =>
                update.Kind == CommandCenterUiUpdateKind.RefreshHeader
                && !update.ThinkingVisible);
        Assert.Contains(
            updates.Updates,
            static update =>
                update.Kind == CommandCenterUiUpdateKind.RefreshLog
                && update.ReasoningEntries == 1);
        Assert.False(window.ThinkingLabel.Visible);
        Assert.Single(
            state.Log.Snapshot(),
            static entry => entry.Kind == SessionLogEntryKind.Reasoning);
    }

    [Fact]
    public async Task First_token_updates_the_actual_window_header_once_and_hides_spinner()
    {
        CommandCenterChatRunner runner = CreateRunner(new StaticNdjsonHandler(
            SerializeFrames(
                new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "first"),
                new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, " second\n"))));
        CommandCenterState state = new(new SessionLogBuffer());
        CommandCenterWindow window = new();
        window.ApplyAbsoluteLayout(120, 40);
        ObservingUiWriter updates = new(state, window);

        await runner.RunTurnAsync("question", state, updates, CancellationToken.None);

        Assert.True(updates.Updates[0].ThinkingVisible);
        Assert.Equal(
            1,
            updates.Updates.Count(static update =>
                update.Kind == CommandCenterUiUpdateKind.RefreshHeader));
        Assert.False(window.ThinkingLabel.Visible);
    }

    [Fact]
    public void Reasoning_refresh_preserves_a_scrolled_transcript_viewport()
    {
        SessionLogBuffer log = new();
        SessionLogEntry first = log.Append(SessionLogEntryKind.Assistant, "entry 0");
        for (int i = 1; i < 30; i++)
        {
            log.Append(SessionLogEntryKind.Assistant, $"entry {i}");
        }

        CommandCenterState state = new(log);
        CommandCenterWindow window = new();
        window.ApplyAbsoluteLayout(120, 24);
        window.ApplyState(state);
        window.LogView.SelectedItem = 10;
        int selectedBefore = window.LogView.SelectedItem ?? 0;

        _ = log.InsertBefore(first, SessionLogEntryKind.Reasoning, "live reasoning");
        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshLog);

        Assert.True(
            window.LogView.SelectedItem > selectedBefore,
            $"Expected the selected transcript anchor to move past inserted reasoning; remained at {selectedBefore}.");
    }

    [Fact]
    public void Transcript_refresh_preserves_line_offset_inside_a_multiline_entry()
    {
        SessionLogBuffer log = new();
        SessionLogEntry first = log.Append(SessionLogEntryKind.Assistant, "before");
        SessionLogEntry target = log.Append(
            SessionLogEntryKind.Assistant,
            "target line zero\ntarget line one\ntarget line two\ntarget line three");
        _ = log.Append(SessionLogEntryKind.Assistant, "after");

        System.Collections.ObjectModel.ObservableCollection<string> oldLines = [];
        List<Guid?> oldAnchors = [];
        log.CopyLinesTo(oldLines, oldAnchors, wrapWidth: 200);
        int[] oldTargetLines = oldAnchors
            .Select((anchor, index) => (anchor, index))
            .Where(candidate => candidate.anchor == target.Id)
            .Select(static candidate => candidate.index)
            .ToArray();
        Assert.True(oldTargetLines.Length >= 3);
        const int selectedLineOffset = 2;
        string selectedLine = oldLines[oldTargetLines[selectedLineOffset]];

        CommandCenterState state = new(log);
        CommandCenterWindow window = new();
        window.ApplyAbsoluteLayout(120, 24);
        window.ApplyState(state);
        window.LogView.SelectedItem = oldTargetLines[selectedLineOffset];

        _ = log.InsertBefore(first, SessionLogEntryKind.Reasoning, "inserted reasoning");
        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshLog);

        System.Collections.ObjectModel.ObservableCollection<string> newLines = [];
        List<Guid?> newAnchors = [];
        log.CopyLinesTo(newLines, newAnchors, wrapWidth: 200);
        int[] newTargetLines = newAnchors
            .Select((anchor, index) => (anchor, index))
            .Where(candidate => candidate.anchor == target.Id)
            .Select(static candidate => candidate.index)
            .ToArray();

        Assert.Equal(newTargetLines[selectedLineOffset], window.LogView.SelectedItem);
        Assert.Equal(selectedLine, newLines[window.LogView.SelectedItem!.Value]);
    }

    [Fact]
    public async Task First_reasoning_stops_spinner_and_cancellation_completes_streaming_entries()
    {
        BlockingAfterPayloadStream stream = new(
            Encoding.UTF8.GetBytes(SerializeFrames(Reasoning("partial thought"))));
        CommandCenterChatRunner runner = CreateRunner(new StreamingHandler(stream));
        CommandCenterState state = new(new SessionLogBuffer());
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        using CancellationTokenSource cancellation = new();

        Task run = runner.RunTurnAsync("question", state, updates.Writer, cancellation.Token);
        await WaitUntilAsync(
            () => state.Log.Snapshot().Any(static entry => entry.Kind == SessionLogEntryKind.Reasoning),
            AsyncTestTimeout);

        Assert.False(state.ThinkingActive);
        cancellation.Cancel();
        await run.WaitAsync(AsyncTestTimeout);

        IReadOnlyList<SessionLogEntry> entries = state.Log.Snapshot();
        Assert.All(entries, static entry => Assert.False(entry.Streaming));
        Assert.Equal(
            "partial thought",
            Assert.Single(entries, static entry => entry.Kind == SessionLogEntryKind.Reasoning).Text);
        Assert.Contains(
            "cancelled",
            Assert.Single(entries, static entry => entry.Kind == SessionLogEntryKind.Assistant).Text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Error_after_reasoning_cleans_up_without_leaking_reasoning_into_answer()
    {
        CommandCenterChatRunner runner = CreateRunner(new StaticNdjsonHandler(
            SerializeFrames(
                Reasoning("private summary"),
                new IntelligenceEvent(IntelligenceEventType.Error, "failed"))));
        CommandCenterState state = new(new SessionLogBuffer());
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        await runner.RunTurnAsync("question", state, updates.Writer, CancellationToken.None);

        IReadOnlyList<SessionLogEntry> entries = state.Log.Snapshot();
        Assert.False(state.ThinkingActive);
        Assert.All(entries, static entry => Assert.False(entry.Streaming));
        Assert.Equal(
            "private summary",
            Assert.Single(entries, static entry => entry.Kind == SessionLogEntryKind.Reasoning).Text);
        Assert.DoesNotContain(
            "private summary",
            Assert.Single(entries, static entry => entry.Kind == SessionLogEntryKind.Assistant).Text,
            StringComparison.Ordinal);
        Assert.Equal(
            "failed",
            Assert.Single(entries, static entry => entry.Kind == SessionLogEntryKind.Error).Text);
    }

    private static IntelligenceEvent Reasoning(string text) =>
        new(
            IntelligenceEventType.Reasoning,
            text,
            Reasoning: new ReasoningContentSegment(text, ReasoningOutputMode.Summary));

    private static string SerializeFrames(params IntelligenceEvent[] frames) =>
        string.Join(
            '\n',
            frames.Select(static frame =>
                JsonSerializer.Serialize(frame, ArcanumJsonContext.Default.IntelligenceEvent)))
        + "\n";

    private static CommandCenterChatRunner CreateRunner(HttpMessageHandler handler)
    {
        ArcanumApiClient client = new(new FakeHttpClientFactory(handler), new FakeSecretStore());
        SessionWorkspaceService workspace = new(
            client,
            new NoopLastSessionStore(),
            NullLogger<SessionWorkspaceService>.Instance);
        CommandCenterHardModalArbiter arbiter = new();
        return new CommandCenterChatRunner(
            client,
            new StaticOptionsMonitor(new ArcanumSettings()),
            workspace,
            new CommandCenterWardCoordinator(arbiter),
            new CommandCenterHumanPromptCoordinator(client, arbiter),
            NullLogger<CommandCenterChatRunner>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class StaticOptionsMonitor(ArcanumSettings settings) : IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; } = settings;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }

    private sealed class NoopLastSessionStore : ILastSessionStore
    {
        public Guid? GetLastSessionId() => null;

        public void SaveSessionId(Guid id)
        {
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class StaticNdjsonHandler(string ndjson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
            });
    }

    private sealed record ObservedUiUpdate(
        CommandCenterUiUpdateKind Kind,
        bool ThinkingVisible,
        int ReasoningEntries);

    private sealed class ObservingUiWriter(
        CommandCenterState state,
        CommandCenterWindow window) : ChannelWriter<CommandCenterUiUpdate>
    {
        public List<ObservedUiUpdate> Updates { get; } = [];

        public override bool TryComplete(Exception? error = null) => true;

        public override bool TryWrite(CommandCenterUiUpdate item)
        {
            window.ApplyState(state, kind: item.Kind);
            Updates.Add(new ObservedUiUpdate(
                item.Kind,
                window.ThinkingLabel.Visible,
                state.Log.Snapshot().Count(static entry =>
                    entry.Kind == SessionLogEntryKind.Reasoning)));

            return true;
        }

        public override ValueTask<bool> WaitToWriteAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    private sealed class StreamingHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            });
    }

    private sealed class BlockingAfterPayloadStream(byte[] payload) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override int Read(Span<byte> buffer)
        {
            if (_position >= payload.Length)
            {
                throw new NotSupportedException("Use async reads after the payload.");
            }

            int count = Math.Min(buffer.Length, payload.Length - _position);
            payload.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < payload.Length)
            {
                await Task.Yield();
                return Read(buffer.Span);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
