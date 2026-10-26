using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterAttachmentDriftMonitorReliabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_obj_churn_does_not_revalidate_even_with_known_nonmatching_sources(bool tracked)
    {
        await using Fixture fixture = new(tracked);

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        for (int i = 0; i < 100; i++)
        {
            fixture.Watcher.Change(fixture.Root, "obj/build.tmp");
        }

        await fixture.DriveAsync();

        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task Sustained_relevant_events_have_a_minimum_fetch_spacing_and_eventually_refresh()
    {
        await using Fixture fixture = new(tracked: true);

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        for (int i = 0; i < 40; i++)
        {
            fixture.Watcher.Change(fixture.Root, "src/notes.txt");

            fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));

            await Task.Delay(2);
        }

        await fixture.DriveAsync();

        long[] requests = fixture.Requests.ToArray();

        Assert.True(requests.Length >= 2, "Relevant events must eventually cause another backend refresh.");

        Assert.All(requests.Zip(requests.Skip(1)), pair =>
            Assert.True(pair.Second - pair.First >= TimeSpan.TicksPerSecond, "The monitor fetched more than once per second during an event storm."));
    }

    [Theory]
    [InlineData("src/notes.txt", null)]
    [InlineData("src", null)]
    [InlineData("archive/notes.txt", "src/notes.txt")]
    [InlineData("src/notes.txt", "archive/notes.txt")]
    [InlineData("moved", "src")]
    public async Task Relevant_files_ancestors_and_both_rename_paths_revalidate(string current, string? previous)
    {
        await using Fixture fixture = new(tracked: true);

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        if (previous is null)
        {
            fixture.Watcher.Change(fixture.Root, current, WatcherChangeTypes.Deleted);
        }
        else
        {
            fixture.Watcher.Rename(fixture.Root, current, previous);
        }

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);
    }

    [Fact]
    public async Task A_working_directory_below_the_registered_root_matches_the_source_suffix()
    {
        await using Fixture fixture = new(tracked: true, descendant: true);

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Watcher.Change(fixture.Root, "notes.txt");

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Optional_watcher_startup_failure_keeps_explicit_refresh_alive_and_reports_only_safe_guidance(bool creationFails)
    {
        await using Fixture fixture = new(tracked: false);

        fixture.CreateFails = creationFails;

        fixture.EnableFails = !creationFails;

        Assert.Null(Record.Exception(fixture.Start));

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Monitor.RequestRefresh();

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);

        Assert.Equal(creationFails ? 0 : 1, fixture.Watcher.Disposals);

        Assert.Contains(fixture.Log.Entries, static entry => entry.Level == LogLevel.Warning && entry.Text.Contains("refresh", StringComparison.OrdinalIgnoreCase));

        Assert.All(fixture.Log.Entries, static entry => Assert.DoesNotContain(Fixture.PrivateFailure, entry.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explicit_refresh_and_pending_snapshot_only_polling_do_not_require_workspace_sources()
    {
        await using Fixture fixture = new(tracked: false);

        fixture.Rows = count => [Fixture.Attachment(false) with { IndexingStatus = count < 2 ? SessionAttachmentIndexStatus.Pending : SessionAttachmentIndexStatus.Indexed }];

        fixture.Start();

        await fixture.UntilAsync(() => fixture.State.SessionAttachments.Any(static row => row.IndexingStatus == SessionAttachmentIndexStatus.Indexed));

        Assert.Equal(2, fixture.Requests.Count);

        fixture.Monitor.RequestRefresh();

        await fixture.UntilAsync(() => fixture.Requests.Count == 3);
    }

    [Fact]
    public async Task Overflow_revalidates_conservatively_without_logging_exception_payloads()
    {
        await using Fixture fixture = new(tracked: false);

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Watcher.EmitError(new InternalBufferOverflowException(Fixture.PrivateFailure));

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);

        Assert.All(fixture.Log.Entries, static entry => Assert.DoesNotContain(Fixture.PrivateFailure, entry.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pending_indexing_retries_a_transient_get_failure_without_spinning()
    {
        await using Fixture fixture = new(tracked: false);

        fixture.State.SessionAttachments = [Fixture.Attachment(false) with { IndexingStatus = SessionAttachmentIndexStatus.Pending }];

        fixture.FailAt = 2;

        fixture.Rows = count => [Fixture.Attachment(false) with { IndexingStatus = count < 3 ? SessionAttachmentIndexStatus.Pending : SessionAttachmentIndexStatus.Indexed }];

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);

        await fixture.DriveAsync();

        Assert.Equal(3, fixture.Requests.Count);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, Assert.Single(fixture.State.SessionAttachments).IndexingStatus);

        Assert.All(fixture.Requests.Zip(fixture.Requests.Skip(1)), static pair =>
            Assert.True(pair.Second - pair.First >= TimeSpan.TicksPerSecond));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_reason_survives_a_later_filesystem_hint_during_a_fetch(bool requestFails)
    {
        await using Fixture fixture = new(tracked: true);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.BeforeResponse = (count, token) => count == 1 ? release.Task.WaitAsync(token) : Task.CompletedTask;

        fixture.FailAt = requestFails ? 1 : 0;

        fixture.Rows = _ => [];

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Monitor.RequestRefresh();

        fixture.Watcher.Change(fixture.Root, "src/notes.txt");

        fixture.State.SessionAttachments = [];

        release.SetResult();

        await fixture.DriveAsync();

        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task Explicit_reason_survives_the_spacing_wait_when_sources_disappear()
    {
        await using Fixture fixture = new(tracked: true);

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        int priorTimers = fixture.Clock.TimersCreated;

        fixture.Monitor.RequestRefresh();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        await fixture.Clock.WaitForTimerCountAsync(priorTimers + 1, timeout.Token);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(150));

        await fixture.Clock.WaitForTimerCountAsync(priorTimers + 2, timeout.Token);

        fixture.Watcher.Change(fixture.Root, "src/notes.txt");

        fixture.State.SessionAttachments = [];

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);
    }

    [Fact]
    public async Task A_reply_for_the_previous_session_cannot_replace_current_attachment_state()
    {
        await using Fixture fixture = new(tracked: true);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.BeforeResponse = (count, token) => count == 1 ? release.Task.WaitAsync(token) : Task.CompletedTask;

        fixture.Rows = _ => [Fixture.Attachment(true) with { LogicalKey = "previous-session" }];

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        SessionAttachmentDto current = Fixture.Attachment(false) with { LogicalKey = "current-session" };

        fixture.State.SessionId = Guid.NewGuid();

        fixture.State.SessionAttachments = [current];

        release.SetResult();

        await fixture.DriveAsync();

        Assert.Same(current, Assert.Single(fixture.State.SessionAttachments));
    }

    [Fact]
    public async Task A_filesystem_hint_changes_no_badge_before_the_backend_reply()
    {
        await using Fixture fixture = new(tracked: true);

        SessionAttachmentDto original = Assert.Single(fixture.State.SessionAttachments);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.BeforeResponse = (count, token) => count == 2 ? release.Task.WaitAsync(token) : Task.CompletedTask;

        fixture.Rows = count => [original with { SourceStatus = count < 2 ? AttachmentSourceStatus.Refreshable : AttachmentSourceStatus.PriorVersion }];

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Watcher.Change(fixture.Root, "src/notes.txt");

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);

        Assert.Equal(AttachmentSourceStatus.Refreshable, Assert.Single(fixture.State.SessionAttachments).SourceStatus);

        release.SetResult();

        await fixture.UntilAsync(() => fixture.State.SessionAttachments[0].SourceStatus == AttachmentSourceStatus.PriorVersion);
    }

    [Fact]
    public async Task Filtering_uses_current_source_rows_and_complete_path_segments()
    {
        await using Fixture fixture = new(tracked: true);

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.State.SessionAttachments = [Fixture.Attachment(true) with { SourceRelativePath = "docs/other.txt", IsRefreshable = false, SourceStatus = AttachmentSourceStatus.Missing }];

        fixture.Watcher.Change(fixture.Root, "src/notes.txt");

        fixture.Watcher.Change(fixture.Root, "docs2/other.txt");

        await fixture.DriveAsync();

        Assert.Single(fixture.Requests);

        fixture.Watcher.Change(fixture.Root, "DOCS/OTHER.TXT", WatcherChangeTypes.Created);

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);
    }

    [Theory]
    [InlineData("src")]
    [InlineData("unknown-registered-root")]
    public async Task Directory_and_root_moves_that_cannot_be_ruled_unrelated_revalidate(string directory)
    {
        await using Fixture fixture = new(tracked: true);

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Watcher.Rename(fixture.Root, "moved-root", directory);

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);
    }

    [Fact]
    public async Task Changed_ancestor_directories_are_relevant_even_without_a_delete_or_rename()
    {
        await using Fixture fixture = new(tracked: true);

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Watcher.Change(fixture.Root, "src");

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);
    }

    [Fact]
    public async Task No_workspace_sources_ignore_all_ordinary_event_types()
    {
        await using Fixture fixture = new(tracked: false);

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Watcher.Change(fixture.Root, "obj/build.tmp", WatcherChangeTypes.Created);

        fixture.Watcher.Change(fixture.Root, "obj/build.tmp", WatcherChangeTypes.Deleted);

        fixture.Watcher.Rename(fixture.Root, "new-root", "old-root");

        await fixture.DriveAsync();

        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData("../not-a-source")]
    [InlineData(" ")]
    [InlineData("/absolute/metadata-is-not-authority")]
    public async Task Ambiguous_source_metadata_keeps_conservative_hints(string relative)
    {
        await using Fixture fixture = new(tracked: true);

        fixture.State.SessionAttachments = [Fixture.Attachment(true) with { SourceRelativePath = relative }];

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Watcher.Change(fixture.Root, "obj/build.tmp");

        await fixture.DriveAsync();

        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task A_missing_watcher_directory_still_starts_the_explicit_refresh_pump()
    {
        await using Fixture fixture = new(tracked: false);

        Directory.Delete(fixture.Root);

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Monitor.RequestRefresh();

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);
    }

    [Fact]
    public async Task Watcher_disposal_failure_during_startup_does_not_disable_pending_polling()
    {
        await using Fixture fixture = new(tracked: false);

        fixture.EnableFails = true;

        fixture.Watcher.FailDisposeOnce = true;

        fixture.Rows = count => [Fixture.Attachment(false) with { IndexingStatus = count < 2 ? SessionAttachmentIndexStatus.Pending : SessionAttachmentIndexStatus.Indexed }];

        Assert.Null(Record.Exception(fixture.Start));

        await fixture.UntilAsync(() => fixture.State.SessionAttachments.Any(static row => row.IndexingStatus == SessionAttachmentIndexStatus.Indexed));

        Assert.Equal(1, fixture.Watcher.Disposals);

        Assert.All(fixture.Log.Entries, static entry => Assert.DoesNotContain(Fixture.PrivateFailure, entry.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unexpected_programmer_failures_are_not_reclassified_as_optional_watcher_failures()
    {
        await using Fixture fixture = new(tracked: false);

        fixture.CreateUnexpected = true;

        Assert.Throws<InvalidOperationException>(fixture.Start);

        Assert.DoesNotContain(fixture.Log.Entries, static entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Cancellation_and_mixed_disposal_are_idempotent_and_stop_an_inflight_request()
    {
        await using Fixture fixture = new(tracked: true);

        fixture.BeforeResponse = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Monitor.Dispose();

        await fixture.Monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Monitor.DisposeAsync();

        fixture.Monitor.RequestRefresh();

        fixture.Watcher.Change(fixture.Root, "src/notes.txt");

        await fixture.DriveAsync();

        Assert.Single(fixture.Requests);

        Assert.Equal(1, fixture.Watcher.Disposals);

        Assert.Equal(0, fixture.Clock.ActiveTimers);
    }

    [Fact]
    public async Task Slow_credential_preparation_cannot_compress_backend_fetches_into_a_burst()
    {
        await using Fixture fixture = new(tracked: false);

        fixture.DelayCredential = true;

        fixture.Start();

        await fixture.UntilAsync(() => fixture.CredentialRequested.Task.IsCompleted);

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));

        fixture.Monitor.RequestRefresh();

        fixture.CredentialReady.SetResult();

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);

        long[] starts = fixture.Requests.ToArray();

        Assert.True(starts[1] - starts[0] >= TimeSpan.TicksPerSecond, "Delayed API preparation compressed actual backend GETs below the minimum interval.");
    }

    [Fact]
    public async Task Canonically_equivalent_unicode_paths_keep_conservative_hints()
    {
        await using Fixture fixture = new(tracked: true);

        fixture.State.SessionAttachments = [Fixture.Attachment(true) with { SourceRelativePath = "src/café.txt" }];

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Watcher.Change(fixture.Root, "src/cafe\u0301.txt");

        await fixture.DriveAsync();

        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task A_failed_nonpending_attempt_waits_for_a_new_hint_or_manual_refresh()
    {
        await using Fixture fixture = new(tracked: true);

        fixture.FailAt = 2;

        fixture.Start();

        await fixture.UntilAsync(() => fixture.Requests.Count == 1);

        fixture.Watcher.Change(fixture.Root, "src/notes.txt");

        await fixture.UntilAsync(() => fixture.Requests.Count == 2);

        await fixture.DriveAsync();

        Assert.Equal(2, fixture.Requests.Count);

        fixture.Monitor.RequestRefresh();

        await fixture.UntilAsync(() => fixture.Requests.Count == 3);

        Assert.All(fixture.Requests.Zip(fixture.Requests.Skip(1)), static pair =>
            Assert.True(pair.Second - pair.First >= TimeSpan.TicksPerSecond));
    }

    [Fact]
    public async Task Lifetime_cancellation_stops_pending_polling_without_post_shutdown_resignals()
    {
        await using Fixture fixture = new(tracked: false);

        using CancellationTokenSource lifetime = new();

        fixture.Rows = _ => [Fixture.Attachment(false) with { IndexingStatus = SessionAttachmentIndexStatus.Pending }];

        fixture.Start(lifetime.Token);

        await fixture.UntilAsync(() => fixture.State.SessionAttachments.Any(static row => row.IndexingStatus == SessionAttachmentIndexStatus.Pending));

        lifetime.Cancel();

        fixture.Monitor.RequestRefresh();

        await fixture.DriveAsync();

        await fixture.Monitor.DisposeAsync();

        Assert.Single(fixture.Requests);

        Assert.Equal(0, fixture.Clock.ActiveTimers);

        Assert.Equal(1, fixture.Watcher.Disposals);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string PrivateFailure = "/private/profile-do-not-log/opaque-source-secret";

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanum-drift-" + Guid.NewGuid().ToString("N"));

        public Fixture(bool tracked, bool descendant = false)
        {
            Root = descendant ? Path.Combine(_directory, "repo", "src") : _directory;

            Directory.CreateDirectory(Root);

            State = new CommandCenterState(new SessionLogBuffer())
            {
                WorkingDirectory = Root,
                SessionId = Guid.NewGuid(),
                SessionAttachments = tracked ? [Attachment(true)] : [],
            };

            Rows = _ => State.SessionAttachments.ToArray();

            ArcanumApiClient client = new(new Factory(new Handler(this)), ArcanumApiCredentialLeaseTestFactory.Create(
                static _ => Task.FromResult(SecretStoreReadResult.Missing()),
                async token =>
                {
                    CredentialRequested.TrySetResult();

                    if (DelayCredential)
                    {
                        await CredentialReady.Task.WaitAsync(token);
                    }

                    return SecretStoreReadResult.Ok("test-key");
                },
                "test-key"));

            Monitor = new CommandCenterAttachmentDriftMonitor(client, Log,
                _ => CreateUnexpected ? throw new InvalidOperationException("programmer failure")
                    : CreateFails ? throw new IOException(PrivateFailure) : Watcher,
                _ => { if (EnableFails) { throw new IOException(PrivateFailure); } }, Clock);
        }

        public string Root { get; }

        public CommandCenterState State { get; }

        public ManualTimerTimeProvider Clock { get; } = new();

        public Watcher Watcher { get; } = new();

        public SafeLog Log { get; } = new();

        public ConcurrentQueue<long> Requests { get; } = new();

        public Func<int, SessionAttachmentDto[]> Rows { get; set; }

        public bool DelayCredential { get; set; }

        public TaskCompletionSource CredentialRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CredentialReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CreateFails { get; set; }

        public bool CreateUnexpected { get; set; }

        public bool EnableFails { get; set; }

        public int FailAt { get; set; }

        public Func<int, CancellationToken, Task>? BeforeResponse { get; set; }

        public CommandCenterAttachmentDriftMonitor Monitor { get; }

        public Channel<CommandCenterUiUpdate> Updates { get; } = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        public void Start() => Start(CancellationToken.None);

        public void Start(CancellationToken token) => Monitor.Start(State, Updates.Writer, token);

        public async Task UntilAsync(Func<bool> condition)
        {
            Stopwatch timeout = Stopwatch.StartNew();

            while (!condition())
            {
                Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(10), "The fake-clock monitor failed to reach the expected backend state.");

                Clock.Advance(TimeSpan.FromMilliseconds(100));

                await Task.Delay(1);
            }

            await Task.Delay(5);
        }

        public async Task DriveAsync()
        {
            // Give the asynchronous pump scheduling opportunities while only the manual clock can
            // release its delays. This is a negative observation window, not a CPU/latency assertion.
            Stopwatch window = Stopwatch.StartNew();

            while (window.Elapsed < TimeSpan.FromMilliseconds(200))
            {
                Clock.Advance(TimeSpan.FromMilliseconds(100));

                await Task.Delay(1);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Monitor.DisposeAsync();

            Watcher.Dispose();

            _ = TestDirectoryCleanup.TryDelete(_directory, nameof(CommandCenterAttachmentDriftMonitorReliabilityTests));
        }

        public static SessionAttachmentDto Attachment(bool tracked) =>
            new(Guid.NewGuid(), "notes", "notes.txt", 1, "session/notes/v1/notes.txt", "text/plain", 10,
                SessionAttachmentKind.Text, "LOADED", DateTimeOffset.UnixEpoch,
                tracked ? AttachmentSourceKind.WorkspaceFile : AttachmentSourceKind.SnapshotOnly,
                tracked ? "opaque-workspace-identity" : null, tracked ? "src/notes.txt" : null,
                tracked, tracked ? AttachmentSourceStatus.Refreshable : AttachmentSourceStatus.NotApplicable);
    }

    private sealed class Watcher : FileSystemWatcher
    {
        public int Disposals { get; private set; }

        public bool FailDisposeOnce { get; set; }

        public void Change(string root, string name, WatcherChangeTypes kind = WatcherChangeTypes.Changed)
        {
            FileSystemEventArgs args = new(kind, root, name);

            if (kind == WatcherChangeTypes.Created)
            {
                OnCreated(args);
            }
            else if (kind == WatcherChangeTypes.Deleted)
            {
                OnDeleted(args);
            }
            else
            {
                OnChanged(args);
            }
        }

        public void Rename(string root, string current, string previous) =>
            OnRenamed(new RenamedEventArgs(WatcherChangeTypes.Renamed, root, current, previous));

        public void EmitError(Exception exception) => OnError(new ErrorEventArgs(exception));

        protected override void Dispose(bool disposing)
        {
            Disposals++;

            base.Dispose(disposing);

            if (FailDisposeOnce)
            {
                FailDisposeOnce = false;

                throw new IOException(Fixture.PrivateFailure);
            }
        }
    }

    private sealed class SafeLog : ILogger<CommandCenterAttachmentDriftMonitor>
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string values = state is IEnumerable<KeyValuePair<string, object?>> fields
                ? string.Join("|", fields.Select(static field => field.Value?.ToString())) : string.Empty;

            Entries.Enqueue((level, formatter(state, exception) + values + exception));
        }
    }

    private sealed class Handler(Fixture fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal($"/api/sessions/{fixture.State.SessionId}/attachments", request.RequestUri!.AbsolutePath);

            fixture.Requests.Enqueue(fixture.Clock.GetTimestamp());

            int count = fixture.Requests.Count;

            if (fixture.BeforeResponse is not null)
            {
                await fixture.BeforeResponse(count, cancellationToken);
            }

            if (count == fixture.FailAt)
            {
                byte[] failure = JsonSerializer.SerializeToUtf8Bytes(
                    ApiResponse<SessionAttachmentDto[]>.FromResult(Result<SessionAttachmentDto[]>.Failure(new Error("Test.Down", "down"))),
                    ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent(failure) };
            }

            byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                ApiResponse<SessionAttachmentDto[]>.FromResult(Result<SessionAttachmentDto[]>.Success(fixture.Rows(fixture.Requests.Count))),
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:5001/") };
    }
}
