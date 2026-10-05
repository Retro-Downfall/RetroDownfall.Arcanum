using System.Runtime.InteropServices;
using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterTerminationTests
{
    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(30);

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
        using CommandCenterTermination termination = new(
            TimeSpan.FromMilliseconds(50),
            code => forced.TrySetResult(code),
            registerSignals: false);

        termination.OnSignal(new PosixSignalContext(signal));

        Assert.Equal(expectedExitCode, await forced.Task.WaitAsync(AsyncTestTimeout));
    }

    [Fact]
    public async Task A_launch_that_unwinds_in_time_is_never_force_ended()
    {
        int forced = 0;
        CommandCenterTermination termination = new(
            TimeSpan.FromMilliseconds(150),
            _ => Interlocked.Increment(ref forced),
            registerSignals: false);

        termination.OnSignal(new PosixSignalContext(PosixSignal.SIGTERM));
        termination.Dispose();

        await Task.Delay(500);

        Assert.Equal(0, forced);
    }

    [Fact]
    public async Task A_second_signal_does_not_start_a_second_backstop()
    {
        int forced = 0;
        using CommandCenterTermination termination = new(
            TimeSpan.FromMilliseconds(50),
            _ => Interlocked.Increment(ref forced),
            registerSignals: false);

        termination.OnSignal(new PosixSignalContext(PosixSignal.SIGTERM));
        termination.OnSignal(new PosixSignalContext(PosixSignal.SIGHUP));

        await Task.Delay(500);

        Assert.Equal(1, forced);
    }

    [Fact]
    public void Registering_for_the_real_signals_and_disposing_is_clean()
    {
        using CommandCenterTermination termination = new();

        Assert.True(termination.Token.CanBeCanceled);
        Assert.False(termination.Token.IsCancellationRequested);
    }
}
