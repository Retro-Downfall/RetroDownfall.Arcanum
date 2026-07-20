namespace RetroDownfall.TheForge.Ux.Services.Git;

/// <summary>
/// Spawns <c>git</c> with per-argument <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> —
/// never a shell, never Hearth's <c>-lc</c> command string. Desktop-local only (The Ledger).
/// </summary>
public interface IGitProcessRunner
{

    Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

}
