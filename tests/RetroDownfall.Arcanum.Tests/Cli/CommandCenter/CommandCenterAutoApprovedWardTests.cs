using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// Issue #219: record-only Ward frames remain visible in Incantations, but the Command Center never
/// waits for them or submits an operator resolution.
/// </summary>
public sealed class CommandCenterWardRecordTests
{
    [Fact]
    public void Ward_argument_preview_remains_bounded_for_informational_records()
    {

        string payload = "{\"command\":\"" + new string('x', 300) + "\"}";

        using JsonDocument document = JsonDocument.Parse(payload);

        string preview = CommandCenterChatRunner.FormatWardArgumentsPreview(
            document.RootElement,
            maxChars: 40);

        Assert.True(preview.Length <= 41);

        Assert.EndsWith("…", preview);

    }

    [Theory]
    [InlineData(WardResolutionOrigin.Human)]
    [InlineData(null)]
    public async Task Legacy_or_originless_Ward_frames_are_informational(
        WardResolutionOrigin? origin)
    {

        RecordingHandler handler = new(
            SerializeFrames(
                new IntelligenceEvent(
                    IntelligenceEventType.Warded,
                    "write_file",
                    WardId: "ward-legacy-1",
                    WardToolName: "write_file",
                    WardOrigin: origin),
                new IntelligenceEvent(
                    IntelligenceEventType.WardResolved,
                    "write_file",
                    WardId: "ward-legacy-1",
                    WardToolName: "write_file",
                    WardAllowed: true,
                    WardOrigin: origin),
                new IntelligenceEvent(IntelligenceEventType.Result, "done", "done")));

        CommandCenterChatRunner runner = CreateRunner(handler);

        CommandCenterState state = new(new SessionLogBuffer());

        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        await runner.RunTurnAsync("write it", state, updates.Writer, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(handler.Requests);

        Assert.DoesNotContain(
            handler.Requests,
            static path => path.Contains("api/wards/", StringComparison.Ordinal));

        IncantationRecord record = Assert.Single(state.Incantations.Snapshot());

        Assert.Contains(
            record.WardNotes,
            static note => note.Contains("Ward recorded", StringComparison.Ordinal));

        Assert.DoesNotContain(
            state.Log.Snapshot(),
            static entry => entry.Kind == SessionLogEntryKind.Error);

    }

    [Fact]
    public async Task Record_only_workspace_memory_and_Covenant_frames_never_block_or_post_a_resolution()
    {

        RecordingHandler handler = new(
            SerializeFrames(
                new IntelligenceEvent(
                    IntelligenceEventType.Warded,
                    "apply_patch",
                    WardId: "ward-record-1",
                    WardToolName: "apply_patch",
                    WardOrigin: WardResolutionOrigin.Ungated),
                new IntelligenceEvent(
                    IntelligenceEventType.WardResolved,
                    "apply_patch",
                    WardId: "ward-record-1",
                    WardToolName: "apply_patch",
                    WardAllowed: true,
                    WardOrigin: WardResolutionOrigin.Ungated),
                new IntelligenceEvent(
                    IntelligenceEventType.Warded,
                    "scribe_lexicon",
                    WardId: "ward-record-2",
                    WardToolName: "scribe_lexicon",
                    WardOrigin: WardResolutionOrigin.Ungated),
                new IntelligenceEvent(
                    IntelligenceEventType.WardResolved,
                    "scribe_lexicon",
                    WardId: "ward-record-2",
                    WardToolName: "scribe_lexicon",
                    WardAllowed: true,
                    WardOrigin: WardResolutionOrigin.Ungated),
                new IntelligenceEvent(
                    IntelligenceEventType.Warded,
                    "retire_covenant",
                    WardId: "ward-record-3",
                    WardToolName: "retire_covenant",
                    WardOrigin: WardResolutionOrigin.Ungated),
                new IntelligenceEvent(
                    IntelligenceEventType.WardResolved,
                    "retire_covenant",
                    WardId: "ward-record-3",
                    WardToolName: "retire_covenant",
                    WardAllowed: true,
                    WardOrigin: WardResolutionOrigin.Ungated),
                new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "done"),
                new IntelligenceEvent(IntelligenceEventType.Result, "done", "done")));

        CommandCenterChatRunner runner = CreateRunner(handler);
        CommandCenterState state = new(new SessionLogBuffer());
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        await runner.RunTurnAsync("patch it", state, updates.Writer, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(handler.Requests);

        Assert.DoesNotContain(
            handler.Requests,
            static path => path.Contains("api/wards/", StringComparison.Ordinal));

        IncantationRecord[] records = [.. state.Incantations.Snapshot()];

        Assert.Equal(
            ["apply_patch", "retire_covenant", "scribe_lexicon"],
            records
                .Select(static record => record.ToolName)
                .OrderBy(static toolName => toolName, StringComparer.Ordinal));

        Assert.All(
            records,
            static record => Assert.Contains(
                record.WardNotes,
                note => note.Contains("recorded", StringComparison.OrdinalIgnoreCase)
                    && note.Contains("ungated", StringComparison.OrdinalIgnoreCase)
                    && note.Contains(record.ToolName, StringComparison.Ordinal)));

        Assert.All(
            records,
            static record => Assert.Contains(
                record.WardNotes,
                note => note.Contains("resolved", StringComparison.OrdinalIgnoreCase)
                    && note.Contains("allowed", StringComparison.OrdinalIgnoreCase)
                    && note.Contains("ungated", StringComparison.OrdinalIgnoreCase)
                    && note.Contains(record.ToolName, StringComparison.Ordinal)));

        Assert.DoesNotContain(
            state.Log.Snapshot(),
            static entry => entry.Kind == SessionLogEntryKind.Error);

    }

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

        return new CommandCenterChatRunner(
            client,
            new StaticOptionsMonitor(new ArcanumSettings()),
            workspace,
            new CommandCenterHumanPromptCoordinator(client, new CommandCenterHardModalArbiter()),
            NullLogger<CommandCenterChatRunner>.Instance);

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

        public Task<ArcanumClientMutationResult<CliContextDocument>>
            SaveSessionIdAsync(
                Guid id,
                Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                ArcanumClientMutationResult<CliContextDocument>.Completed(
                    CliContextDocument.Empty with { SessionId = id }));
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

    private sealed class RecordingHandler(string ndjson) : HttpMessageHandler
    {

        private readonly ConcurrentQueue<string> _requests = new();

        public IReadOnlyCollection<string> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            _requests.Enqueue(request.RequestUri?.AbsolutePath ?? string.Empty);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
            });

        }

    }

}
