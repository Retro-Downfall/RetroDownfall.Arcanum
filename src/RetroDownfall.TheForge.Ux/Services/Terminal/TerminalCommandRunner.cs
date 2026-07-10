using System.Diagnostics;

namespace RetroDownfall.TheForge.Ux.Services.Terminal;

/// <summary>
/// Spawns the platform shell with <see cref="ProcessStartInfo.ArgumentList"/>, streams stdout/stderr
/// concurrently, and supports cancellation via process-tree kill. Does not throw for Stop.
/// </summary>
public sealed class TerminalCommandRunner : ITerminalCommandRunner
{

    private readonly ITerminalShellResolver _shellResolver;

    public TerminalCommandRunner(ITerminalShellResolver shellResolver)
    {

        _shellResolver = shellResolver;

    }

    public async Task<TerminalCommandResult> RunAsync(
        string command,
        string workingDirectory,
        IProgress<TerminalOutputEvent>? progress,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(command))
        {

            return TerminalCommandResult.Failed("Command is empty.");

        }

        TerminalShellSpec shell = _shellResolver.Resolve();

        ProcessStartInfo startInfo = new()
        {
            FileName = shell.FileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string arg in shell.ArgumentPrefix)
        {

            startInfo.ArgumentList.Add(arg);

        }

        startInfo.ArgumentList.Add(command);

        Process process;

        try
        {

            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start returned null.");

        }
        catch (Exception ex)
        {

            string message = $"Failed to start shell '{shell.FileName}': {ex.Message}";

            progress?.Report(new TerminalOutputEvent(message, TerminalOutputKind.StandardError));

            return TerminalCommandResult.Failed(message);

        }

        using (process)
        {

            Task stdoutTask = ReadLinesAsync(
                process.StandardOutput,
                TerminalOutputKind.StandardOutput,
                progress,
                CancellationToken.None);

            Task stderrTask = ReadLinesAsync(
                process.StandardError,
                TerminalOutputKind.StandardError,
                progress,
                CancellationToken.None);

            bool cancelled = false;

            try
            {

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException)
            {

                cancelled = true;

                TerminalProcessTreeKiller.TryKillEntireTree(process);

                try
                {

                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

                }
                catch (Exception)
                {

                    // Best-effort wait after kill.

                }

            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            if (cancelled || cancellationToken.IsCancellationRequested)
            {

                return TerminalCommandResult.CancelledResult();

            }

            return TerminalCommandResult.Completed(process.ExitCode);

        }

    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        TerminalOutputKind kind,
        IProgress<TerminalOutputEvent>? progress,
        CancellationToken cancellationToken)
    {

        try
        {

            while (true)
            {

                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line is null)
                {

                    break;

                }

                progress?.Report(new TerminalOutputEvent(line, kind));

            }

        }
        catch (OperationCanceledException)
        {

            // Reader cancelled with process teardown.

        }
        catch (ObjectDisposedException)
        {

            // Stream closed after kill.

        }
        catch (IOException)
        {

            // Stream broken after kill.

        }

    }

}
