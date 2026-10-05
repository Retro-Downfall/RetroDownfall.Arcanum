namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// What a Ctrl+C does while Command Center start-up (auto-serve, MCP refresh, Session restore) is still
/// running and the terminal UI does not yet own the console.
/// </summary>
/// <remarks>
/// The first interrupt asks start-up to unwind through its cancellation token, so the usual cleanup runs
/// and the launch exits 130. It is absorbed: the process does not end on the spot. A start-up step that
/// never observes the token (a credential read parked on an operating-system approval, a stalled mount)
/// would then leave the operator with no way out, so any later interrupt is not absorbed and the default
/// action ends the process, as Ctrl+C did before the launch took the signal over.
/// </remarks>
internal sealed class CommandCenterStartUpInterrupt(Action cancelStartUp)
{
    private int _interrupts;

    /// <summary>
    /// Handles one interrupt. Returns <see langword="true"/> when it was absorbed (start-up is unwinding),
    /// and <see langword="false"/> when the default action should end the process.
    /// </summary>
    public bool Absorb()
    {
        if (Interlocked.Increment(ref _interrupts) > 1)
        {
            return false;
        }

        try
        {
            cancelStartUp();
        }
        catch (ObjectDisposedException)
        {
            // The run already finished; there is nothing left to cancel.
        }

        return true;
    }
}
