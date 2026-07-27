using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;

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

    /// <summary>OS-level resource limits could not be applied before start, or the child could not be assigned to a Windows Job Object after start; when assignment fails the process tree is killed so the child is never left running unbounded.</summary>
    ResourceLimitApplyFailed,

    /// <summary>The process was killed by the kernel for exceeding an OS-enforced resource limit (CPU time or memory).</summary>
    ResourceLimitExceeded,

    /// <summary>
    /// Filesystem sandbox could not be applied and <c>AllowUnsandboxedToolChildren</c> is false;
    /// the process was never started.
    /// </summary>
    FilesystemSandboxUnavailable,

    /// <summary>
    /// Windows + Sanctum path-boundary enforcement: no FS jail available; process never started.
    /// </summary>
    FilesystemSandboxDeniedByWindowsSanctum,

    /// <summary>
    /// A caller-owned trusted executable/SDK/cache identity check failed after sandbox/resource
    /// preparation and immediately before Process.Start; the child was never spawned.
    /// </summary>
    PreStartValidationFailed,

}

internal readonly record struct CappedStreamOutput(string Text, bool Truncated);

internal readonly record struct CappedChildProcessPreStartValidationResult(
    bool Success,
    string? Error,
    string? Code = null);

internal enum ChildProcessSandboxDeniedRootKind
{
    Source,
    PackageCache,
    Other,
}

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

    /// <summary>
    /// Model-safe detail when <see cref="Outcome"/> is
    /// <see cref="CappedChildProcessOutcome.FilesystemSandboxUnavailable"/> or
    /// <see cref="CappedChildProcessOutcome.FilesystemSandboxDeniedByWindowsSanctum"/>.
    /// </summary>
    internal string? FilesystemSandboxDenialMessage { get; init; }

    internal string? PreStartValidationError { get; init; }

    internal string? PreStartValidationCode { get; init; }

    internal ChildProcessSandboxDeniedRootKind?
        SandboxDeniedRoot { get; init; }

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
        CancellationToken cancellationToken,
        ChildProcessSandboxRequest? filesystemSandbox = null,
        ILogger? logger = null,
        Func<CappedChildProcessPreStartValidationResult>? preStartValidation = null,
        Func<TimeSpan>? getCleanupTimeRemaining = null)
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

        // FS jail wraps the post-rlimit StartInfo (macOS sandbox-exec around the ulimit prelude when
        // present; Linux Landlock inactive for macOS-ARM beta). Order: env scrub → rlimits → FS jail → start → Job assign.
        ChildProcessSandboxApplyResult? sandboxResult = null;

        if (filesystemSandbox is not null)
        {

            sandboxResult = ChildProcessFilesystemJail.Apply(startInfo, filesystemSandbox, logger);

            if (sandboxResult.Status == ChildProcessSandboxApplyStatus.Unavailable)
            {

                await CleanupSandboxTempPathsAsync(
                        sandboxResult,
                        getCleanupTimeRemaining,
                        logger)
                    .ConfigureAwait(false);

                return new CappedChildProcessRunResult
                {

                    Outcome = CappedChildProcessOutcome.FilesystemSandboxUnavailable,

                    PerStreamCapBytes = perStreamCapBytes,

                    FilesystemSandboxDenialMessage = string.IsNullOrWhiteSpace(sandboxResult.Detail)
                        ? ChildProcessSandboxMessages.SandboxUnavailable
                        : sandboxResult.Detail + " " + ChildProcessSandboxMessages.NotNetworkIsolationNote,

                };

            }

            if (sandboxResult.Status == ChildProcessSandboxApplyStatus.DeniedByWindowsSanctum)
            {

                await CleanupSandboxTempPathsAsync(
                        sandboxResult,
                        getCleanupTimeRemaining,
                        logger)
                    .ConfigureAwait(false);

                return new CappedChildProcessRunResult
                {

                    Outcome = CappedChildProcessOutcome.FilesystemSandboxDeniedByWindowsSanctum,

                    PerStreamCapBytes = perStreamCapBytes,

                    FilesystemSandboxDenialMessage = ChildProcessSandboxMessages.WindowsSanctumPathBoundaryDenied,

                };

            }

            if (filesystemSandbox.RequireAppliedFilesystemJail
                && sandboxResult.Status != ChildProcessSandboxApplyStatus.Applied)
            {

                await CleanupSandboxTempPathsAsync(
                        sandboxResult,
                        getCleanupTimeRemaining,
                        logger)
                    .ConfigureAwait(false);

                return new CappedChildProcessRunResult
                {

                    Outcome = CappedChildProcessOutcome.FilesystemSandboxUnavailable,

                    PerStreamCapBytes = perStreamCapBytes,

                    FilesystemSandboxDenialMessage =
                        "workspace_check requires an active filesystem jail; the process was not started. "
                        + ChildProcessSandboxMessages.NotNetworkIsolationNote,

                };

            }

            // Applied (macOS Seatbelt), NoFilesystemJail (Windows Job Objects only), and
            // EscapedByOperator continue — never treat NoFilesystemJail as Applied FS confinement.

        }

        UnixProcessGroupSupervisor.Apply(startInfo);

        using Process process = new();

        process.StartInfo = startInfo;

        using CancellationTokenSource timeoutCts = new(timeout);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        CancellationToken waitToken = linked.Token;

        // The cgroup scope directory (if any) is created by resourceLimiter.Apply above, before
        // Process.Start() is even attempted. Every exit path from this point on — including every
        // Start() failure caught below — must still run limiterResult.CleanupAsync, so the whole
        // remainder of the method is wrapped in this outer try/finally rather than relying on the
        // inner try/finally (around the main run logic) alone, which a Start() failure would bypass
        // entirely, leaking an empty, process-less cgroup directory.
        int startedPid = -1;

        int? unixProcessGroupId = null;
        MacOsDescendantSupervisor? descendantSupervisor = null;
        bool descendantContainmentVerified = true;

        try
        {

            if (cancellationToken.IsCancellationRequested)
            {

                return new CappedChildProcessRunResult
                {

                    Outcome =
                        CappedChildProcessOutcome.CanceledBeforeStart,

                    PerStreamCapBytes = perStreamCapBytes,

                };
            }

            if (preStartValidation is not null)
            {

                CappedChildProcessPreStartValidationResult validation;

                try
                {

                    validation = preStartValidation();
                }
                catch (Exception)
                {

                    validation = new CappedChildProcessPreStartValidationResult(
                        false,
                        "Trusted process identity validation failed.");
                }

                if (!validation.Success)
                {

                    return new CappedChildProcessRunResult
                    {

                        Outcome =
                            CappedChildProcessOutcome.PreStartValidationFailed,

                        PerStreamCapBytes = perStreamCapBytes,

                        PreStartValidationError = validation.Error,

                        PreStartValidationCode = validation.Code,

                    };
                }

            }

            if (cancellationToken.IsCancellationRequested)
            {

                return new CappedChildProcessRunResult
                {
                    Outcome =
                        CappedChildProcessOutcome.CanceledBeforeStart,
                    PerStreamCapBytes = perStreamCapBytes,
                };
            }

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

            // Captured immediately after a successful Start() so it is available to any later
            // diagnostics; the cgroup cleanup callback itself is keyed off the pre-generated scope
            // path rather than this pid (see ProcessResourceLimiter.ApplyOnLinux), so it does not
            // actually need a live pid to clean up correctly.
            startedPid = process.Id;

            unixProcessGroupId = UnixProcessGroup.TryCreate(startedPid);
            descendantSupervisor =
                MacOsDescendantSupervisor.TryStart(startedPid);

            if (OperatingSystem.IsMacOS()
                && filesystemSandbox?.ToolName
                    == ToolRiskClassifier.WorkspaceCheckToolName
                && descendantSupervisor is null)
            {
                logger?.LogWarning(
                    "workspace_check descendant monitoring could not attach; process-group cleanup remains active.");
            }

            // Windows Job Objects: AssignProcessToJobObject must run immediately after Start and
            // before any stdout/stderr wait. .NET cannot create the child suspended, so there is a
            // brief post-start race before the job binds (DESIGN §11.15). Failure here is fail-closed:
            // kill the tree and surface ResourceLimitApplyFailed — never leave the child unbounded.
            if (limiterResult.AssignAfterStart is not null)
            {

                ResourceLimitError? assignError = limiterResult.AssignAfterStart(process);

                if (assignError is not null)
                {

                    ProcessTreeKiller.TryKillEntireTree(
                        process,
                        context: "execute_command/run_spell_script (Job Object assign failed)");

                    return new CappedChildProcessRunResult
                    {

                        Outcome = CappedChildProcessOutcome.ResourceLimitApplyFailed,

                        PerStreamCapBytes = perStreamCapBytes,

                        ResourceLimitApplyError = assignError.Message,

                    };

                }

            }

            CancellationTokenRegistration killRegistration = waitToken.Register(
                static state =>
                {

                    (Process child, int? groupId, MacOsDescendantSupervisor? supervisor) =
                        ((Process Child, int? GroupId, MacOsDescendantSupervisor? Supervisor))state!;
                    supervisor?.KillTracked();
                    UnixProcessGroup.TryTerminateAndKill(
                        groupId);
                    ProcessTreeKiller.TryKillEntireTree(
                        child,
                        context: "execute_command/run_spell_script");

                },
                (process, unixProcessGroupId, descendantSupervisor));

            try
            {

                Task<CappedStreamOutput> stdoutTask = ReadStreamCappedAsync(
                    process.StandardOutput,
                    perStreamCapBytes,
                    cancellationToken);

                Task<CappedStreamOutput> stderrTask = ReadStreamCappedAsync(
                    process.StandardError,
                    perStreamCapBytes,
                    cancellationToken);

                try
                {

                    await process.WaitForExitAsync(waitToken).ConfigureAwait(false);

                }
                catch (OperationCanceledException)
                {

                    UnixProcessGroup.TryKill(unixProcessGroupId);

                    ProcessTreeKiller.TryKillEntireTree(process, context: "execute_command/run_spell_script");
                    if (descendantSupervisor is not null)
                    {
                        descendantContainmentVerified =
                            await descendantSupervisor
                            .StopKillAndVerifyAsync(
                                TimeSpan.FromSeconds(2))
                            .ConfigureAwait(false);
                    }

                    (CappedStreamOutput canceledStdout, CappedStreamOutput canceledStderr) =
                        await CompleteStreamReadTasksAsync(
                            stdoutTask,
                            stderrTask).ConfigureAwait(false);

                    if (!descendantContainmentVerified)
                    {
                        logger?.LogWarning(
                            "workspace_check descendant cleanup could not verify quiescence after cancellation.");
                    }

                    if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {

                        return new CappedChildProcessRunResult
                        {

                            Outcome = CappedChildProcessOutcome.TimedOut,

                            Stdout = canceledStdout,

                            Stderr = canceledStderr,

                            PerStreamCapBytes = perStreamCapBytes,

                        };

                    }

                    return new CappedChildProcessRunResult
                    {

                        Outcome = CappedChildProcessOutcome.Canceled,

                        Stdout = canceledStdout,

                        Stderr = canceledStderr,

                        PerStreamCapBytes = perStreamCapBytes,

                    };

                }

                // A successful parent exit is not proof that descendants exited. The captured
                // process group remains addressable after the original pid is gone.
                UnixProcessGroup.TryKill(unixProcessGroupId);
                if (descendantSupervisor is not null)
                {
                    descendantContainmentVerified =
                        await descendantSupervisor
                        .StopKillAndVerifyAsync(
                            TimeSpan.FromSeconds(2))
                        .ConfigureAwait(false);
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

                if (!descendantContainmentVerified)
                {
                    logger?.LogWarning(
                        "workspace_check descendant cleanup could not verify quiescence after process exit.");
                }

                ResourceLimitKind? exceededResource = await CheckSignalKillAsync(exitCode, resourceLimits, limiterResult)
                    .ConfigureAwait(false);

                return new CappedChildProcessRunResult
                {

                    Outcome = exceededResource is not null
                        ? CappedChildProcessOutcome.ResourceLimitExceeded
                        : CappedChildProcessOutcome.Completed,

                    Stdout = stdout,

                    Stderr = stderr,

                    ExitCode = exitCode,

                    PerStreamCapBytes = perStreamCapBytes,

                    SandboxDeniedRoot = ClassifySandboxDenial(
                        stdout.Text,
                        stderr.Text,
                        filesystemSandbox),

                    ExceededResource = exceededResource,

                };

            }
            finally
            {

                UnixProcessGroup.TryKill(unixProcessGroupId);

                await killRegistration.DisposeAsync().ConfigureAwait(false);

            }

        }
        finally
        {
            if (descendantSupervisor is not null)
            {
                await descendantSupervisor.DisposeAsync()
                    .ConfigureAwait(false);
            }

            await CleanupSandboxTempPathsAsync(
                    sandboxResult,
                    getCleanupTimeRemaining,
                    logger)
                .ConfigureAwait(false);

            if (limiterResult.CleanupAsync is not null)
            {

                try
                {

                    await limiterResult.CleanupAsync(startedPid)
                        .WaitAsync(
                            GetCleanupTimeRemaining(
                                getCleanupTimeRemaining))
                        .ConfigureAwait(false);

                }
                catch (TimeoutException)
                {

                    logger?.LogWarning(
                        "Timed out cleaning the child-process resource limiter scope.");

                }

            }

        }

    }

    private static async Task CleanupSandboxTempPathsAsync(
        ChildProcessSandboxApplyResult? sandboxResult,
        Func<TimeSpan>? getCleanupTimeRemaining,
        ILogger? logger)
    {
        bool cleaned =
            await ChildProcessFilesystemJail.CleanupTempPathsAsync(
                    sandboxResult?.TempPathsToCleanup,
                    GetCleanupTimeRemaining(
                        getCleanupTimeRemaining))
                .ConfigureAwait(false);

        if (!cleaned)
        {
            logger?.LogWarning(
                "Timed out cleaning child-process sandbox temporary paths.");
        }
    }

    private static TimeSpan GetCleanupTimeRemaining(
        Func<TimeSpan>? getCleanupTimeRemaining)
    {
        if (getCleanupTimeRemaining is null)
        {
            return TimeSpan.FromSeconds(5);
        }

        try
        {
            TimeSpan remaining = getCleanupTimeRemaining();
            return remaining > TimeSpan.Zero
                ? remaining
                : TimeSpan.Zero;
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private static ChildProcessSandboxDeniedRootKind?
        ClassifySandboxDenial(
            string standardOutput,
            string standardError,
            ChildProcessSandboxRequest? sandbox)
    {
        if (sandbox?.RequireAppliedFilesystemJail != true)
        {
            return null;
        }

        string combined = standardOutput + "\n" + standardError;
        bool denied = combined.Contains(
                "Operation not permitted",
                StringComparison.OrdinalIgnoreCase)
            || combined.Contains(
                "Permission denied",
                StringComparison.OrdinalIgnoreCase)
            || combined.Contains(
                "read-only file system",
                StringComparison.OrdinalIgnoreCase)
            || (combined.Contains(
                    "Access to the path",
                    StringComparison.OrdinalIgnoreCase)
                && combined.Contains(
                    "is denied",
                    StringComparison.OrdinalIgnoreCase))
            || combined.Contains(
                "Could not write lines to file",
                StringComparison.OrdinalIgnoreCase);

        if (!denied)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(sandbox.SourceReadOnlyRoot)
            && combined.Contains(
                sandbox.SourceReadOnlyRoot,
                StringComparison.Ordinal))
        {
            return ChildProcessSandboxDeniedRootKind.Source;
        }

        if (!string.IsNullOrEmpty(sandbox.PackageReadOnlyRoot)
            && combined.Contains(
                sandbox.PackageReadOnlyRoot,
                StringComparison.Ordinal))
        {
            return ChildProcessSandboxDeniedRootKind.PackageCache;
        }

        return ChildProcessSandboxDeniedRootKind.Other;
    }

    /// <summary>
    /// Maps a child process's exit code to the OS-enforced resource limit it indicates was
    /// exceeded, accounting for both signal-reporting conventions that can occur here: the shell
    /// (<c>ulimit</c> prelude) convention of <c>128 + signal</c>, and a direct kernel report of the
    /// negative signal number (observed when the tracked pid is signal-killed directly, e.g. after
    /// the prelude's <c>exec</c> has replaced the shell's process image with the real target).
    /// SIGXCPU (24), SIGKILL (9), and SIGSEGV (11) are POSIX-standard and identical on macOS and Linux.
    /// On Windows, maps the common Job Object memory-kill NTSTATUS <c>STATUS_QUOTA_EXCEEDED</c>
    /// (<c>0xC0000044</c>) to <see cref="ResourceLimitKind.Memory"/> when a memory limit was configured.
    /// </summary>
    /// <remarks>
    /// Only classifies the exit as a resource-limit breach when the corresponding limit was actually
    /// configured (&gt; 0) for this invocation — otherwise a script that happens to exit with a
    /// look-alike code (e.g. <c>exit(137)</c> for its own reasons, or a system-wide, unrelated OOM
    /// kill while no Sanctum memory cap was set) would be misreported as a Sanctum breach.
    ///
    /// For memory (signal 9/11), when <paramref name="limiterResult"/> exposes authoritative cgroup
    /// OOM evidence (<see cref="ProcessResourceLimiterResult.WasOomKilledAsync"/> — Linux with a
    /// cgroups v2 scope), that evidence is required before attributing the kill to the configured
    /// limit: a bare exit code cannot otherwise distinguish a Sanctum-enforced OOM kill from an
    /// unrelated external <c>kill -9</c> or a system-wide OOM event outside this process's cgroup.
    /// Falls back to the exit-code-only heuristic when no such evidence is available (macOS, Windows
    /// Job Objects, or Linux without a usable cgroup).
    /// </remarks>
    private static async Task<ResourceLimitKind?> CheckSignalKillAsync(
        int exitCode,
        ResourceLimits? resourceLimits,
        ProcessResourceLimiterResult limiterResult)
    {

        if (resourceLimits is null)
        {

            return null;

        }

        if (OperatingSystem.IsWindows())
        {

            return ClassifyWindowsJobExit(exitCode, resourceLimits);

        }

        int signal = exitCode switch
        {
            < 0 => -exitCode,
            > 128 => exitCode - 128,
            _ => 0,
        };

        if (signal == 24 && resourceLimits.MaxCpuSeconds > 0)
        {

            return ResourceLimitKind.Cpu;

        }

        if (signal is 9 or 11 && resourceLimits.MaxMemoryMb > 0)
        {

            if (limiterResult.WasOomKilledAsync is not null)
            {

                bool confirmedOomKill = await limiterResult.WasOomKilledAsync().ConfigureAwait(false);

                return confirmedOomKill ? ResourceLimitKind.Memory : null;

            }

            return ResourceLimitKind.Memory;

        }

        return null;

    }

    private static ResourceLimitKind? ClassifyWindowsJobExit(int exitCode, ResourceLimits resourceLimits)
    {

        // Job Object process/job memory violations commonly surface as STATUS_QUOTA_EXCEEDED.
        // CPU-time kills do not have a stable, documented exit code we can trust across Windows
        // versions, so wall-clock timeout remains the reliable attribution path for CPU on Windows.
        if (exitCode == WindowsJobObjectInterop.StatusQuotaExceeded
            && (resourceLimits.MaxMemoryMb > 0 || resourceLimits.MaxProcessMemoryMb > 0))
        {

            return ResourceLimitKind.Memory;

        }

        return null;

    }

    private static async Task<(CappedStreamOutput Stdout, CappedStreamOutput Stderr)>
        CompleteStreamReadTasksAsync(
        Task<CappedStreamOutput> stdoutTask,
        Task<CappedStreamOutput> stderrTask)
    {

        CappedStreamOutput stdout = default;
        CappedStreamOutput stderr = default;

        try
        {

            stdout = await stdoutTask
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

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
        catch (TimeoutException)
        {

        }

        try
        {

            stderr = await stderrTask
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

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
        catch (TimeoutException)
        {

        }

        return (stdout, stderr);
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

                    int safeChars = Utf8Truncation.ChooseSafeCharCount(buffer.AsSpan(0, read), remaining);

                    builder.Append(buffer, 0, safeChars);

                }

                truncated = true;

                // The cap has been reached, but the child process may still be writing to this
                // pipe. If nobody keeps reading, the OS pipe buffer (a few tens of KB) fills and
                // the child blocks on its next write — a de facto hang until the invocation
                // timeout kills the whole tree, even though the output we care about has already
                // been captured. Keep draining (and discarding) the rest of the stream in the
                // background so the child can finish/exit naturally; this method still returns
                // immediately with the truncated output captured so far.
                _ = DrainAndDiscardAsync(reader, cancellationToken);

                break;

            }

            builder.Append(buffer, 0, read);

            approximateBytes += encodedSize;

        }

        return new CappedStreamOutput(builder.ToString(), truncated);

    }

    /// <summary>
    /// Keeps reading (and discarding) a stream after its output cap has been reached, so the
    /// underlying OS pipe never backs up and blocks the child process's writes — see
    /// <see cref="ReadStreamCappedAsync"/>. Best-effort and fire-and-forget: the cap's output has
    /// already been captured and returned to the caller by the time this runs, so any failure here
    /// (including cancellation, or the stream/process already being gone) simply stops draining.
    /// </summary>
    private static async Task DrainAndDiscardAsync(StreamReader reader, CancellationToken cancellationToken)
    {

        try
        {

            char[] buffer = new char[8192];

            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
            {

            }

        }
        catch (Exception)
        {

            // Best-effort drain — see remarks above.

        }

    }

}
