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
    private readonly CancellationTokenSource _source = new();

    private readonly CancellationTokenSource _unwound = new();

    private readonly List<PosixSignalRegistration> _registrations = [];

    private readonly TimeSpan _grace;

    private readonly Action<int> _forceExit;

    private int _requested;

    private int _disposed;

    public CommandCenterTermination()
        : this(
            CliApplicationFactory.ProcessTerminationGrace,
            static code => Environment.Exit(code),
            registerSignals: true)
    {
    }

    internal CommandCenterTermination(TimeSpan grace, Action<int> forceExit, bool registerSignals)
    {
        ArgumentNullException.ThrowIfNull(forceExit);

        _grace = grace;
        _forceExit = forceExit;

        if (registerSignals)
        {
            Register(PosixSignal.SIGTERM);
            Register(PosixSignal.SIGHUP);
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

        foreach (PosixSignalRegistration registration in _registrations)
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
            _registrations.Add(PosixSignalRegistration.Create(signal, OnSignal));
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
            _source.Cancel();

            _ = Task.Delay(_grace, _unwound.Token)
                .ContinueWith(
                    _ => _forceExit(forcedExitCode),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default);
        }
        catch (ObjectDisposedException)
        {
            // The launch already finished and disposed these sources; there is nothing left to stop.
        }
    }

    private static int ExitCodeFor(PosixSignal signal) =>
        signal switch
        {
            PosixSignal.SIGHUP => 129,
            PosixSignal.SIGTERM => 143,
            _ => 1,
        };
}
