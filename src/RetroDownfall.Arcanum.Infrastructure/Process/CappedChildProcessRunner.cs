using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;

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

    /// <summary>OS-level resource limits could not be applied to <see cref="ProcessStartInfo"/> before start; the process was never started.</summary>
    ResourceLimitApplyFailed,

    /// <summary>The process was killed by the kernel for exceeding an OS-enforced resource limit (CPU time or memory).</summary>
    ResourceLimitExceeded,

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

    /// <summary>Non-sensitive detail when <see cref="Outcome"/> is <see cref="CappedChildProcessOutcome.ResourceLimitApplyFailed"/>.</summary>
    internal string? ResourceLimitApplyError { get; init; }

    /// <summary>Which resource was exceeded when <see cref="Outcome"/> is <see cref="CappedChildProcessOutcome.ResourceLimitExceeded"/>.</summary>
    internal ResourceLimitKind? ExceededResource { get; init; }

}

internal static class CappedChildProcessRunner
{

    internal static async Task<CappedChildProcessRunResult> RunAsync(
        ProcessStartInfo startInfo,
        ChildProcessEnvironmentProfile environmentProfile,
        long totalOutputCapBytes,
        TimeSpan timeout,
        ResourceLimits? resourceLimits,
        IProcessResourceLimiter? resourceLimiter,
        CancellationToken cancellationToken)
    {

        ChildProcessEnvironmentScrubber.ApplyProfile(startInfo, environmentProfile);

        long perStreamCapBytes = totalOutputCapBytes / 2L;

        if (perStreamCapBytes < 1024L)
        {

            perStreamCapBytes = 1024L;

        }

        // Call sites without Sanctum context (e.g. run_spell_script constructed directly in unit
        // tests, with no campaign/DI backing) legitimately have nothing to resolve limits from;
        // resource-limit enforcement is simply skipped for that invocation rather than applying an
        // unconfigured default.
        // Applied before Process.Start() so the rewritten StartInfo (potentially a ulimit shell
        // prelude — see ProcessResourceLimiter) is what actually gets launched.
        ProcessResourceLimiterResult limiterResult = resourceLimits is not null && resourceLimiter is not null
            ? resourceLimiter.Apply(startInfo, resourceLimits)
            : new ProcessResourceLimiterResult(null, null);

        if (limiterResult.Error is not null)
        {

            return new CappedChildProcessRunResult
            {

                Outcome = CappedChildProcessOutcome.ResourceLimitApplyFailed,

                PerStreamCapBytes = perStreamCapBytes,

                ResourceLimitApplyError = limiterResult.Error.Message,

            };

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

        // Captured immediately after a successful Start() so cgroup cleanup (if any) always runs
        // against the actual child pid, regardless of which return path below is taken.
        int startedPid = process.Id;

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

            int exitCode = process.ExitCode;

            ResourceLimitKind? exceededResource = CheckSignalKill(exitCode, resourceLimits);

            return new CappedChildProcessRunResult
            {

                Outcome = exceededResource is not null
                    ? CappedChildProcessOutcome.ResourceLimitExceeded
                    : CappedChildProcessOutcome.Completed,

                Stdout = stdout,

                Stderr = stderr,

                ExitCode = exitCode,

                PerStreamCapBytes = perStreamCapBytes,

                ExceededResource = exceededResource,

            };

        }
        finally
        {

            await killRegistration.DisposeAsync().ConfigureAwait(false);

            if (limiterResult.CleanupAsync is not null)
            {

                await limiterResult.CleanupAsync(startedPid).ConfigureAwait(false);

            }

        }

    }

    /// <summary>
    /// Maps a child process's exit code to the OS-enforced resource limit it indicates was
    /// exceeded, accounting for both signal-reporting conventions that can occur here: the shell
    /// (<c>ulimit</c> prelude) convention of <c>128 + signal</c>, and a direct kernel report of the
    /// negative signal number (observed when the tracked pid is signal-killed directly, e.g. after
    /// the prelude's <c>exec</c> has replaced the shell's process image with the real target).
    /// SIGXCPU (24), SIGKILL (9), and SIGSEGV (11) are POSIX-standard and identical on macOS and Linux.
    /// </summary>
    /// <remarks>
    /// Only classifies the exit as a resource-limit breach when the corresponding limit was actually
    /// configured (&gt; 0) for this invocation — otherwise a script that happens to exit with a
    /// look-alike code (e.g. <c>exit(137)</c> for its own reasons, or a system-wide, unrelated OOM
    /// kill while no Sanctum memory cap was set) would be misreported as a Sanctum breach.
    /// </remarks>
    private static ResourceLimitKind? CheckSignalKill(int exitCode, ResourceLimits? resourceLimits)
    {

        if (resourceLimits is null)
        {

            return null;

        }

        int signal = exitCode switch
        {
            < 0 => -exitCode,
            > 128 => exitCode - 128,
            _ => 0,
        };

        return signal switch
        {
            24 when resourceLimits.MaxCpuSeconds > 0 => ResourceLimitKind.Cpu,
            9 or 11 when resourceLimits.MaxMemoryMb > 0 => ResourceLimitKind.Memory,
            _ => null,
        };

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
