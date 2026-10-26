using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Tests.Support;
using Terminal.Gui.App;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterSessionConcurrencyTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset Epoch = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Overlapping_session_pages_advance_distinct_cursors_and_history_steps()
    {
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        ConcurrentQueue<DateTimeOffset?> cursors = new();
        using Handler handler = new(async (request, token) =>
        {
            string? cursorText = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["beforeUpdatedAt"];
            DateTimeOffset cursor = DateTimeOffset.Parse(cursorText!, System.Globalization.CultureInfo.InvariantCulture);
            cursors.Enqueue(cursor);
            if (cursors.Count == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            return Json(new ApiResponse<SessionQueryResult>(new([], cursor.AddDays(-1), true), true, null),
                ArcanumJsonContext.Default.ApiResponseSessionQueryResult);
        });
        await using Harness h = new(handler);
        h.State.NextSessionPageBeforeUpdatedAt = Epoch;
        Task first = h.Dispatch(CommandCenterAction.LoadOlderSessionPage);
        await entered.Task.WaitAsync(HangGuard);
        Task second = h.Dispatch(CommandCenterAction.LoadOlderSessionPage);
        try
        {
            Assert.Single(cursors);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(HangGuard);
        }
        Assert.Equal(new DateTimeOffset?[] { Epoch, Epoch.AddDays(-1) }, cursors.ToArray());
        Assert.Equal(new DateTimeOffset?[] { null, Epoch }, h.State.SessionPageHistory);
        Assert.Equal(Epoch.AddDays(-1), h.State.SessionPageBeforeUpdatedAt);
    }

    [Theory]
    [InlineData((int)CommandCenterAction.LoadOlderSessionPage)]
    [InlineData((int)CommandCenterAction.LoadNewerSessionPage)]
    [InlineData((int)CommandCenterAction.LoadOlderTranscriptPage)]
    [InlineData((int)CommandCenterAction.LoadNewerTranscriptPage)]
    public async Task Page_actions_wait_for_the_shared_gate_and_cancel_without_mutating(int actionValue)
    {
        CommandCenterAction action = (CommandCenterAction)actionValue;
        using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        await using Harness h = new(handler);
        h.State.ApplySessionMeta(Guid.NewGuid(), "Current", "Active", 1);
        h.State.NextSessionPageBeforeUpdatedAt = Epoch;
        h.State.SessionPageHistory.Add(null);
        h.State.LoadedTranscriptEntries = [new(Guid.NewGuid(), h.State.SessionId!.Value, "user", "entry", null, null, Epoch)];
        h.State.HasOlderTranscriptEntries = true;
        h.State.HasNewerTranscriptEntries = true;
        TaskCompletionSource release = NewSignal();
        Task holder = h.Host.RunGatedAsync(h.State, h.Ui.Writer, CancellationToken.None, () => release.Task);
        Task page = h.Dispatch(action);
        try
        {
            Assert.Empty(handler.Paths);
            Assert.False(page.IsCompleted);
            h.Cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => page.WaitAsync(HangGuard));
        }
        finally
        {
            release.TrySetResult();
            await holder.WaitAsync(HangGuard);
            try { await page.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        }
        Assert.Single(h.State.SessionPageHistory);
        Assert.Equal("entry", Assert.Single(h.State.LoadedTranscriptEntries).Content);
    }

    [Fact]
    public async Task Submit_during_gated_resume_is_refused_without_sending_or_clearing_intent()
    {
        Guid source = Guid.NewGuid();
        Guid destination = Guid.NewGuid();
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        using Handler handler = new(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(destination.ToString("D"), StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return Json(new ApiResponse<SessionDetailDto>(new(destination, null, "Destination", "Active", 0, Epoch, Epoch, null, 0), true, null),
                    ArcanumJsonContext.Default.ApiResponseSessionDetailDto);
            }

            return DefaultResponse(request);
        });
        await using Harness h = new(handler);
        h.State.ApplySessionMeta(source, "Source", "Active", 0);
        Guid attachment = Guid.NewGuid();
        h.State.StageAttachmentReference(attachment);
        h.Window.Input.Text = "message for destination";
        Task resume = h.Host.RunGatedAsync(h.State, h.Ui.Writer, CancellationToken.None,
            async () => Assert.Equal(SessionResumeOutcome.Success,
                (await h.Workspace.ResumeSessionAsync(h.State, destination, CancellationToken.None)).Outcome));
        await entered.Task.WaitAsync(HangGuard);
        try
        {
            await h.Submit("message for destination").WaitAsync(HangGuard);
            h.Pump();
            Assert.DoesNotContain(handler.Paths, static p => p.Contains("intelligence", StringComparison.Ordinal));
            Assert.Equal("message for destination", h.Window.Input.Text);
            Assert.Equal(attachment, Assert.Single(h.State.StagedAttachmentReferences));
            Assert.False(h.State.Generating);
        }
        finally
        {
            release.TrySetResult();
            await resume.WaitAsync(HangGuard);
        }
        Assert.Equal(destination, h.State.SessionId);
    }

    [Theory]
    [InlineData((int)CommandCenterFocusRegion.Composer, (int)CommandCenterOverlayKind.CommandPalette)]
    [InlineData((int)CommandCenterFocusRegion.Sessions, (int)CommandCenterOverlayKind.None)]
    [InlineData((int)CommandCenterFocusRegion.Transcript, (int)CommandCenterOverlayKind.None)]
    [InlineData((int)CommandCenterFocusRegion.Composer, (int)CommandCenterOverlayKind.None)]
    public async Task Turn_completion_preserves_the_operators_current_focus(
        int focusValue, int overlayValue)
    {
        CommandCenterFocusRegion focus = (CommandCenterFocusRegion)focusValue;
        CommandCenterOverlayKind overlay = (CommandCenterOverlayKind)overlayValue;
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        using Handler handler = new(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("intelligence", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            return DefaultResponse(request);
        });
        await using Harness h = new(handler);
        h.State.FocusRegion = CommandCenterFocusRegion.Composer;
        h.Window.FocusInput();
        Task turn = h.Submit("hello");
        await entered.Task.WaitAsync(HangGuard);
        h.Pump();
        if (overlay == CommandCenterOverlayKind.CommandPalette)
        {
            CommandCenterHost.OpenPaletteNow(h.State, h.Window, CommandPaletteMode.Actions, string.Empty);
        }
        else
        {
            h.State.FocusRegion = focus;
            if (focus == CommandCenterFocusRegion.Transcript) h.Window.FocusLog();
        }
        CommandCenterFocusRegion expected = h.State.FocusRegion;
        release.TrySetResult();
        await turn.WaitAsync(HangGuard);
        h.Pump();
        Assert.Equal(overlay, h.State.Overlay);
        Assert.Equal(expected, h.State.FocusRegion);
        if (overlay == CommandCenterOverlayKind.CommandPalette)
        {
            Assert.True(h.Window.OverlayFilter.HasFocus);
            Assert.False(h.Window.Input.HasFocus);
        }
        Assert.False(h.State.Generating);
        Assert.Null(h.State.TurnCts);
    }

    [Fact]
    public async Task Active_turn_rejects_another_submit_but_cancel_remains_available()
    {
        TaskCompletionSource entered = NewSignal();
        using Handler handler = new(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("intelligence", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            }

            return DefaultResponse(request);
        });
        await using Harness h = new(handler);
        Task first = h.Submit("first");
        await entered.Task.WaitAsync(HangGuard);
        h.Pump();
        h.Window.Input.Text = "second";
        try
        {
            await h.Submit("second").WaitAsync(HangGuard);
            h.Pump();
            Assert.Equal("second", h.Window.Input.Text);
            Assert.Single(handler.Paths, static p => p.Contains("intelligence", StringComparison.Ordinal));
            Assert.True(h.State.Generating);
            await h.Dispatch(CommandCenterAction.CancelTurn).WaitAsync(HangGuard);
            await first.WaitAsync(HangGuard);
            Assert.False(h.State.Generating);
            Assert.Null(h.State.TurnCts);
        }
        finally
        {
            h.Cancellation.Cancel();
            await first.WaitAsync(HangGuard);
        }
    }

    [Theory]
    [InlineData((int)CommandCenterAction.LoadOlderSessionPage)]
    [InlineData((int)CommandCenterAction.LoadNewerSessionPage)]
    [InlineData((int)CommandCenterAction.LoadOlderTranscriptPage)]
    [InlineData((int)CommandCenterAction.LoadNewerTranscriptPage)]
    public async Task Paging_while_generating_is_refused_and_releases_the_action_gate(int actionValue)
    {
        using Handler handler = new((request, _) => Task.FromResult(DefaultResponse(request)));
        await using Harness h = new(handler);
        Assert.True(h.State.TryBeginTurn());
        await h.Dispatch((CommandCenterAction)actionValue).WaitAsync(HangGuard);
        Assert.Empty(handler.Paths);
        Assert.Contains("active turn", h.State.FooterHint, StringComparison.Ordinal);
        bool ran = false;
        await h.Host.RunGatedAsync(h.State, h.Ui.Writer, CancellationToken.None, () =>
        {
            ran = true;
            return Task.CompletedTask;
        }).WaitAsync(HangGuard);
        Assert.True(ran);
        h.State.EndTurn();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_ui_during_a_turn_does_not_fault_submit_or_leak_turn_ownership(bool cancelInvocation)
    {
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        using Handler handler = new(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("intelligence", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            return DefaultResponse(request);
        });
        await using Harness h = new(handler);
        Task turn = h.Submit("finishing during exit");
        await entered.Task.WaitAsync(HangGuard);
        h.Ui.Writer.TryComplete();
        if (cancelInvocation) h.Cancellation.Cancel();
        release.TrySetResult();
        await turn.WaitAsync(HangGuard);
        Assert.False(h.State.Generating);
        Assert.Null(h.State.TurnCts);
        Assert.DoesNotContain(h.Logger.Entries, static entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task Duplicate_submit_after_ui_closes_is_still_a_nonthrowing_refusal()
    {
        using Handler handler = new((request, _) => Task.FromResult(DefaultResponse(request)));
        await using Harness h = new(handler);
        Assert.True(h.State.TryBeginTurn());
        h.Window.Input.Text = "keep this";
        h.Ui.Writer.TryComplete();
        await h.Submit("keep this").WaitAsync(HangGuard);
        Assert.Empty(handler.Paths);
        Assert.Equal("keep this", h.Window.Input.Text);
        Assert.True(h.State.Generating);
        h.State.EndTurn();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static HttpResponseMessage Json<T>(T value, JsonTypeInfo<T> type) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, type)),
    };

    private static HttpResponseMessage DefaultResponse(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath;
        if (path.Contains("intelligence", StringComparison.Ordinal))
        {
            string frame = JsonSerializer.Serialize(new IntelligenceEvent(IntelligenceEventType.Result, "done", "done"),
                ArcanumJsonContext.Default.IntelligenceEvent);
            return new(HttpStatusCode.OK) { Content = new StringContent(frame + "\n", Encoding.UTF8, "application/x-ndjson") };
        }

        if (path.EndsWith("/entries", StringComparison.Ordinal))
        {
            return Json(new ApiResponse<EntryDto[]>([], true, null), ArcanumJsonContext.Default.ApiResponseEntryDtoArray);
        }

        if (path == "/api/sessions")
        {
            return Json(new ApiResponse<SessionQueryResult>(new([], null, false), true, null), ArcanumJsonContext.Default.ApiResponseSessionQueryResult);
        }

        return new(HttpStatusCode.NotFound);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        internal CommandCenterState State { get; } = new(new SessionLogBuffer());
        internal Channel<CommandCenterUiUpdate> Ui { get; } = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        internal CancellationTokenSource Cancellation { get; } = new();
        internal CommandCenterWindow Window { get; } = new();
        internal IApplication App { get; } = Application.Create().Init("ansi");
        internal CommandCenterHost Host { get; }

        internal TestCapturingLogger<CommandCenterHost> Logger { get; } = new();
        internal SessionWorkspaceService Workspace => _provider.GetRequiredService<SessionWorkspaceService>();

        internal Harness(Handler handler)
        {
            ServiceCollection services = new();
            CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());
            services.RemoveAll<Microsoft.Extensions.Logging.ILogger<CommandCenterHost>>();
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<CommandCenterHost>>(Logger);
            services.RemoveAll<ArcanumApiClient>();
            services.AddSingleton(new ArcanumApiClient(new Factory(handler), ArcanumApiCredentialLeaseTestFactory.Create("test-key")));
            services.RemoveAll<ILastSessionStore>();
            services.AddSingleton<ILastSessionStore>(new LastSessionStore());
            _provider = services.BuildServiceProvider();
            Host = (CommandCenterHost)_provider.GetRequiredService<ICommandCenterHost>();
            Window.ApplyAbsoluteLayout(120, 40);
        }

        internal Task Dispatch(CommandCenterAction action) => (Task)typeof(CommandCenterHost)
            .GetMethod("DispatchActionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Host, [action, State, Ui.Writer, App, Window, Cancellation, (Action)(() => throw new InvalidOperationException("Unexpected submit."))])!;
        internal Task Submit(string text) => (Task)typeof(CommandCenterHost)
            .GetMethod("HandleSubmitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Host, [text, State, Ui.Writer, App, Window, Cancellation])!;
        internal void Pump() => App.TimedEvents!.RunTimers();
        public async ValueTask DisposeAsync()
        {
            Cancellation.Cancel();
            await _provider.DisposeAsync();
            Window.Dispose();
            App.Dispose();
            Cancellation.Dispose();
        }
    }

    private sealed class LastSessionStore : ILastSessionStore
    {
        public Guid? GetLastSessionId() => null;
        public Task<ArcanumClientMutationResult<CliContextDocument>> SaveSessionIdAsync(Guid id,
            Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync, CancellationToken cancellationToken) =>
            Task.FromResult(ArcanumClientMutationResult<CliContextDocument>.Completed(CliContextDocument.Empty with { SessionId = id }));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal ConcurrentQueue<string> Paths { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Enqueue(request.RequestUri!.AbsolutePath);
            return respond(request, cancellationToken);
        }
    }
}
