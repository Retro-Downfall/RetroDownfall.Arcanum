using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Tests.Support;
using Terminal.Gui.App;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterSessionAttachmentBindingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Session_metadata_retains_only_the_current_sessions_attachment_projection(bool sameSession)
    {
        CommandCenterState state = new(new SessionLogBuffer());
        Guid source = Guid.NewGuid();
        state.ApplySessionMeta(source, "Source", "Active", 0);
        SessionAttachmentDto row = Attachment("source") with { IndexingStatus = SessionAttachmentIndexStatus.Pending };
        state.SessionAttachments = [row];

        state.ApplySessionMeta(sameSession ? source : Guid.NewGuid(), "Destination", "Active", 0);

        if (sameSession)
        {
            Assert.Same(row, Assert.Single(state.SessionAttachments));
            Assert.NotNull(state.AttachmentIndexingStatusText);
        }
        else
        {
            Assert.Empty(state.SessionAttachments);
            Assert.Null(state.AttachmentIndexingStatusText);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Successful_resume_fetches_destination_attachments_without_a_filesystem_event(bool confirm, bool holdDestinationResponse)
    {
        await using Harness h = new();
        h.State.ApplySessionMeta(h.Source, "Source", "Active", 0);
        h.Monitor.Start(h.State, h.Updates.Writer, h.Cancellation.Token);
        await h.UntilAsync(() => h.State.SessionAttachments.Any(static row => row.LogicalKey == "source"));

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (holdDestinationResponse)
        {
            h.BeforeAttachmentResponse = token => release.Task.WaitAsync(token);
        }

        try
        {
            await h.ResumeAsync(h.Destination, confirm);

            if (holdDestinationResponse)
            {
                await h.UntilAsync(() => h.AttachmentRequests.Contains(h.Destination));

                Assert.Equal(h.Destination, h.State.SessionId);

                Assert.Empty(h.State.SessionAttachments);
            }
        }
        finally
        {
            release.TrySetResult();
        }

        // Wait for the asynchronous refresh to publish the destination projection.
        await h.UntilAsync(() => h.State.SessionAttachments.Any(static row => row.LogicalKey == "destination"));

        Assert.Equal(h.Destination, h.State.SessionId);
        Assert.Contains(h.Destination, h.AttachmentRequests);
        Assert.Equal("destination", Assert.Single(h.State.SessionAttachments).LogicalKey);
        Assert.DoesNotContain("source", h.State.Log.RenderPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_resume_preserves_the_existing_attachment_projection()
    {
        await using Harness h = new();
        h.State.ApplySessionMeta(h.Source, "Source", "Active", 0);
        SessionAttachmentDto row = Attachment("source");
        h.State.SessionAttachments = [row];
        h.RefuseResume = true;

        await h.ResumeAsync(h.Destination, confirm: false);

        Assert.Equal(h.Source, h.State.SessionId);
        Assert.Same(row, Assert.Single(h.State.SessionAttachments));
        Assert.Empty(h.AttachmentRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_inflight_snapshot_cannot_cross_a_session_binding_lifetime(bool returnToOriginal)
    {
        await using Harness h = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.BeforeAttachmentResponse = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        h.State.ApplySessionMeta(h.Source, "Source", "Active", 0);
        h.Monitor.Start(h.State, h.Updates.Writer, h.Cancellation.Token);
        await h.UntilAsync(() => entered.Task.IsCompleted);
        SessionAttachmentDto fresh = Attachment("fresh-projection");
        try
        {
            h.State.ApplySessionMeta(h.Destination, "Destination", "Active", 0);
            if (returnToOriginal)
            {
                h.State.ApplySessionMeta(h.Source, "Source", "Active", 0);
            }
            h.State.SessionAttachments = [fresh];
        }
        finally
        {
            release.TrySetResult();
        }

        await h.DriveAsync();
        Assert.Same(fresh, Assert.Single(h.State.SessionAttachments));
        Assert.Single(h.AttachmentRequests);
    }

    private static SessionAttachmentDto Attachment(string key) =>
        new(Guid.NewGuid(), key, key + ".txt", 1, "session/" + key + "/v1/file.txt", "text/plain", 10,
            SessionAttachmentKind.Text, "HASH", DateTimeOffset.UnixEpoch)
        {
            IndexingStatus = SessionAttachmentIndexStatus.Indexed,
        };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        internal Guid Source { get; } = Guid.NewGuid();
        internal Guid Destination { get; } = Guid.NewGuid();
        internal bool RefuseResume { get; set; }
        internal Func<CancellationToken, Task>? BeforeAttachmentResponse { get; set; }
        internal ConcurrentQueue<Guid> AttachmentRequests { get; } = new();
        internal CommandCenterState State { get; } = new(new SessionLogBuffer()) { WorkingDirectory = AppContext.BaseDirectory };
        internal Channel<CommandCenterUiUpdate> Updates { get; } = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        internal CancellationTokenSource Cancellation { get; } = new();
        internal ManualTimerTimeProvider Clock { get; } = new();
        internal CommandCenterAttachmentDriftMonitor Monitor { get; }
        internal CommandCenterHost Host { get; }
        internal CommandCenterWindow Window { get; } = new();
        internal IApplication App { get; } = Application.Create().Init("ansi");

        internal Harness()
        {
            ArcanumApiClient client = new(new Factory(new Handler(this)), ArcanumApiCredentialLeaseTestFactory.Create("test-key"));
            Monitor = new CommandCenterAttachmentDriftMonitor(client, NullLogger<CommandCenterAttachmentDriftMonitor>.Instance,
                static _ => new FileSystemWatcher(), static _ => { }, Clock);
            ServiceCollection services = new();
            CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());
            services.RemoveAll<ArcanumApiClient>();
            services.AddSingleton(client);
            services.RemoveAll<CommandCenterAttachmentDriftMonitor>();
            services.AddSingleton(Monitor);
            services.RemoveAll<ILastSessionStore>();
            services.AddSingleton<ILastSessionStore>(new LastSessionStore());
            _services = services.BuildServiceProvider();
            Host = (CommandCenterHost)_services.GetRequiredService<ICommandCenterHost>();
            Window.ApplyAbsoluteLayout(120, 40);
        }

        internal async Task ResumeAsync(Guid target, bool confirm)
        {
            await InvokeAsync("ResumeWithConfirmCoreAsync", State, Window, App, Updates.Writer, target, confirm, Cancellation.Token);
            if (confirm)
            {
                Assert.Equal(CommandCenterOverlayKind.DiscardConfirm, State.Overlay);
                await InvokeAsync("ConfirmPendingAsync", State, Window, App, Updates.Writer, Cancellation);
            }
            App.TimedEvents!.RunTimers();
        }

        private Task InvokeAsync(string method, params object[] arguments) => (Task)typeof(CommandCenterHost)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Host, arguments)!;

        internal async Task UntilAsync(Func<bool> condition)
        {
            Stopwatch guard = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(guard.Elapsed < TimeSpan.FromSeconds(10), "Expected attachment monitor state was not reached.");
                Clock.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(1);
            }
        }

        internal async Task DriveAsync()
        {
            Stopwatch opportunity = Stopwatch.StartNew();
            while (opportunity.Elapsed < TimeSpan.FromMilliseconds(200))
            {
                Clock.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(1);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Cancellation.Cancel();
            await Monitor.DisposeAsync();
            await _services.DisposeAsync();
            Window.Dispose();
            App.Dispose();
            Cancellation.Dispose();
        }

        private sealed class Handler(Harness h) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/attachments", StringComparison.Ordinal))
                {
                    Guid id = Guid.Parse(path.Split('/')[3]);
                    h.AttachmentRequests.Enqueue(id);
                    if (h.BeforeAttachmentResponse is not null)
                    {
                        await h.BeforeAttachmentResponse(token);
                    }

                    return Json(new ApiResponse<SessionAttachmentDto[]>([Attachment(id == h.Source ? "source" : "destination")], true, null),
                        ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);
                }

                if (path == "/api/sessions")
                {
                    return Json(new ApiResponse<SessionQueryResult>(new([], null, false), true, null), ArcanumJsonContext.Default.ApiResponseSessionQueryResult);
                }

                if (path.EndsWith("/entries", StringComparison.Ordinal))
                {
                    return Json(new ApiResponse<EntryDto[]>([], true, null), ArcanumJsonContext.Default.ApiResponseEntryDtoArray);
                }

                if (path == "/api/sessions/" + h.Destination && !h.RefuseResume)
                {
                    return Json(new ApiResponse<SessionDetailDto>(new(h.Destination, null, "Destination", "Active", 0,
                        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, 0), true, null), ArcanumJsonContext.Default.ApiResponseSessionDetailDto);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }

    private static HttpResponseMessage Json<T>(T value, JsonTypeInfo<T> type) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, type)),
    };

    private sealed class LastSessionStore : ILastSessionStore
    {
        public Guid? GetLastSessionId() => null;
        public Task<ArcanumClientMutationResult<CliContextDocument>> SaveSessionIdAsync(Guid id,
            Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync, CancellationToken cancellationToken) =>
            Task.FromResult(ArcanumClientMutationResult<CliContextDocument>.Completed(CliContextDocument.Empty with { SessionId = id }));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:5001/") };
    }
}
