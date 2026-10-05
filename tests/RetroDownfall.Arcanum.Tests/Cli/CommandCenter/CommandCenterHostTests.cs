using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterHostTests
{
    [Theory]
    [InlineData(ServeLaunchStatus.Failed, CliExitCode.NetworkError)]
    [InlineData(ServeLaunchStatus.AuthFailed, CliExitCode.ConfigurationError)]
    public async Task Launch_failure_exits_before_command_center_refresh(
        ServeLaunchStatus status,
        CliExitCode expectedExitCode)
    {
        ServiceCollection services = new();
        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<IArcanumServeLauncher>();
        services.AddSingleton<IArcanumServeLauncher>(
            new FixedServeLauncher(status));

        RecordingConsole console = new();
        services.RemoveAll<IConsoleDispatcher>();
        services.AddSingleton<IConsoleDispatcher>(console);

        await using ServiceProvider provider = services.BuildServiceProvider();
        ICommandCenterHost host = provider.GetRequiredService<ICommandCenterHost>();

        int exitCode = await host.RunAsync(CancellationToken.None);

        Assert.Equal((int)expectedExitCode, exitCode);
        Assert.Contains("command center launch stopped", console.Diagnostics);
    }

    /// <summary>
    /// Ctrl+C during the pre-TUI startup (auto-serve readiness polling, MCP refresh, session
    /// restore) cancels the invocation token. The CLI contract fixes cancellation at 130, so the
    /// host must not fold that into its generic failure arm.
    /// </summary>
    [Fact]
    public async Task Cancelling_startup_exits_130_not_the_generic_failure_code()
    {
        ServiceCollection services = new();
        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());
        services.RemoveAll<IArcanumServeLauncher>();
        services.AddSingleton<IArcanumServeLauncher>(new CancellingServeLauncher());

        await using ServiceProvider provider = services.BuildServiceProvider();
        ICommandCenterHost host = provider.GetRequiredService<ICommandCenterHost>();

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        int exitCode = await host.RunAsync(cts.Token);

        Assert.Equal((int)CliExitCode.Cancelled, exitCode);
    }

    /// <summary>
    /// SIGTERM and SIGHUP cancel the token the launch hands the host; while the terminal UI owns the
    /// console that has to become a stop request on the UI thread, or the process keeps a dead run alive.
    /// </summary>
    [Fact]
    public async Task External_token_cancellation_requests_tui_stop()
    {
        using CancellationTokenSource external = new();
        using CancellationTokenSource run = new();
        using ManualResetEventSlim loopEntered = new();
        using ManualResetEventSlim loopStopped = new();
        int stopRequests = 0;
        int cleanups = 0;

        Task loop = Task.Run(() => CommandCenterHost.RunTerminalLoop(
            external.Token,
            run,
            invokeOnUiThread: static action => action(),
            requestStop: () =>
            {
                _ = Interlocked.Increment(ref stopRequests);
                loopStopped.Set();
            },
            runLoop: () =>
            {
                loopEntered.Set();
                _ = loopStopped.Wait(AsyncTestTimeout);
            },
            cleanup: () => Interlocked.Increment(ref cleanups)));

        Assert.True(loopEntered.Wait(AsyncTestTimeout), "The loop never started.");

        await external.CancelAsync();
        await loop.WaitAsync(AsyncTestTimeout);

        Assert.Equal(1, stopRequests);
        Assert.Equal(1, cleanups);
        Assert.True(run.IsCancellationRequested);
    }

    /// <summary>
    /// A runtime failure inside the loop used to skip everything after it: the run token stayed live, the
    /// UI pump and the thinking timer kept running, and the failure was reported as a start failure.
    /// </summary>
    [Fact]
    public void A_throwing_app_run_still_cancels_the_run_token()
    {
        using CancellationTokenSource external = new();
        using CancellationTokenSource run = new();
        int cleanups = 0;

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
            CommandCenterHost.RunTerminalLoop(
                external.Token,
                run,
                invokeOnUiThread: static action => action(),
                requestStop: static () => { },
                runLoop: static () => throw new InvalidOperationException("The loop failed."),
                cleanup: () => cleanups++));

        Assert.Equal("The loop failed.", thrown.Message);
        Assert.True(run.IsCancellationRequested);
        Assert.Equal(1, cleanups);
    }

    [Fact]
    public void A_token_cancelled_before_the_loop_starts_never_enters_it()
    {
        using CancellationTokenSource external = new();
        using CancellationTokenSource run = new();
        external.Cancel();
        bool entered = false;
        int cleanups = 0;

        CommandCenterHost.RunTerminalLoop(
            external.Token,
            run,
            invokeOnUiThread: static action => action(),
            requestStop: static () => { },
            runLoop: () => entered = true,
            cleanup: () => cleanups++);

        Assert.False(entered);
        Assert.True(run.IsCancellationRequested);
        Assert.Equal(1, cleanups);
    }

    /// <summary>
    /// A cancellation that arrives while the loop is already ending, or after it ended, must not throw
    /// into whatever cancelled it (a signal handler has nowhere to put the exception).
    /// </summary>
    [Fact]
    public async Task A_stop_request_that_fails_does_not_throw_into_the_canceller()
    {
        using CancellationTokenSource external = new();
        using CancellationTokenSource run = new();
        using ManualResetEventSlim loopEntered = new();
        using ManualResetEventSlim release = new();

        Task loop = Task.Run(() => CommandCenterHost.RunTerminalLoop(
            external.Token,
            run,
            invokeOnUiThread: static _ => throw new ObjectDisposedException("app"),
            requestStop: static () => { },
            runLoop: () =>
            {
                loopEntered.Set();
                _ = release.Wait(AsyncTestTimeout);
            },
            cleanup: static () => { }));

        Assert.True(loopEntered.Wait(AsyncTestTimeout), "The loop never started.");

        external.Cancel();
        release.Set();

        await loop.WaitAsync(AsyncTestTimeout);
    }

    [Fact]
    public void Each_app_run_failure_code_gets_its_own_diagnostic()
    {
        Assert.Equal(
            CommandCenterHost.DescribeTerminalTooSmall(70, 20),
            CommandCenterHost.DescribeAppRunFailure(CommandCenterApp.TooSmall, 70, 20));

        Assert.Equal(
            CommandCenterHost.StartFailureMessage,
            CommandCenterHost.DescribeAppRunFailure(CommandCenterApp.StartFailed, 0, 0));

        Assert.Equal(
            CommandCenterHost.RuntimeFailureMessage,
            CommandCenterHost.DescribeAppRunFailure(CommandCenterApp.CrashedAfterStart, 0, 0));

        Assert.NotEqual(CommandCenterHost.StartFailureMessage, CommandCenterHost.RuntimeFailureMessage);

        Assert.Null(CommandCenterHost.DescribeAppRunFailure(0, 0, 0));
        Assert.Null(CommandCenterHost.DescribeAppRunFailure((int)CliExitCode.Cancelled, 0, 0));
    }

    /// <summary>
    /// The terminal app reports a failure that happens after it has started with its own code, so the
    /// host can say the interface stopped unexpectedly instead of that it failed to start.
    /// </summary>
    [Fact]
    public void A_failure_after_the_app_started_is_reported_with_its_own_code()
    {
        CommandCenterApp app = new(NullLogger<CommandCenterApp>.Instance);

        int code = app.RunAfterStart(static () => throw new InvalidOperationException("boom"));

        Assert.Equal(CommandCenterApp.CrashedAfterStart, code);
        Assert.Equal(17, app.RunAfterStart(static () => 17));
    }

    /// <summary>
    /// When the action gate is contended, the continuation after the wait runs on a thread-pool thread.
    /// The window reads a gated session action decides from are therefore taken before the gate is
    /// awaited — on the caller's (UI) thread — and handed to the action as values.
    /// </summary>
    [Fact]
    public async Task Gated_session_actions_read_window_state_on_the_ui_thread()
    {
        ServiceCollection services = new();
        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());
        await using ServiceProvider provider = services.BuildServiceProvider();
        CommandCenterHost host = Assert.IsType<CommandCenterHost>(provider.GetRequiredService<ICommandCenterHost>());
        CommandCenterState state = new(new SessionLogBuffer());
        Channel<CommandCenterUiUpdate> ui = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        using ManualResetEventSlim holding = new();
        using ManualResetEventSlim release = new();

        Task holder = host.RunGatedAsync(
            state,
            ui.Writer,
            CancellationToken.None,
            async () =>
            {
                holding.Set();
                await Task.Run(() => release.Wait(AsyncTestTimeout));
            });

        Assert.True(holding.Wait(AsyncTestTimeout), "The first action never took the gate.");

        int uiThreadId = 0;
        int captureThreadId = -1;
        int actionThreadId = -1;
        int observed = 0;
        Task? gated = null;

        Thread uiThread = new(() =>
        {
            uiThreadId = global::System.Environment.CurrentManagedThreadId;
            gated = host.RunGatedAsync(
                state,
                ui.Writer,
                CancellationToken.None,
                capture: () =>
                {
                    captureThreadId = global::System.Environment.CurrentManagedThreadId;
                    return 42;
                },
                action: input =>
                {
                    actionThreadId = global::System.Environment.CurrentManagedThreadId;
                    observed = input;
                    return Task.CompletedTask;
                });
        });

        uiThread.Start();
        Assert.True(uiThread.Join(AsyncTestTimeout), "The caller was blocked by the contended gate.");

        // The gate is held, so the action has not run — but the window has already been read, on the
        // thread that pressed the key.
        Assert.Equal(uiThreadId, captureThreadId);
        Assert.Equal(-1, actionThreadId);

        release.Set();
        await holder.WaitAsync(AsyncTestTimeout);
        await gated!.WaitAsync(AsyncTestTimeout);

        Assert.Equal(42, observed);
        Assert.NotEqual(uiThreadId, actionThreadId);
    }

    [Fact]
    public void Window_state_is_read_once_and_the_transcript_copy_is_taken_only_for_a_transcript_pick()
    {
        Guid selected = Guid.NewGuid();
        RecordingWindow window = new() { ComposerHasText = true, SelectedSession = selected, LogIndex = 3, LogLines = ["a", "b"] };
        CommandCenterState state = new(new SessionLogBuffer());

        SessionActionWindowState composerFocus = SessionActionWindowState.Capture(window, state);

        Assert.True(composerFocus.ComposerHasText);
        Assert.Equal(selected, composerFocus.SelectedSessionId);
        Assert.Equal(-1, composerFocus.SelectedLogIndex);
        Assert.Empty(composerFocus.LogLines);
        Assert.Equal(0, window.LogSnapshotReads);

        state.FocusRegion = CommandCenterFocusRegion.Transcript;

        SessionActionWindowState transcriptFocus = SessionActionWindowState.Capture(window, state);

        Assert.Equal(3, transcriptFocus.SelectedLogIndex);
        Assert.Equal(["a", "b"], transcriptFocus.LogLines);
        Assert.Equal(1, window.LogSnapshotReads);
    }

    private sealed class RecordingWindow : ICommandCenterSessionActionWindow
    {
        public bool ComposerHasText { get; init; }

        public Guid? SelectedSession { get; init; }

        public int LogIndex { get; init; }

        public IReadOnlyList<string> LogLines { get; init; } = [];

        public int LogSnapshotReads { get; private set; }

        public int GetSelectedLogIndex() => LogIndex;

        public IReadOnlyList<string> GetLogLinesSnapshot()
        {
            LogSnapshotReads++;
            return LogLines;
        }

        public Guid? GetSelectedSessionId(CommandCenterState state) => SelectedSession;
    }

    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(30);

    private sealed class CancellingServeLauncher : IArcanumServeLauncher
    {
        public Task<ServeLaunchResult> EnsureRunningAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                new ServeLaunchResult(
                    ServeLaunchStatus.AlreadyRunning,
                    HealthProbeState.Healthy,
                    TimeSpan.Zero,
                    null,
                    null));
        }
    }

    private sealed class FixedServeLauncher(ServeLaunchStatus status)
        : IArcanumServeLauncher
    {
        public Task<ServeLaunchResult> EnsureRunningAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                new ServeLaunchResult(
                    status,
                    status == ServeLaunchStatus.AuthFailed
                        ? HealthProbeState.Unauthorized
                        : HealthProbeState.ConnectionRefused,
                    TimeSpan.Zero,
                    null,
                    "command center launch stopped"));
        }
    }

    private sealed class RecordingConsole : IConsoleDispatcher
    {
        internal List<string> Diagnostics { get; } = [];

        public void WritePayload(string value)
        {
        }

        public void WriteDiagnostic(string value) => Diagnostics.Add(value);

        public void WriteVerbose(string value)
        {
        }

        public void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo)
        {
        }

        public void WriteJson(JsonElement value)
        {
        }

        public void BeginJsonStream()
        {
        }
    }
}
