using System.Diagnostics;
using System.Text;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.Services.Terminal;

/// <summary>
/// Spawns the platform shell with <see cref="ProcessStartInfo.ArgumentList"/>, streams stdout/stderr
/// concurrently, and supports cancellation via process-tree kill. Does not throw for Stop.
/// </summary>
public sealed class TerminalCommandRunner : ITerminalCommandRunner
{
    internal const int MaxOutputLineChars = 64 * 1024;

    /// <summary>
    /// How long the stdout/stderr readers are given to reach EOF after the shell has exited. Redirected
    /// pipes stay open while any descendant still holds the inherited write end, so an unbounded wait
    /// wedges The Hearth forever whenever a command leaves a background child behind.
    /// </summary>
    internal static readonly TimeSpan ReaderDrainTimeout = TimeSpan.FromSeconds(2);

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

        ProcessStartInfo startInfo = BuildStartInfo(shell, command, workingDirectory);

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

            StreamReader stdoutReader = process.StandardOutput;

            StreamReader stderrReader = process.StandardError;

            Task stdoutTask = ReadLinesAsync(
                stdoutReader,
                TerminalOutputKind.StandardOutput,
                progress,
                CancellationToken.None);

            Task stderrTask = ReadLinesAsync(
                stderrReader,
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

            await DrainReadersAsync(stdoutReader, stderrReader, stdoutTask, stderrTask).ConfigureAwait(false);

            if (cancelled || cancellationToken.IsCancellationRequested)
            {

                return TerminalCommandResult.CancelledResult();

            }

            return TerminalCommandResult.Completed(process.ExitCode);

        }

    }

    internal static ProcessStartInfo BuildStartInfo(
        TerminalShellSpec shell,
        string command,
        string workingDirectory)
    {

        ProcessStartInfo startInfo = new()
        {
            FileName = shell.FileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // cmd.exe does not parse its command line by MSVCRT rules, but ArgumentList always builds one that
        // way — an interior double quote becomes \" and, because the line then holds more than two quote
        // characters, cmd falls back to stripping only the outermost pair and runs the backslashes
        // verbatim. `/S /C "<command>"` makes that strip unconditional and correct, so the operator's
        // command reaches the shell exactly as typed. Arguments and ArgumentList are mutually exclusive.
        if (IsCommandPromptShell(shell.FileName))
        {

            startInfo.Arguments = BuildCommandPromptArguments(shell.ArgumentPrefix, command);

            return startInfo;

        }

        foreach (string arg in shell.ArgumentPrefix)
        {

            startInfo.ArgumentList.Add(arg);

        }

        startInfo.ArgumentList.Add(command);

        return startInfo;

    }

    private static bool IsCommandPromptShell(string fileName) =>
        string.Equals(
            Path.GetFileNameWithoutExtension(fileName),
            "cmd",
            StringComparison.OrdinalIgnoreCase);

    private static string BuildCommandPromptArguments(IReadOnlyList<string> argumentPrefix, string command)
    {

        StringBuilder builder = new();

        foreach (string arg in argumentPrefix)
        {

            if (string.Equals(arg, "/C", StringComparison.OrdinalIgnoreCase))
            {

                builder.Append("/S ");

            }

            builder.Append(arg).Append(' ');

        }

        builder.Append('"').Append(command).Append('"');

        return builder.ToString();

    }

    /// <summary>
    /// Waits a bounded window for both readers to finish, then closes our read ends so any pending read
    /// on a pipe an orphaned grandchild still holds unwinds instead of blocking forever. The reader tasks
    /// swallow the resulting <see cref="ObjectDisposedException"/>/<see cref="IOException"/> and are abandoned.
    /// </summary>
    private static async Task DrainReadersAsync(
        StreamReader stdoutReader,
        StreamReader stderrReader,
        Task stdoutTask,
        Task stderrTask)
    {

        try
        {

            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(ReaderDrainTimeout).ConfigureAwait(false);

        }
        catch (TimeoutException)
        {

            CloseQuietly(stdoutReader);

            CloseQuietly(stderrReader);

        }

    }

    private static void CloseQuietly(StreamReader reader)
    {

        try
        {

            reader.Close();

        }
        catch (Exception)
        {

            // Best-effort: the stream may already be torn down.

        }

    }

    internal static async Task ReadLinesAsync(
        StreamReader reader,
        TerminalOutputKind kind,
        IProgress<TerminalOutputEvent>? progress,
        CancellationToken cancellationToken)
    {

        try
        {
            BoundedTextLineReader lineReader = new(reader, MaxOutputLineChars);

            while (true)
            {

                BoundedTextLineReadResult read = await lineReader
                    .ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (!read.HasLine)
                {

                    break;

                }

                string line = read.IsTooLong
                    ? $"{read.Line} … [output line truncated]"
                    : read.Line;

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
