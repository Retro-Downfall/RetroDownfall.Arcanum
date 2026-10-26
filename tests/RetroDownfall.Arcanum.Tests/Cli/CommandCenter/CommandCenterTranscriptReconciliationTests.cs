using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterTranscriptReconciliationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Durable_result_adopts_selectable_ids_and_count_without_erasing_diagnostics_or_anchors()
    {
        await using Harness harness = new();
        EntryDto prior = harness.Entry("user", "same text");
        EntryDto answer = harness.Entry("assistant", "durable answer");
        harness.Http.Entries = [answer, prior];
        harness.State.Log.ReplaceWithApiHistory([(SessionLogEntryKind.User, prior.Content!, prior.Id)], false);
        Guid anchor = Assert.Single(harness.State.Log.Snapshot()).Id;
        harness.State.LoadedTranscriptEntries = [prior];
        harness.State.Log.Append(SessionLogEntryKind.Error, "keep warning");
        harness.State.LastError = "keep warning";
        await harness.SubmitAsync();
        Assert.Equal(2, harness.State.SessionEntryCount);
        Assert.Equal([prior.Id, answer.Id], harness.State.LoadedTranscriptEntries.Select(entry => entry.Id));
        Assert.Equal(anchor, Assert.Single(harness.State.Log.Snapshot(), entry => entry.SourceEntryId == prior.Id).Id);
        Assert.Equal("durable answer", Assert.Single(harness.State.Log.Snapshot(), entry => entry.SourceEntryId == answer.Id).Text);
        Assert.Contains("keep warning", harness.State.Log.RenderPlainText(), StringComparison.Ordinal);
        Assert.Equal("keep warning", harness.State.LastError);
        Assert.DoesNotContain(harness.State.Log.Snapshot(), entry => entry.Kind == SessionLogEntryKind.Assistant && entry.SourceEntryId is null);
        Assert.Equal(1, harness.Http.AskCount);
    }

    [Fact]
    public async Task New_binding_and_lookahead_make_older_history_available_after_live_turn()
    {
        await using Harness harness = new();
        harness.State.ClearSessionBinding();
        harness.Http.BindSession = true;
        harness.Http.Entries = Enumerable.Range(0, 201).Select(i => harness.Entry("assistant", $"persisted {i}")).ToArray();
        await harness.SubmitAsync();
        Assert.Equal(harness.Http.SessionId, harness.State.SessionId);
        Assert.Equal(201, harness.State.SessionEntryCount);
        Assert.Equal(200, harness.State.LoadedTranscriptEntries.Count);
        Assert.True(harness.State.HasOlderTranscriptEntries);
        Assert.False(harness.State.HasNewerTranscriptEntries);
        Assert.Contains(harness.State.Log.Snapshot(), entry => entry.SourceEntryId == harness.Http.Entries[0].Id);
    }

    [Fact]
    public async Task Historical_page_keeps_its_anchor_and_selection_while_newest_metadata_advances()
    {
        await using Harness harness = new();
        EntryDto old = harness.Entry("assistant", "older selected answer");
        harness.State.LoadedTranscriptEntries = [old];
        harness.State.HasNewerTranscriptEntries = true;
        harness.State.SelectedTranscriptEntryId = old.Id;
        harness.State.Log.ReplaceWithApiHistory([(SessionLogEntryKind.Assistant, old.Content!, old.Id)], true, true);
        Guid anchor = Assert.Single(harness.State.Log.Snapshot(), entry => entry.SourceEntryId == old.Id).Id;
        await harness.SubmitAsync();
        Assert.Equal(harness.Http.Entries.Length, harness.State.SessionEntryCount);
        Assert.Equal(old.Id, Assert.Single(harness.State.LoadedTranscriptEntries).Id);
        Assert.Equal(old.Id, harness.State.SelectedTranscriptEntryId);
        Assert.Equal(anchor, Assert.Single(harness.State.Log.Snapshot(), entry => entry.SourceEntryId == old.Id).Id);
        Assert.True(harness.State.HasNewerTranscriptEntries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_error_result_retains_provisional_output_without_snapshot_adoption(bool error)
    {
        await using Harness harness = new();
        harness.Http.Terminal = error ? IntelligenceEventType.Error : null;
        await harness.SubmitAsync();
        Assert.Equal(0, harness.Http.DetailCount);
        Assert.Empty(harness.State.LoadedTranscriptEntries);
        Assert.Contains(harness.State.Log.Snapshot(), entry => entry.Kind == SessionLogEntryKind.Assistant && entry.SourceEntryId is null && entry.Text.Contains("live answer", StringComparison.Ordinal));
        Assert.Contains("/resume", harness.State.Log.RenderPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_local_binding_does_not_adopt_an_unaccepted_remote_snapshot()
    {
        await using Harness harness = new();
        harness.Http.BindSession = true;
        harness.Store.Refuse = true;
        await harness.SubmitAsync();
        Assert.Equal(0, harness.Http.DetailCount);
        Assert.Empty(harness.State.LoadedTranscriptEntries);
        Assert.Contains("Warning:", harness.State.LastError, StringComparison.Ordinal);
        Assert.Contains(harness.State.Log.Snapshot(), entry => entry.Kind == SessionLogEntryKind.Assistant && entry.SourceEntryId is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_changed_visit_rejects_held_newest_snapshot_including_A_B_A(bool returnToA)
    {
        await using Harness harness = new();
        harness.Http.HoldEntries = true;
        Task turn = harness.SubmitAsync();
        await harness.Http.EntriesEntered.Task.WaitAsync(Deadline);
        Guid next = Guid.NewGuid();
        harness.State.SessionId = next;
        if (returnToA)
        {
            harness.State.SessionId = harness.Http.SessionId;
        }
        harness.State.SessionTitle = "new visit";
        harness.Http.ReleaseEntries.TrySetResult();
        await turn.WaitAsync(Deadline);
        Assert.Equal(returnToA ? harness.Http.SessionId : next, harness.State.SessionId);
        Assert.Equal("new visit", harness.State.SessionTitle);
        Assert.Empty(harness.State.LoadedTranscriptEntries);
    }

    [Fact]
    public async Task Scrolled_latest_page_retains_wrapped_selection_focus_and_view_when_new_rows_evict_its_anchor()
    {
        await using Harness harness = new();
        EntryDto selected = harness.Entry("assistant", string.Join(' ', Enumerable.Repeat("selected wrapped answer", 80)));
        harness.State.LoadedTranscriptEntries = [selected];
        harness.State.Log.ReplaceWithApiHistory([(SessionLogEntryKind.Assistant, selected.Content!, selected.Id)], false);
        harness.Window.ApplyState(harness.State);
        harness.Window.ScrollLogHome();
        harness.Window.ScrollLogDown(2);
        harness.Window.FocusLog();
        harness.State.FocusRegion = CommandCenterFocusRegion.Transcript;
        int? line = harness.Window.LogView.SelectedItem;
        Assert.False(harness.Window.FollowTail);
        harness.Http.Entries = Enumerable.Range(0, 201).Select(i => harness.Entry("assistant", $"new {i}")).ToArray();
        await harness.SubmitAsync();
        harness.Pump();
        Assert.Equal(selected.Id, Assert.Single(harness.State.LoadedTranscriptEntries).Id);
        Assert.False(harness.Window.FollowTail);
        Assert.Equal(line, harness.Window.LogView.SelectedItem);
        Assert.Equal(CommandCenterFocusRegion.Transcript, harness.State.FocusRegion);
        Assert.True(harness.State.HasNewerTranscriptEntries);
    }

    [Fact]
    public async Task Cancelled_stream_retains_partial_output_while_server_cleanup_is_delayed_and_never_reads_a_snapshot()
    {
        await using Harness harness = new();
        harness.Http.HoldStream = true;
        Task turn = harness.SubmitAsync();
        await harness.Http.StreamWaiting.Task.WaitAsync(Deadline);
        Assert.True(harness.State.TryCancelTurn());
        await turn.WaitAsync(Deadline);
        // The server's durable cleanup is still outstanding. A later persisted row is not evidence
        // available to the cancelled client and must not be guessed from matching text.
        harness.Http.Entries = [harness.Entry("assistant", "live answer")];
        Assert.Equal(0, harness.Http.DetailCount);
        Assert.Empty(harness.State.LoadedTranscriptEntries);
        Assert.Contains(harness.State.Log.Snapshot(), entry => entry.Kind == SessionLogEntryKind.Assistant
            && entry.SourceEntryId is null && entry.Text.Contains("cancelled", StringComparison.Ordinal));
        Assert.Contains("/resume", harness.State.Log.RenderPlainText(), StringComparison.Ordinal);
        Assert.False(harness.State.Generating);
        Assert.Null(harness.State.TurnCts);
    }

    [Fact]
    public async Task Existing_incantation_and_ward_notes_survive_and_history_is_not_duplicated_on_refresh()
    {
        await using Harness harness = new();
        IncantationRecord live = harness.State.Incantations.UpsertCall("live-call", "read_file", "{}");
        harness.State.Incantations.UpsertError("live-call", "read_file", "retained failure");
        harness.State.Incantations.AppendWardNote("read_file", "retained Ward note");
        harness.Http.Entries = [
            harness.Entry("assistant", "answer"),
            harness.Entry("tool", "stored replacement") with { ToolCallId = "live-call", ToolName = "read_file" },
            harness.Entry("tool", "historic result") with { ToolCallId = "history-call", ToolName = "search" },
        ];
        await harness.SubmitAsync();
        await harness.SubmitAsync();
        IncantationRecord retained = Assert.Single(harness.State.Incantations.Snapshot(), entry => entry.CallId == "live-call");
        Assert.Same(live, retained);
        Assert.Equal(IncantationState.Failed, retained.State);
        Assert.Contains("retained Ward note", retained.WardNotes);
        Assert.Single(harness.State.Incantations.Snapshot(), entry => entry.CallId == "history-call");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refused_or_wrong_session_snapshot_preserves_live_output_and_warning(bool wrongSession)
    {
        await using Harness harness = new();
        harness.Http.RefuseEntries = !wrongSession;
        if (wrongSession)
        {
            harness.Http.Entries = [harness.Entry("assistant", "wrong response") with { SessionId = Guid.NewGuid() }];
        }
        harness.State.LastError = "original warning";
        await harness.SubmitAsync();
        Assert.Empty(harness.State.LoadedTranscriptEntries);
        Assert.Contains(harness.State.Log.Snapshot(), entry => entry.Kind == SessionLogEntryKind.Assistant && entry.SourceEntryId is null);
        Assert.Equal("original warning", harness.State.LastError);
        Assert.Contains("/resume", harness.State.Log.RenderPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_the_post_result_snapshot_keeps_the_live_answer_and_recovery_guidance()
    {
        await using Harness harness = new();
        harness.Http.HoldEntries = true;
        Task turn = harness.SubmitAsync();
        await harness.Http.EntriesEntered.Task.WaitAsync(Deadline);
        Assert.True(harness.State.TryCancelTurn());
        await turn.WaitAsync(Deadline);
        Assert.Empty(harness.State.LoadedTranscriptEntries);
        Assert.Contains(harness.State.Log.Snapshot(), entry => entry.Kind == SessionLogEntryKind.Assistant && entry.SourceEntryId is null);
        Assert.Contains("/resume", harness.State.Log.RenderPlainText(), StringComparison.Ordinal);
        Assert.False(harness.State.Generating);
        Assert.Null(harness.State.TurnCts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_live_answer_is_immediately_selectable_by_the_existing_fork_consumers(bool alternative)
    {
        await using Harness harness = new();
        EntryDto prompt = harness.Entry("user", "persisted original prompt");
        EntryDto answer = harness.Entry("assistant", "persisted final answer");
        harness.Http.Entries = [answer, prompt];
        await harness.SubmitAsync();
        harness.Window.ApplyState(harness.State);
        for (int line = 0; line < harness.Window.LogView.Source!.Count; line++)
        {
            harness.Window.LogView.SelectedItem = line;
            if (harness.Window.GetSelectedTranscriptEntryId(harness.State) == answer.Id)
            {
                break;
            }
        }
        harness.State.SelectedTranscriptEntryId = harness.Window.GetSelectedTranscriptEntryId(harness.State);
        Assert.Equal(answer.Id, harness.State.SelectedTranscriptEntryId);
        await harness.DispatchAsync(alternative ? "/fork alternative" : "/fork at");
        Assert.Equal(alternative ? null : answer.Id, harness.Http.ForkRequest?.UpToEntryId);
        Assert.Equal(alternative ? prompt.Id : null, harness.Http.ForkRequest?.BeforeEntryId);
        Assert.Equal(1, harness.Http.ForkCount);
        Assert.Equal(1, harness.Http.AskCount);
    }

    [Fact]
    public async Task Legacy_history_replay_cannot_settle_an_unrelated_live_call_or_invent_a_call_identity()
    {
        await using Harness harness = new();
        IncantationRecord live = harness.State.Incantations.UpsertCall("unrelated-live", "read_file", "{}");
        harness.Http.Entries = [
            harness.Entry("assistant", "answer"),
            harness.Entry("system", "[ToolResult: historic outcome]"),
            harness.Entry("assistant", "[ToolCall: read_file({})]"),
        ];
        await harness.SubmitAsync();
        await harness.SubmitAsync();
        Assert.Equal(IncantationState.Pending, live.State);
        Assert.Same(live, Assert.Single(harness.State.Incantations.Snapshot()));
    }

    [Fact]
    public async Task A_verified_persisted_result_uses_its_call_id_instead_of_the_latest_pending_call()
    {
        await using Harness harness = new();
        IncantationRecord live = harness.State.Incantations.UpsertCall("unrelated-live", "read_file", "{}");
        harness.Http.Entries = [
            harness.Entry("assistant", "answer"),
            harness.Entry("system", "[ToolResult: verified outcome]") with { ToolCallId = "verified-history" },
        ];
        await harness.SubmitAsync();
        Assert.Equal(IncantationState.Pending, live.State);
        IncantationRecord historical = Assert.Single(harness.State.Incantations.Snapshot(), entry => entry.CallId == "verified-history");
        Assert.Equal("verified outcome", historical.ResultText);
        Assert.Equal(IncantationState.Succeeded, historical.State);
    }

    [Fact]
    public async Task Scrolling_during_a_held_snapshot_preserves_the_current_view_not_only_the_pre_turn_view()
    {
        await using Harness harness = new();
        EntryDto old = harness.Entry("assistant", string.Join(' ', Enumerable.Repeat("old anchored row", 80)));
        harness.State.LoadedTranscriptEntries = [old];
        harness.State.Log.ReplaceWithApiHistory([(SessionLogEntryKind.Assistant, old.Content!, old.Id)], false);
        harness.Window.ApplyState(harness.State);
        Assert.True(harness.Window.FollowTail);
        harness.Http.HoldEntries = true;
        Task turn = harness.SubmitAsync();
        await harness.Http.EntriesEntered.Task.WaitAsync(Deadline);
        harness.Window.ScrollLogHome();
        Assert.False(harness.Window.FollowTail);
        harness.Http.ReleaseEntries.TrySetResult();
        await turn.WaitAsync(Deadline);
        harness.Pump();
        Assert.Equal(old.Id, Assert.Single(harness.State.LoadedTranscriptEntries).Id);
        Assert.True(harness.State.HasNewerTranscriptEntries);
        Assert.False(harness.Window.FollowTail);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private readonly Terminal.Gui.App.IApplication _app;
        public CommandCenterWindow Window { get; } = new();
        private readonly CancellationTokenSource _lifetime = new();
        public HttpFixture Http { get; } = new();
        public SessionStore Store { get; } = new();
        public CommandCenterState State { get; } = new(new SessionLogBuffer());

        public Harness()
        {
            ServiceCollection services = new();
            CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());
            services.RemoveAll<ArcanumApiClient>();
            services.AddSingleton(new ArcanumApiClient(new ClientFactory(Http), ArcanumApiCredentialLeaseTestFactory.Create("test-key")));
            services.RemoveAll<ILastSessionStore>();
            services.AddSingleton<ILastSessionStore>(Store);
            _provider = services.BuildServiceProvider();
            _app = Terminal.Gui.App.Application.Create().Init("ansi");
            Window.ApplyAbsoluteLayout(120, 24);
            State.ApplySessionMeta(Http.SessionId, "before", "Active", 0);
            Http.Entries = [Entry("assistant", "durable answer")];
        }

        public EntryDto Entry(string role, string text) => new(Guid.NewGuid(), Http.SessionId, role, text, null, null, DateTimeOffset.UtcNow);

        public Task SubmitAsync() => (Task)typeof(CommandCenterHost).GetMethod("HandleSubmitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
            _provider.GetRequiredService<ICommandCenterHost>(),
            ["question", State, Channel.CreateUnbounded<CommandCenterUiUpdate>().Writer, _app, Window, _lifetime])!;

        public Task<ShellDispatchResult> DispatchAsync(string command) => _provider.GetRequiredService<ShellCommandDispatcher>().DispatchAsync(command, State, CancellationToken.None);

        public void Pump() => _app.TimedEvents!.RunTimers();

        public async ValueTask DisposeAsync()
        {
            _lifetime.Dispose();
            Window.Dispose();
            _app.Dispose();
            await _provider.DisposeAsync();
        }
    }

    private sealed class SessionStore : ILastSessionStore
    {
        public bool Refuse { get; set; }
        public Guid? GetLastSessionId() => null;
        public Task<ArcanumClientMutationResult<CliContextDocument>> SaveSessionIdAsync(Guid id, Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync, CancellationToken cancellationToken) =>
            Task.FromResult(Refuse
                ? ArcanumClientMutationResult<CliContextDocument>.Blocked(new Error(ErrorCodes.Data.ResetInProgress, "binding refusal"))
                : ArcanumClientMutationResult<CliContextDocument>.Completed(CliContextDocument.Empty with { SessionId = id }));
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://localhost:5001"), Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class HeldStream(byte[] payload, TaskCompletionSource waiting) : MemoryStream(payload)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length)
            {
                return await base.ReadAsync(buffer, cancellationToken);
            }
            waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class HttpFixture : HttpMessageHandler
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public EntryDto[] Entries { get; set; } = [];
        public IntelligenceEventType? Terminal { get; set; } = IntelligenceEventType.Result;
        public bool BindSession { get; set; }
        public bool HoldEntries { get; set; }
        public bool HoldStream { get; set; }
        public bool RefuseEntries { get; set; }
        public TaskCompletionSource StreamWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AskCount { get; private set; }
        public int ForkCount { get; private set; }
        public ForkSessionRequest? ForkRequest { get; private set; }
        public int DetailCount { get; private set; }
        public TaskCompletionSource EntriesEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseEntries { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/fork/preview", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(ApiResponse<SessionForkPreviewDto>.FromResult(Result<SessionForkPreviewDto>.Success(new SessionForkPreviewDto(0, 0, "fingerprint"))), ArcanumJsonContext.Default.ApiResponseSessionForkPreviewDto));
            }

            if (path.EndsWith("/fork", StringComparison.Ordinal))
            {
                ForkCount++;
                ForkRequest = JsonSerializer.Deserialize(await request.Content!.ReadAsStringAsync(cancellationToken), ArcanumJsonContext.Default.ForkSessionRequest);
                // Existing fork failure semantics remain owned by SessionWorkspaceServiceTests.
                // The consumer must send this exact durable cutoff once, without a second inference.
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            }

            if (request.Method == HttpMethod.Post)
            {
                AskCount++;
                List<IntelligenceEvent> frames = [];
                if (BindSession)
                {
                    frames.Add(new IntelligenceEvent(IntelligenceEventType.SessionBound, "bound", SessionId.ToString("D")));
                }
                frames.Add(new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, "live answer"));
                if (!HoldStream && Terminal is { } terminal)
                {
                    frames.Add(new IntelligenceEvent(terminal, "terminal", "live answer"));
                }

                string payload = string.Join('\n', frames.Select(frame => JsonSerializer.Serialize(frame, ArcanumJsonContext.Default.IntelligenceEvent))) + "\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = HoldStream ? new StreamContent(new HeldStream(Encoding.UTF8.GetBytes(payload), StreamWaiting))
                        : new StringContent(payload, Encoding.UTF8, "application/x-ndjson"),
                };
            }

            if (path.EndsWith("/entries", StringComparison.Ordinal))
            {
                EntriesEntered.TrySetResult();
                if (HoldEntries)
                {
                    await ReleaseEntries.Task.WaitAsync(cancellationToken);
                }

                if (RefuseEntries)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }

                return Json(JsonSerializer.Serialize(ApiResponse<EntryDto[]>.FromResult(Result<EntryDto[]>.Success(Entries)), ArcanumJsonContext.Default.ApiResponseEntryDtoArray));
            }

            if (path == $"/api/sessions/{SessionId:D}")
            {
                DetailCount++;
                SessionDetailDto detail = new(SessionId, null, "after", "Active", Entries.Length, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 0);
                return Json(JsonSerializer.Serialize(ApiResponse<SessionDetailDto>.FromResult(Result<SessionDetailDto>.Success(detail)), ArcanumJsonContext.Default.ApiResponseSessionDetailDto));
            }

            if (path is "/api/sessions" or "/api/sessions/query")
            {
                return Json(JsonSerializer.Serialize(ApiResponse<SessionQueryResult>.FromResult(Result<SessionQueryResult>.Success(new SessionQueryResult([], null, false))), ArcanumJsonContext.Default.ApiResponseSessionQueryResult));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
