using System.Runtime.InteropServices;
using RetroDownfall.Arcanum.Cli.Infrastructure;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Owns the cancellation token a bare or deep-link launch hands the Command Center, and cancels it when
/// the process is asked to end by SIGTERM or SIGHUP.
/// </summary>
/// <remarks>
/// A terminal Ctrl+C reaches the process as a key, and the Command Center handles it itself. SIGTERM
/// (a service manager, <c>kill</c>) and SIGHUP (the terminal window closing) are different: the default
/// action ends the process on the spot, skipping the host's cleanup and leaving an auto-launched server
/// running. The handlers here survive the signal so that cleanup can run, and cancel the token the host
/// watches. Because surviving a signal also means a hung host could ignore it for ever, a backstop ends
/// the process with the conventional <c>128 + signal</c> code if the launch has not unwound within the
/// grace window.
/// </remarks>
internal sealed class CommandCenterTermination : IDisposable
{
    /// <summary>
    /// The longest the backstop waits for the terminal restore before it ends the process regardless; the
    /// restore is a handful of bytes, so a terminal that has not taken them in this long is not reading.
    /// </summary>
    internal static readonly TimeSpan TerminalRestoreBudget = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _source = new();

    private readonly CancellationTokenSource _unwound = new();

    private readonly List<IDisposable> _registrations = [];

    private readonly TimeSpan _grace;

    private readonly Action<int> _forceExit;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly Func<PosixSignal, Action<PosixSignalContext>, IDisposable> _registerSignal;

    private int _requested;

    private int _disposed;

    public CommandCenterTermination()
        : this(
            CliApplicationFactory.ProcessTerminationGrace,
            static code => EndProcess(code, CommandCenterApp.RestoreTerminalModes, Environment.Exit),
            registerSignals: true)
    {
    }

    /// <param name="grace">How long the launch has to unwind before the backstop ends the process.</param>
    /// <param name="forceExit">Ends the process with the given code; replaced by a test.</param>
    /// <param name="registerSignals">Whether to register for the real signals.</param>
    /// <param name="delay">The grace timer; replaced by a test so it never races a real clock.</param>
    /// <param name="registerSignal">Registers one signal handler; replaced by a test.</param>
    internal CommandCenterTermination(
        TimeSpan grace,
        Action<int> forceExit,
        bool registerSignals,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<PosixSignal, Action<PosixSignalContext>, IDisposable>? registerSignal = null)
    {
        ArgumentNullException.ThrowIfNull(forceExit);

        _grace = grace;
        _forceExit = forceExit;
        _delay = delay ?? Task.Delay;
        _registerSignal = registerSignal ?? RegisterWithOperatingSystem;

        if (registerSignals)
        {
            Register(PosixSignal.SIGTERM);
            Register(PosixSignal.SIGHUP);
        }
    }

    /// <summary>
    /// The backstop's exit. It skips the host's cleanup, which is the point of a backstop, so it restores
    /// the terminal itself first: a hung interface would otherwise leave the shell with mouse reporting on
    /// and the alternate screen up.
    /// </summary>
    /// <remarks>
    /// The backstop is for a host that has hung, and the restore is a write to the terminal, which can hang
    /// too: a terminal that has stopped reading (flow control, a frozen emulator) blocks the write, and
    /// the thread that holds the console's lock may be the stuck interface itself. So the restore runs on
    /// its own thread and is waited on for at most <paramref name="restoreBudget"/>; the process ends
    /// whether the restore finished, failed or is still blocked.
    /// </remarks>
    /// <param name="exitCode">The conventional <c>128 + signal</c> code to end the process with.</param>
    /// <param name="restoreTerminal">Puts the terminal's modes back; replaced by a test.</param>
    /// <param name="exit">Ends the process with the given code; replaced by a test.</param>
    /// <param name="restoreBudget">
    /// The longest the restore is waited on; <see cref="TerminalRestoreBudget"/> when omitted.
    /// </param>
    internal static void EndProcess(
        int exitCode,
        Action restoreTerminal,
        Action<int> exit,
        TimeSpan? restoreBudget = null)
    {
        ArgumentNullException.ThrowIfNull(restoreTerminal);
        ArgumentNullException.ThrowIfNull(exit);

        // A dedicated thread, not the pool: a hung host can have starved the pool, and a restore stuck in a
        // write must not be able to hold anything the exit needs. It is a background thread, so a restore
        // that never returns cannot keep the process alive either.
        Thread restore = new(() => RestoreBestEffort(restoreTerminal))
        {
            IsBackground = true,
            Name = "Command Center terminal restore",
        };

        restore.Start();

        _ = restore.Join(restoreBudget ?? TerminalRestoreBudget);

        exit(exitCode);
    }

    private static void RestoreBestEffort(Action restoreTerminal)
    {
        try
        {
            restoreTerminal();
        }
        catch (Exception)
        {
            // The terminal is already unreliable and the process is about to end; a failed restore must not
            // be what keeps it from ending, and there is nowhere left to report it.
        }
    }

    /// <summary>Cancelled when the process is asked to end by SIGTERM or SIGHUP.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>The handler for a registered termination signal.</summary>
    internal void OnSignal(PosixSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Survive the signal: the default action would end the process before the host can clean up.
        context.Cancel = true;

        RequestStop(ExitCodeFor(context.Signal));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The launch unwound: the backstop must not end a process that is already finishing.
        _unwound.Cancel();

        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }

        _source.Dispose();
        _unwound.Dispose();
    }

    private void Register(PosixSignal signal)
    {
        try
        {
            _registrations.Add(_registerSignal(signal, OnSignal));
        }
        catch (PlatformNotSupportedException)
        {
            // This platform has no such signal; the other registration and Ctrl+Q still end the run.
        }
    }

    private void RequestStop(int forcedExitCode)
    {
        if (Interlocked.Exchange(ref _requested, 1) != 0)
        {
            return;
        }

        try
        {
            // Armed before the token is cancelled: cancelling runs the token's callbacks inline on this
            // thread, so one that blocks would otherwise keep the backstop from ever starting, and a
            // hung host is exactly what the backstop is for.
            _ = _delay(_grace, _unwound.Token)
                .ContinueWith(
                    _ => _forceExit(forcedExitCode),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default);

            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The launch already finished and disposed these sources; there is nothing left to stop.
        }
        catch (AggregateException)
        {
            // A cancellation callback threw. The token is cancelled and the backstop is armed; the
            // signal handler that called this has nowhere to put the exception.
        }
    }

    private static IDisposable RegisterWithOperatingSystem(PosixSignal signal, Action<PosixSignalContext> handler) =>
        PosixSignalRegistration.Create(signal, handler);

    private static int ExitCodeFor(PosixSignal signal) =>
        signal switch
        {
            PosixSignal.SIGHUP => 129,
            PosixSignal.SIGTERM => 143,
            _ => 1,
        };
}
