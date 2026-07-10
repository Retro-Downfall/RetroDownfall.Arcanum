namespace RetroDownfall.TheForge.Ux.Services.Terminal;

/// <summary>
/// Runs a single user shell command in a working directory, streaming stdout/stderr.
/// UI-agnostic; does not call Arcanum APIs.
/// </summary>
public interface ITerminalCommandRunner
{

    Task<TerminalCommandResult> RunAsync(
        string command,
        string workingDirectory,
        IProgress<TerminalOutputEvent>? progress,
        CancellationToken cancellationToken);

}
