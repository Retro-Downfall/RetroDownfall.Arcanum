using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

internal enum CappedChildProcessOutcome
{

    Completed,

    TimedOut,

    Canceled,

    CanceledBeforeStart,

    FailedToStart,

    IoErrorOnStart,

    AccessDeniedOnStart,

    IoErrorReadingOutput,

    AccessDeniedReadingOutput,

    CanceledWhileReadingOutput,

}

internal readonly record struct CappedStreamOutput(string Text, bool Truncated);

internal sealed class CappedChildProcessRunResult
{

    internal CappedChildProcessOutcome Outcome { get; init; }

    internal CappedStreamOutput Stdout { get; init; }

    internal CappedStreamOutput Stderr { get; init; }

    internal int ExitCode { get; init; }

    internal long PerStreamCapBytes { get; init; }

    internal Exception? FaultException { get; init; }

}

internal static class CappedChildProcessRunner
{

    internal static async Task<CappedChildProcessRunResult> RunAsync(
        ProcessStartInfo startInfo,
        ChildProcessEnvironmentProfile environmentProfile,
        long totalOutputCapBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {

        ChildProcessEnvironmentScrubber.ApplyProfile(startInfo, environmentProfile);

        long perStreamCapBytes = totalOutputCapBytes / 2L;

        if (perStreamCapBytes < 1024L)
        {

            perStreamCapBytes = 1024L;

        }

        using Process process = new();

        process.StartInfo = startInfo;

        using CancellationTokenSource timeoutCts = new(timeout);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        CancellationToken waitToken = linked.Token;

        try
        {

            if (!process.Start())
            {

                return new CappedChildProcessRunResult
                {

                    Outcome = CappedChildProcessOutcome.FailedToStart,

                    PerStreamCapBytes = perStreamCapBytes,

                };

            }

        }
        catch (IOException ex)
        {

            return new CappedChildProcessRunResult
            {

                Outcome = CappedChildProcessOutcome.IoErrorOnStart,

                PerStreamCapBytes = perStreamCapBytes,

                FaultException = ex,

            };

        }
        catch (UnauthorizedAccessException ex)
        {

            return new CappedChildProcessRunResult
            {

                Outcome = CappedChildProcessOutcome.AccessDeniedOnStart,

                PerStreamCapBytes = perStreamCapBytes,

                FaultException = ex,

            };

        }
        catch (OperationCanceledException)
        {

            return new CappedChildProcessRunResult
            {

                Outcome = CappedChildProcessOutcome.CanceledBeforeStart,

                PerStreamCapBytes = perStreamCapBytes,

            };

        }
        catch (InvalidOperationException ex)
        {

            return new CappedChildProcessRunResult
            {

                Outcome = CappedChildProcessOutcome.FailedToStart,

                PerStreamCapBytes = perStreamCapBytes,

                FaultException = ex,

            };

        }
        catch (Win32Exception ex)
        {

            return new CappedChildProcessRunResult
            {

                Outcome = CappedChildProcessOutcome.FailedToStart,

                PerStreamCapBytes = perStreamCapBytes,

                FaultException = ex,

            };

        }

        CancellationTokenRegistration killRegistration = waitToken.Register(
            static state => TryKillProcessEntireTree((Process)state!),
            process);

        try
        {

            Task<CappedStreamOutput> stdoutTask = ReadStreamCappedAsync(process.StandardOutput, perStreamCapBytes, waitToken);

            Task<CappedStreamOutput> stderrTask = ReadStreamCappedAsync(process.StandardError, perStreamCapBytes, waitToken);

            try
            {

                await process.WaitForExitAsync(waitToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException)
            {

                TryKillProcessEntireTree(process);

                await ObserveStreamReadTasksAsync(stdoutTask, stderrTask).ConfigureAwait(false);

                if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {

                    return new CappedChildProcessRunResult
                    {

                        Outcome = CappedChildProcessOutcome.TimedOut,

                        PerStreamCapBytes = perStreamCapBytes,

                    };

                }

                return new CappedChildProcessRunResult
                {

                    Outcome = CappedChildProcessOutcome.Canceled,

                    PerStreamCapBytes = perStreamCapBytes,

                };

            }

            CappedStreamOutput stdout;

            CappedStreamOutput stderr;

            try
            {

                stdout = await stdoutTask.ConfigureAwait(false);

                stderr = await stderrTask.ConfigureAwait(false);

            }
            catch (IOException ex)
            {

                return new CappedChildProcessRunResult
                {

                    Outcome = CappedChildProcessOutcome.IoErrorReadingOutput,

                    PerStreamCapBytes = perStreamCapBytes,

                    FaultException = ex,

                };

            }
            catch (UnauthorizedAccessException ex)
            {

                return new CappedChildProcessRunResult
                {

                    Outcome = CappedChildProcessOutcome.AccessDeniedReadingOutput,

                    PerStreamCapBytes = perStreamCapBytes,

                    FaultException = ex,

                };

            }
            catch (OperationCanceledException)
            {

                return new CappedChildProcessRunResult
                {

                    Outcome = CappedChildProcessOutcome.CanceledWhileReadingOutput,

                    PerStreamCapBytes = perStreamCapBytes,

                };

            }

            return new CappedChildProcessRunResult
            {

                Outcome = CappedChildProcessOutcome.Completed,

                Stdout = stdout,

                Stderr = stderr,

                ExitCode = process.ExitCode,

                PerStreamCapBytes = perStreamCapBytes,

            };

        }
        finally
        {

            await killRegistration.DisposeAsync().ConfigureAwait(false);

        }

    }

    private static async Task ObserveStreamReadTasksAsync(
        Task<CappedStreamOutput> stdoutTask,
        Task<CappedStreamOutput> stderrTask)
    {

        try
        {

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        }
        catch (IOException)
        {

        }
        catch (UnauthorizedAccessException)
        {

        }
        catch (OperationCanceledException)
        {

        }

    }

    private static async Task<CappedStreamOutput> ReadStreamCappedAsync(
        StreamReader reader,
        long maxBytes,
        CancellationToken cancellationToken)
    {

        StringBuilder builder = new();

        char[] buffer = new char[4096];

        long approximateBytes = 0L;

        bool truncated = false;

        while (true)
        {

            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {

                break;

            }

            long encodedSize = Encoding.UTF8.GetByteCount(buffer, 0, read);

            if (approximateBytes + encodedSize > maxBytes)
            {

                long remaining = maxBytes - approximateBytes;

                if (remaining > 0)
                {

                    int safeChars = ChooseSafeCharCount(buffer, read, remaining);

                    builder.Append(buffer, 0, safeChars);

                }

                truncated = true;

                break;

            }

            builder.Append(buffer, 0, read);

            approximateBytes += encodedSize;

        }

        return new CappedStreamOutput(builder.ToString(), truncated);

    }

    private static int ChooseSafeCharCount(char[] buffer, int charCount, long remainingBytes)
    {

        long running = 0L;

        for (int i = 0; i < charCount; i++)
        {

            int charByteSize = Encoding.UTF8.GetByteCount(buffer, i, 1);

            if (running + charByteSize > remainingBytes)
            {

                return i;

            }

            running += charByteSize;

        }

        return charCount;

    }

    private static void TryKillProcessEntireTree(Process process)
    {

        try
        {

            if (!process.HasExited)
            {

                process.Kill(entireProcessTree: true);

            }

        }
        catch (InvalidOperationException)
        {

        }
        catch (Win32Exception)
        {

        }
        catch (NotSupportedException)
        {

            try
            {

                process.Kill();

            }
            catch (Exception)
            {

            }

        }

    }

}
