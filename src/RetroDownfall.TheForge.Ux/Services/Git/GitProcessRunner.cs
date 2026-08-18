using System.Diagnostics;
using System.Text;
using RetroDownfall.TheForge.Ux.Services.Terminal;

namespace RetroDownfall.TheForge.Ux.Services.Git;

/// <summary>
/// Runs <c>git</c> with <see cref="ProcessStartInfo.ArgumentList"/> only (no shell concatenation).
/// Bounds stdout/stderr length. Used exclusively by The Ledger — not Hearth.
/// </summary>
public sealed class GitProcessRunner : IGitProcessRunner
{

    public const int DefaultMaxOutputChars = 1_048_576;

    private readonly int _maxOutputChars;

    public GitProcessRunner(int maxOutputChars = DefaultMaxOutputChars)
    {

        _maxOutputChars = maxOutputChars > 0 ? maxOutputChars : DefaultMaxOutputChars;

    }

    public async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(arguments);

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {

            return GitProcessResult.Failed("Working directory is empty.", arguments);

        }

        string fullDirectory;

        try
        {

            fullDirectory = Path.GetFullPath(workingDirectory);

        }
        catch (Exception ex)
        {

            return GitProcessResult.Failed($"Invalid working directory: {ex.Message}", arguments);

        }

        if (!Directory.Exists(fullDirectory))
        {

            return GitProcessResult.Failed($"Working directory does not exist: {fullDirectory}", arguments);

        }

        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = fullDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string arg in arguments)
        {

            startInfo.ArgumentList.Add(arg);

        }

        Process process;

        try
        {

            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start returned null.");

        }
        catch (Exception ex)
        {

            return GitProcessResult.Failed($"Failed to start git: {ex.Message}", arguments);

        }

        using (process)
        {

            Task<(string Text, bool Truncated)> stdoutTask = ReadBoundedAsync(
                process.StandardOutput,
                _maxOutputChars,
                CancellationToken.None);

            Task<(string Text, bool Truncated)> stderrTask = ReadBoundedAsync(
                process.StandardError,
                _maxOutputChars,
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

            (string stdout, bool stdoutTruncated) = await stdoutTask.ConfigureAwait(false);

            (string stderr, bool stderrTruncated) = await stderrTask.ConfigureAwait(false);

            if (cancelled || cancellationToken.IsCancellationRequested)
            {

                return GitProcessResult.CancelledResult(arguments);

            }

            return GitProcessResult.Completed(
                process.ExitCode,
                stdout,
                stderr,
                arguments,
                stdoutTruncated,
                stderrTruncated);

        }

    }

    internal static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        StreamReader reader,
        int maxChars,
        CancellationToken cancellationToken)
    {

        StringBuilder builder = new(capacity: Math.Min(maxChars, 4096));

        char[] buffer = new char[4096];

        bool truncated = false;

        try
        {

            while (true)
            {

                int read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);

                if (read <= 0)
                {

                    break;

                }

                int remaining = maxChars - builder.Length;

                if (remaining <= 0)
                {

                    truncated = true;

                    // Drain the rest so the process does not block on a full pipe.

                    continue;

                }

                if (read > remaining)
                {

                    builder.Append(buffer, 0, remaining);

                    truncated = true;

                }
                else
                {

                    builder.Append(buffer, 0, read);

                }

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

        if (truncated)
        {

            TrimTrailingPartialLine(builder);

        }

        return (builder.ToString(), truncated);

    }

    /// <summary>
    /// Drops everything after the last newline. The cap is a character offset, so it can land mid-line —
    /// and a half path is still a syntactically valid porcelain row that The Ledger would then offer to
    /// diff or stage. Truncation is reported through <c>GitProcessResult.StdoutTruncated</c>/
    /// <c>StderrTruncated</c>; a human-readable notice must never be spliced into the captured output,
    /// because callers parse it as machine-readable git output.
    /// </summary>
    private static void TrimTrailingPartialLine(StringBuilder builder)
    {

        for (int i = builder.Length - 1; i >= 0; i--)
        {

            if (builder[i] == '\n')
            {

                builder.Length = i + 1;

                return;

            }

        }

        builder.Length = 0;

    }

}
