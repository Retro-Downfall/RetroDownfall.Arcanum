using System.Runtime.InteropServices;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Infrastructure;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterTerminationTests
{
    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Stands in for the grace timer so a test decides when the grace "elapses" instead of racing a real
    /// clock: the delay completes only when the test says so, or is cancelled with the token it was given.
    /// </summary>
    private sealed class ManualGrace
    {
        private readonly List<TaskCompletionSource> _delays = [];

        public int Armed
        {
            get
            {
                lock (_delays)
                {
                    return _delays.Count;
                }
            }
        }

        public Task Delay(TimeSpan grace, CancellationToken cancellationToken)
        {
            TaskCompletionSource delay = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = cancellationToken.Register(() => delay.TrySetCanceled(cancellationToken));

            lock (_delays)
            {
                _delays.Add(delay);
            }

            return delay.Task;
        }

        public void ElapseAll()
        {
            lock (_delays)
            {
                foreach (TaskCompletionSource delay in _delays)
                {
                    _ = delay.TrySetResult();
                }
            }
        }

        public Task First()
        {
            lock (_delays)
            {
                return _delays[0].Task;
            }
        }
    }

    [Theory]
    [InlineData(PosixSignal.SIGTERM)]
    [InlineData(PosixSignal.SIGHUP)]
    public void A_termination_signal_cancels_the_launch_token_and_survives_the_signal(PosixSignal signal)
    {
        using CommandCenterTermination termination = new(TimeSpan.FromMinutes(5), _ => { }, registerSignals: false);
        PosixSignalContext context = new(signal);

        Assert.False(termination.Token.IsCancellationRequested);

        termination.OnSignal(context);

        // Surviving the signal is what lets the host unwind, stop the auto-launched server and restore
        // the terminal; the default action would end the process on the spot.
        Assert.True(context.Cancel);
        Assert.True(termination.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(PosixSignal.SIGTERM, 143)]
    [InlineData(PosixSignal.SIGHUP, 129)]
    public async Task A_launch_that_does_not_unwind_within_the_grace_is_ended_with_the_signals_exit_code(
        PosixSignal signal,
        int expectedExitCode)
    {
        TaskCompletionSource<int> forced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ManualGrace grace = new();
        using CommandCenterTermination termination = new(
            TimeSpan.FromSeconds(10),
            code => forced.TrySetResult(code),
            registerSignals: false,
            delay: grace.Delay);

        termination.OnSignal(new PosixSignalContext(signal));
        Assert.False(forced.Task.IsCompleted);

        grace.ElapseAll();

        Assert.Equal(expectedExitCode, await forced.Task.WaitAsync(AsyncTestTimeout));
    }

    [Fact]
    public async Task A_launch_that_unwinds_in_time_is_never_force_ended()
    {
        int forced = 0;
        ManualGrace grace = new();
        CommandCenterTermination termination = new(
            TimeSpan.FromSeconds(10),
            _ => Interlocked.Increment(ref forced),
            registerSignals: false,
            delay: grace.Delay);

        termination.OnSignal(new PosixSignalContext(PosixSignal.SIGTERM));
        termination.Dispose();

        // Disposing the launch is what cancels the grace timer; the timer's own end proves nothing was
        // left to run, so there is no real time to wait out.
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grace.First().WaitAsync(AsyncTestTimeout));
        Assert.Equal(0, forced);
    }

    [Fact]
    public async Task A_second_signal_does_not_start_a_second_backstop()
    {
        int forced = 0;
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ManualGrace grace = new();
        using CommandCenterTermination termination = new(
            TimeSpan.FromSeconds(10),
            code =>
            {
                _ = Interlocked.Increment(ref forced);
                _ = ended.TrySetResult();
            },
            registerSignals: false,
            delay: grace.Delay);

        termination.OnSignal(new PosixSignalContext(PosixSignal.SIGTERM));
        termination.OnSignal(new PosixSignalContext(PosixSignal.SIGHUP));

        Assert.Equal(1, grace.Armed);

        grace.ElapseAll();
        await ended.Task.WaitAsync(AsyncTestTimeout);

        Assert.Equal(1, forced);
    }

    /// <summary>
    /// Cancelling the token runs its callbacks on the signal thread, so a callback that blocks would keep a
    /// backstop that is armed afterwards from ever starting, which is the hung host the backstop exists
    /// for. The backstop has to be armed before the token is cancelled.
    /// </summary>
    [Fact]
    public async Task A_cancellation_callback_that_blocks_cannot_keep_the_backstop_from_arming()
    {
        TaskCompletionSource<int> forced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CommandCenterTermination termination = new(
            TimeSpan.FromMilliseconds(20),
            code => forced.TrySetResult(code),
            registerSignals: false);

        // A callback that never returns on its own: it waits for the backstop, far longer than the test.
        _ = termination.Token.Register(() => forced.Task.Wait(TimeSpan.FromMinutes(2)));

        _ = Task.Run(() => termination.OnSignal(new PosixSignalContext(PosixSignal.SIGTERM)));

        Assert.Equal(143, await forced.Task.WaitAsync(AsyncTestTimeout));
    }

    /// <summary>
    /// A callback that throws must not escape a signal handler: the handler has nowhere to put it, and the
    /// backstop still has to be armed.
    /// </summary>
    [Fact]
    public void A_cancellation_callback_that_throws_does_not_escape_the_signal_handler()
    {
        ManualGrace grace = new();
        using CommandCenterTermination termination = new(
            TimeSpan.FromSeconds(10),
            _ => { },
            registerSignals: false,
            delay: grace.Delay);
        _ = termination.Token.Register(static () => throw new InvalidOperationException("callback failed"));

        termination.OnSignal(new PosixSignalContext(PosixSignal.SIGTERM));

        Assert.True(termination.Token.IsCancellationRequested);
        Assert.Equal(1, grace.Armed);
    }

    /// <summary>
    /// The forced exit skips the host's cleanup, so it restores the terminal first: a hung interface leaves
    /// the shell with mouse reporting on and the alternate screen up.
    /// </summary>
    [Fact]
    public void The_forced_exit_restores_the_terminal_before_it_ends_the_process()
    {
        List<string> steps = [];

        CommandCenterTermination.EndProcess(
            143,
            restoreTerminal: () => steps.Add("restore"),
            exit: code => steps.Add($"exit {code}"));

        Assert.Equal(["restore", "exit 143"], steps);
    }

    /// <summary>
    /// The backstop exists for a hung host. A terminal that has stopped reading (flow control, a frozen
    /// emulator) blocks the restore's write, and a restore that is waited on without a bound would then
    /// keep the process from ever ending, which is what the backstop is there to prevent.
    /// </summary>
    [Fact]
    public async Task A_terminal_restore_that_never_returns_cannot_keep_the_process_from_ending()
    {
        using ManualResetEventSlim stuck = new();
        int exitedWith = -1;

        try
        {
            Task ended = Task.Run(() => CommandCenterTermination.EndProcess(
                143,
                restoreTerminal: () => stuck.Wait(),
                exit: code => exitedWith = code,
                restoreBudget: TimeSpan.FromMilliseconds(100)));

            Task finished = await Task.WhenAny(ended, Task.Delay(AsyncTestTimeout));

            Assert.Same(ended, finished);
            Assert.Equal(143, exitedWith);
        }
        finally
        {
            stuck.Set();
        }
    }

    [Fact]
    public void The_restore_is_waited_on_for_a_bounded_time_that_is_well_inside_the_grace_window()
    {
        Assert.True(CommandCenterTermination.TerminalRestoreBudget > TimeSpan.Zero);
        Assert.True(
            CommandCenterTermination.TerminalRestoreBudget < CliApplicationFactory.ProcessTerminationGrace);
    }

    [Fact]
    public void A_terminal_restore_that_throws_still_ends_the_process()
    {
        int exitedWith = -1;

        CommandCenterTermination.EndProcess(
            129,
            restoreTerminal: static () => throw new IOException("the terminal is gone"),
            exit: code => exitedWith = code);

        Assert.Equal(129, exitedWith);
    }

    [Fact]
    public void A_signal_this_platform_does_not_have_is_skipped_and_the_others_still_register()
    {
        List<PosixSignal> attempted = [];
        List<TestRegistration> registered = [];
        CommandCenterTermination termination = new(
            TimeSpan.FromMinutes(5),
            _ => { },
            registerSignals: true,
            registerSignal: (signal, _) =>
            {
                attempted.Add(signal);

                if (signal == PosixSignal.SIGHUP)
                {
                    throw new PlatformNotSupportedException("no SIGHUP here");
                }

                TestRegistration registration = new();
                registered.Add(registration);
                return registration;
            });

        Assert.Equal([PosixSignal.SIGTERM, PosixSignal.SIGHUP], attempted);
        TestRegistration only = Assert.Single(registered);
        Assert.False(only.Disposed);

        termination.Dispose();

        Assert.True(only.Disposed);
    }

    [Fact]
    public void Registering_for_the_real_signals_and_disposing_is_clean()
    {
        using CommandCenterTermination termination = new();

        Assert.True(termination.Token.CanBeCanceled);
        Assert.False(termination.Token.IsCancellationRequested);
    }

    private sealed class TestRegistration : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
