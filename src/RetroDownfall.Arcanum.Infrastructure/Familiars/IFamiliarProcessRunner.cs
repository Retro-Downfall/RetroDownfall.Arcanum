namespace RetroDownfall.Arcanum.Infrastructure.Familiars;

/// <summary>
/// Spawns the operator's installed vendor CLI and reads its NDJSON. This is the only place in
/// Arcanum that starts a Familiar, so the spawn discipline — argument list rather than command
/// string, scrubbed environment, bounded deadline, kill-tree on the way out — lives here once
/// instead of at each call site.
/// </summary>
/// <remarks>
/// The MCP stdio layer is a deliberate non-dependency. That is JSON-RPC with a handshake and cursor
/// semantics over a long-lived server; a Familiar is a one-shot process per inference turn. The
/// spawn discipline is shared; the protocol is not.
/// </remarks>
public interface IFamiliarProcessRunner
{

    /// <summary>
    /// Streams non-blank stdout lines as the CLI writes them. Faults with
    /// <see cref="FamiliarProcessException"/> when the binary is missing, the OS refuses the spawn,
    /// the deadline passes, or the CLI exits non-zero — the last of these <em>after</em> yielding
    /// whatever frames did arrive, so a partial answer can never be mistaken for a complete one.
    /// </summary>
    IAsyncEnumerable<string> RunLinesAsync(
        FamiliarProcessRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs to completion and returns a classified outcome instead of throwing. Used by the status
    /// probe, which has to tell "not installed" apart from "installed but not signed in" and would
    /// otherwise be reading that distinction out of exception types.
    /// </summary>
    Task<FamiliarProcessOutput> RunToCompletionAsync(
        FamiliarProcessRequest request,
        CancellationToken cancellationToken);

}
