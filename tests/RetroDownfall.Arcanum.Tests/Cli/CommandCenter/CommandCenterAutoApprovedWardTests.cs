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
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// Issue #53: a server-side auto-approved Ward must surface in the transcript without opening the
/// blocking Ward modal and without racing the server with a manual <c>POST /api/wards/{id}</c>.
/// </summary>
public sealed class CommandCenterAutoApprovedWardTests
{

    [Fact]
    public async Task An_auto_approved_ward_neither_opens_the_modal_nor_posts_a_resolution()
    {

        RecordingHandler handler = new(
            SerializeFrames(
                new IntelligenceEvent(
                    IntelligenceEventType.Warded,
                    "apply_patch",
                    WardId: "ward-auto-1",
                    WardToolName: "apply_patch",
                    WardOrigin: WardResolutionOrigin.AutoApproved),
                new IntelligenceEvent(
                    IntelligenceEventType.WardResolved,
                    "apply_patch",
                    WardId: "ward-auto-1",
                    WardToolName: "apply_patch",
                    WardAllowed: true,
                    WardOrigin: WardResolutionOrigin.AutoApproved),
                new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "done"),
                new IntelligenceEvent(IntelligenceEventType.Result, "done", "done")));

        CommandCenterHardModalArbiter arbiter = new();
        CommandCenterWardCoordinator coordinator = new(arbiter);
        int shown = 0;
        coordinator.SetUiCallbacks(_ => Interlocked.Increment(ref shown), null);

        CommandCenterChatRunner runner = CreateRunner(handler, coordinator);
        CommandCenterState state = new(new SessionLogBuffer());
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        await runner.RunTurnAsync("patch it", state, updates.Writer, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, shown);

        Assert.Null(coordinator.PendingRequest);

        Assert.DoesNotContain(
            handler.Requests,
            static path => path.Contains("api/wards/", StringComparison.Ordinal));

        IncantationRecord record = Assert.Single(state.Incantations.Snapshot());

        Assert.Contains(
            record.WardNotes,
            static note => note.Contains("Auto-approved", StringComparison.OrdinalIgnoreCase));

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

    private static CommandCenterChatRunner CreateRunner(
        HttpMessageHandler handler,
        CommandCenterWardCoordinator coordinator)
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
            coordinator,
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
