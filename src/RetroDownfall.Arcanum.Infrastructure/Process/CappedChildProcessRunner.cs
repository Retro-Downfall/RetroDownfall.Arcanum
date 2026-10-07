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

    /// <summary>
    /// Complete output crossed the in-memory preview boundary but could not be durably spilled;
    /// the process tree was killed instead of silently discarding diagnostics.
    /// </summary>
    OutputPreservationFailed,

    /// <summary>OS-level resource limits could not be applied before start, or the child could not be assigned to a Windows Job Object after start; when assignment fails the process tree is killed so the child is never left running unbounded.</summary>
    ResourceLimitApplyFailed,

    /// <summary>
    /// The macOS memory monitor stopped on a fault while the child was running, so nothing enforced the
    /// memory ceiling any more and the process tree was killed. Unlike
    /// <see cref="ResourceLimitApplyFailed"/> the child did start and run — for as long as the monitor
    /// lasted — so its work may be partly done and its output is incomplete.
    /// </summary>
    MemoryMonitorStopped,

    /// <summary>The process was killed by the kernel for exceeding an OS-enforced resource limit (CPU time or memory).</summary>
    ResourceLimitExceeded,

    /// <summary>
    /// Filesystem sandbox could not be applied and <c>AllowUnsandboxedToolChildren</c> is false;
    /// the process was never started.
    /// </summary>
    FilesystemSandboxUnavailable,

    /// <summary>
    /// Legacy Windows Sanctum denial outcome retained for result compatibility.
    /// </summary>
    FilesystemSandboxDeniedByWindowsSanctum,

    /// <summary>
    /// A caller-owned trusted executable/SDK/cache identity check failed after sandbox/resource
    /// preparation and immediately before Process.Start; the child was never spawned.
    /// </summary>
    PreStartValidationFailed,
}

/// <param name="ReadAbandoned">
/// The run completed but this pipe was still held open (by a descendant the command left running) when
/// the post-exit drain gave up on it: <see cref="Text"/> is empty, nothing was preserved, and the stream
/// is reported as truncated without a <see cref="CompleteOutputPath"/>.
/// </param>
internal readonly record struct CappedStreamOutput(
    string Text,
    bool Truncated,
    string? CompleteOutputPath = null,
    long TotalBytes = 0L,
    bool ReadAbandoned = false);

internal sealed class ChildProcessOutputPreservationException(
    Exception innerException) : IOException(
        "Complete child-process output could not be preserved.",
        innerException);

internal sealed class CommandOutputSpillLimitException(
    long attemptedBytes,
    long limitBytes) : IOException(
        $"Physical resource protection: the command-output artifact reached {attemptedBytes} bytes, exceeding the explicit Sanctum MaxFileWriteMb policy of {limitBytes} bytes. The partial artifact was deleted; rerun with quieter output or explicitly raise Sanctum MaxFileWriteMb.")
{
    internal long AttemptedBytes { get; } = attemptedBytes;

    internal long LimitBytes { get; } = limitBytes;
}

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
    /// <summary>
    /// How long the runner keeps draining stdout/stderr <em>after</em> the child itself has exited.
    /// Only a pipe still held open by a surviving descendant can outlast this: the kernel buffer a
    /// legitimately exited child leaves behind drains in microseconds. It matches the give-up bound
    /// the cancellation path already applies in <see cref="CompleteStreamReadTasksAsync"/>.
    /// </summary>
    private static readonly TimeSpan PostExitOutputDrainGrace =
        TimeSpan.FromSeconds(5);

    /// <summary>
    /// Second, deliberately short window granted to a reader after the runner has waited out
    /// <see cref="PostExitOutputDrainGrace"/> and swept the process group again — just long enough
    /// for a reader unblocked by that sweep to deliver its output before it is abandoned.
    /// </summary>
    private static readonly TimeSpan AbandonedOutputDrainRegrace =
        TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Status the Unix resource-limit prelude exits with when it refuses to exec because a limit
    /// could not be applied (see <see cref="ProcessResourceLimiterResult.PreExecFailureMarker"/>).
    /// </summary>
    private const int PreExecLimitFailureExitCode = 126;

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
        Func<TimeSpan>? getCleanupTimeRemaining = null,
        string? outputSpillDirectory = null,
        IReadOnlyCollection<string>? operatorDeclaredSecretEnvironmentVariables = null,
        Func<int, long?, MacOsDescendantSupervisor?>? descendantSupervisorFactory = null)
    {
        ChildProcessEnvironmentScrubber.ApplyProfile(
            startInfo,
            environmentProfile,
            operatorDeclaredSecretEnvironmentVariables);

        // No caller can feed a tool child input — RunAsync has no parameter for it — so fd 0 is
        // redirected unconditionally rather than left inherited. An unredirected child receives the
        // host's own stdin handle, and the `arcanum serve` daemon is started attached to the
        // operator's terminal (ServeProcessLauncher), so a child that reads stdin (`cat`, `git
        // commit`, `ssh`) would block on that terminal while every production caller passes
        // Timeout.InfiniteTimeSpan — a wedge with no deadline to break it. The write end is closed
        // immediately after start (CloseChildStandardInput), turning that hang into an instant EOF.
        startInfo.RedirectStandardInput = true;

        long perStreamCapBytes = totalOutputCapBytes / 2L;

        if (perStreamCapBytes < 1024L)
        {
            perStreamCapBytes = 1024L;
        }

        OutputSpillBudget? outputSpillBudget =
            !string.IsNullOrWhiteSpace(outputSpillDirectory)
            && resourceLimits?.MaxFileWriteMb > 0
                ? new OutputSpillBudget(
                    resourceLimits.MaxFileWriteMb * 1024L * 1024L)
                : null;

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

        // Everything from here on — every sandbox refusal, every Start() failure, every cancellation —
        // owes the limiter its cleanup (the cgroup scope directory exists from Apply onwards) and the
        // sandbox its temp artifacts, so the whole remainder of the method sits inside this one
        // try/finally instead of each early return remembering to clean up for itself.
        ChildProcessSandboxApplyResult? sandboxResult = null;

        int startedPid = -1;

        MacOsDescendantSupervisor? descendantSupervisor = null;

        try
        {
            // FS jail wraps the post-rlimit StartInfo (macOS sandbox-exec around the ulimit prelude when
            // present; Linux Landlock inactive for macOS-ARM beta). Order: env scrub → rlimits → FS jail → start → Job assign.
            if (filesystemSandbox is not null)
            {
                sandboxResult = ChildProcessFilesystemJail.Apply(startInfo, filesystemSandbox, logger);

                if (sandboxResult.Status == ChildProcessSandboxApplyStatus.Unavailable)
                {
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
                    return new CappedChildProcessRunResult
                    {
                        Outcome = CappedChildProcessOutcome.FilesystemSandboxUnavailable,

                        PerStreamCapBytes = perStreamCapBytes,

                        FilesystemSandboxDenialMessage =
                            "workspace_check requires an active filesystem jail; the process was not started. "
                            + ChildProcessSandboxMessages.NotNetworkIsolationNote,
                    };
                }

                // Applied OS jail and an explicitly accepted operator escape continue.
            }

            bool processGroupEstablishedByLauncher =
                UnixProcessGroupSupervisor.Apply(startInfo);

            using Process process = new();

            process.StartInfo = startInfo;

            using CancellationTokenSource timeoutCts = new(timeout);

            using CancellationTokenSource outputFailureCts = new();

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token,
                outputFailureCts.Token);

            CancellationToken waitToken = linked.Token;

            int? unixProcessGroupId = null;

            bool descendantContainmentVerified = true;

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

            CloseChildStandardInput(process, logger);

            // Only a launcher that put the child in its own group (Linux setsid) leaves the runner a group
            // to kill. setpgid on an already-started child is refused once it has exec'd, which it always
            // has by the time Process.Start returns, so there is no post-start fallback. On macOS a
            // cancellation or timeout is therefore the tree kill (SIGKILL to every descendant still attached
            // to the root, which never lets the launcher's EXIT trap run) plus the descendant supervisor's
            // identity tracking; the launcher's trap only covers the child's own exit.
            unixProcessGroupId = processGroupEstablishedByLauncher
                ? startedPid
                : null;
            descendantSupervisor = (descendantSupervisorFactory ?? DefaultDescendantSupervisorFactory)(
                startedPid,
                limiterResult.MonitoredMemoryLimitBytes);

            // macOS has no kernel memory ceiling for a child (RLIMIT_AS is rejected), so the
            // supervisor's footprint monitor is the ceiling. A child it could not attach to while
            // still running would run unbounded: kill it and fail closed. A child that already
            // exited cannot exceed anything, so there is nothing left to bound.
            if (limiterResult.MonitoredMemoryLimitBytes is not null)
            {
                if (descendantSupervisor is null
                    && !process.HasExited)
                {
                    UnixProcessGroup.TryKill(unixProcessGroupId);

                    ProcessTreeKiller.TryKillEntireTree(
                        process,
                        context: "execute_command/run_spell_script (memory monitor could not attach)");

                    return new CappedChildProcessRunResult
                    {
                        Outcome = CappedChildProcessOutcome.ResourceLimitApplyFailed,

                        PerStreamCapBytes = perStreamCapBytes,

                        ResourceLimitApplyError =
                            "The child-process memory monitor could not attach to the started process; the process was killed.",
                    };
                }

                MacOsDescendantSupervisor? memoryMonitor = descendantSupervisor;

                limiterResult = limiterResult with
                {
                    WasOomKilledAsync = () => Task.FromResult(memoryMonitor?.MemoryLimitExceeded == true),
                };
            }

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

                // The Windows broker waits for this before creating the untrusted target: job
                // membership alone cannot tell this run's job from one the host inherited.
                if (!ChildProcessFilesystemJail.ConfirmWindowsJobAssignment(sandboxResult))
                {
                    ProcessTreeKiller.TryKillEntireTree(
                        process,
                        context: "execute_command/run_spell_script (Job Object confirmation failed)");

                    return new CappedChildProcessRunResult
                    {
                        Outcome = CappedChildProcessOutcome.ResourceLimitApplyFailed,

                        PerStreamCapBytes = perStreamCapBytes,

                        ResourceLimitApplyError =
                            "execute_command: the sandbox broker could not be told it was assigned to the Sanctum Job Object; the command was not started.",
                    };
                }
            }

            CancellationTokenRegistration killRegistration = waitToken.Register(
                static state =>
                {
                    (Process child, int? groupId, MacOsDescendantSupervisor? supervisor) =
                        ((Process Child, int? GroupId, MacOsDescendantSupervisor? Supervisor))state!;
                    supervisor?.KillTracked();
                    // Not awaited: the grace must not hold the thread that is cancelling the run.
                    _ = UnixProcessGroup.TerminateAndKillAsync(
                        groupId);
                    ProcessTreeKiller.TryKillEntireTree(
                        child,
                        context: "execute_command/run_spell_script");
                },
                (process, unixProcessGroupId, descendantSupervisor));

            try
            {
                string? stdoutSpillPath = BuildOutputSpillPath(
                    outputSpillDirectory,
                    "stdout");

                string? stderrSpillPath = BuildOutputSpillPath(
                    outputSpillDirectory,
                    "stderr");

                Task<CappedStreamOutput> stdoutTask = ReadStreamCappedAsync(
                    process.StandardOutput,
                    perStreamCapBytes,
                    cancellationToken,
                    stdoutSpillPath,
                    outputSpillBudget);

                Task<CappedStreamOutput> stderrTask = ReadStreamCappedAsync(
                    process.StandardError,
                    perStreamCapBytes,
                    cancellationToken,
                    stderrSpillPath,
                    outputSpillBudget);

                CancelWaitWhenOutputReadFails(
                    stdoutTask,
                    outputFailureCts);

                CancelWaitWhenOutputReadFails(
                    stderrTask,
                    outputFailureCts);

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
                            await StopDescendantSupervisorAsync(
                                    descendantSupervisor,
                                    logger)
                                .ConfigureAwait(false);
                    }

                    (CappedStreamOutput canceledStdout, CappedStreamOutput canceledStderr) =
                        await CompleteStreamReadTasksAsync(
                            stdoutTask,
                            stderrTask).ConfigureAwait(false);

                    DeleteOutputSpillsWhenReadersComplete(
                        stdoutTask,
                        stdoutSpillPath,
                        stderrTask,
                        stderrSpillPath);

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

                    if (outputFailureCts.IsCancellationRequested)
                    {
                        Exception? outputFault = GetOutputReadFault(
                            stdoutTask,
                            stderrTask);

                        return new CappedChildProcessRunResult
                        {
                            Outcome = outputFault switch
                            {
                                ChildProcessOutputPreservationException =>
                                    CappedChildProcessOutcome.OutputPreservationFailed,
                                CommandOutputSpillLimitException =>
                                    CappedChildProcessOutcome.OutputPreservationFailed,
                                UnauthorizedAccessException =>
                                    CappedChildProcessOutcome.AccessDeniedReadingOutput,
                                _ => CappedChildProcessOutcome.IoErrorReadingOutput,
                            },

                            Stdout = canceledStdout,

                            Stderr = canceledStderr,

                            PerStreamCapBytes = perStreamCapBytes,

                            FaultException = outputFault,
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
                        await StopDescendantSupervisorAsync(
                                descendantSupervisor,
                                logger)
                            .ConfigureAwait(false);
                }

                CappedStreamOutput stdout;

                CappedStreamOutput stderr;

                try
                {
                    // One deadline for both pipes. Waiting for each in turn gave stderr a fresh grace once
                    // stdout reached EOF, so the documented 5 s bound was really up to 10 s.
                    (stdout, stderr) = await AwaitPostExitOutputAsync(
                            stdoutTask,
                            stderrTask,
                            PostExitOutputDrainGrace,
                            TimeProvider.System)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The child is gone but something still holds the write end of its pipes, so
                    // the readers will never see EOF. Every containment sweep above is best-effort
                    // (DESIGN §11.15), and once WaitForExitAsync has returned neither the timeout
                    // nor the caller's token reaches these reads — an unbounded await here wedges
                    // the tool call forever and, because the Job Object release, the AppContainer
                    // undo-log replay and the sandbox temp cleanup all live past the drain, leaks
                    // the run's OS resources for the lifetime of the host. Sweep once more to try
                    // to force EOF, then walk away with whatever arrived.
                    logger?.LogWarning(
                        "Child process {ProcessId} exited but its output pipes were still held open after {DrainSeconds}s; the drain was abandoned and the output is reported as truncated.",
                        startedPid,
                        PostExitOutputDrainGrace.TotalSeconds);

                    descendantSupervisor?.KillTracked();

                    await UnixProcessGroup.TerminateAndKillAsync(unixProcessGroupId)
                        .ConfigureAwait(false);

                    // A reader that finished keeps its spill: its complete output is what
                    // execute_command hands back. Only an abandoned reader's spill is reclaimed.
                    (CappedStreamOutput abandonedStdout, CappedStreamOutput abandonedStderr) =
                        await DrainAbandonedStreamReadTasksAsync(
                                stdoutTask,
                                stdoutSpillPath,
                                stderrTask,
                                stderrSpillPath)
                            .ConfigureAwait(false);

                    int abandonedExitCode = process.ExitCode;

                    if (MemoryMonitorFaultResult(
                            descendantSupervisor,
                            limiterResult,
                            perStreamCapBytes,
                            abandonedStdout,
                            abandonedStderr,
                            abandonedExitCode) is { } abandonedMonitorFault)
                    {
                        return abandonedMonitorFault;
                    }

                    ResourceLimitKind? abandonedExceededResource = await CheckSignalKillAsync(
                            abandonedExitCode,
                            resourceLimits,
                            limiterResult)
                        .ConfigureAwait(false);

                    return new CappedChildProcessRunResult
                    {
                        Outcome = abandonedExceededResource is not null
                            ? CappedChildProcessOutcome.ResourceLimitExceeded
                            : CappedChildProcessOutcome.Completed,

                        Stdout = abandonedStdout,

                        Stderr = abandonedStderr,

                        ExitCode = abandonedExitCode,

                        PerStreamCapBytes = perStreamCapBytes,

                        ExceededResource = abandonedExceededResource,
                    };
                }
                catch (ChildProcessOutputPreservationException ex)
                {
                    DeleteOutputSpillsWhenReadersComplete(
                        stdoutTask,
                        stdoutSpillPath,
                        stderrTask,
                        stderrSpillPath);

                    return new CappedChildProcessRunResult
                    {
                        Outcome = CappedChildProcessOutcome.OutputPreservationFailed,

                        PerStreamCapBytes = perStreamCapBytes,

                        FaultException = ex,
                    };
                }
                catch (IOException ex)
                {
                    DeleteOutputSpillsWhenReadersComplete(
                        stdoutTask,
                        stdoutSpillPath,
                        stderrTask,
                        stderrSpillPath);

                    return new CappedChildProcessRunResult
                    {
                        Outcome = CappedChildProcessOutcome.IoErrorReadingOutput,

                        PerStreamCapBytes = perStreamCapBytes,

                        FaultException = ex,
                    };
                }
                catch (UnauthorizedAccessException ex)
                {
                    DeleteOutputSpillsWhenReadersComplete(
                        stdoutTask,
                        stdoutSpillPath,
                        stderrTask,
                        stderrSpillPath);

                    return new CappedChildProcessRunResult
                    {
                        Outcome = CappedChildProcessOutcome.AccessDeniedReadingOutput,

                        PerStreamCapBytes = perStreamCapBytes,

                        FaultException = ex,
                    };
                }
                catch (OperationCanceledException)
                {
                    DeleteOutputSpillsWhenReadersComplete(
                        stdoutTask,
                        stdoutSpillPath,
                        stderrTask,
                        stderrSpillPath);

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

                if (MemoryMonitorFaultResult(
                        descendantSupervisor,
                        limiterResult,
                        perStreamCapBytes,
                        stdout,
                        stderr,
                        exitCode) is { } monitorFault)
                {
                    return monitorFault;
                }

                // The fail-closed prelude exits 126 before exec when a limit could not be applied and
                // only then writes its per-run marker, so the target never ran: report the refusal,
                // never the prelude's exit as the target's result.
                if (exitCode == PreExecLimitFailureExitCode
                    && limiterResult.PreExecFailureMarker is string preExecFailureMarker
                    && stderr.Text.Contains(preExecFailureMarker, StringComparison.Ordinal))
                {
                    return new CappedChildProcessRunResult
                    {
                        Outcome = CappedChildProcessOutcome.ResourceLimitApplyFailed,

                        Stdout = stdout,

                        Stderr = stderr,

                        ExitCode = exitCode,

                        PerStreamCapBytes = perStreamCapBytes,

                        ResourceLimitApplyError =
                            "A configured child-process resource limit could not be applied before exec; the target was not run.",
                    };
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
            await TearDownRunAsync(
                    descendantSupervisor,
                    sandboxResult,
                    limiterResult,
                    startedPid,
                    getCleanupTimeRemaining,
                    logger)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases everything a run acquired, one isolated step at a time. This runs in a <c>finally</c>: an
    /// exception from any step would replace the run's own result and skip every step after it — leaving
    /// the sandbox temp directory, the Windows undo-log replay or the limiter scope behind — so each step
    /// is contained and logged instead.
    /// </summary>
    private static async Task TearDownRunAsync(
        MacOsDescendantSupervisor? descendantSupervisor,
        ChildProcessSandboxApplyResult? sandboxResult,
        ProcessResourceLimiterResult limiterResult,
        int startedPid,
        Func<TimeSpan>? getCleanupTimeRemaining,
        ILogger? logger)
    {
        if (descendantSupervisor is not null)
        {
            try
            {
                await descendantSupervisor.DisposeAsync()
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Disposing the descendant supervisor failed; the remaining teardown continues.");
            }

            LogMonitorFault(
                descendantSupervisor,
                limiterResult.MonitoredMemoryLimitBytes,
                logger);

            LogUnreadableFootprints(
                descendantSupervisor,
                logger);
        }

        // The child is gone by now, killed with TerminateProcess on every abnormal path, so the
        // Windows broker's own restore never ran. Replay its undo log before the log itself is
        // deleted below, or the granted AppContainer ACE outlives the run permanently.
        try
        {
            _ = ChildProcessFilesystemJail.RestoreWindowsAppContainerState(
                sandboxResult,
                logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Replaying the Windows AppContainer undo log failed; the remaining teardown continues.");
        }

        try
        {
            await CleanupSandboxTempPathsAsync(
                    sandboxResult,
                    getCleanupTimeRemaining,
                    logger)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Cleaning the child-process sandbox temporary artifacts failed; the remaining teardown continues.");
        }

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
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Cleaning the child-process resource limiter scope failed.");
            }
        }
    }

    private static MacOsDescendantSupervisor? DefaultDescendantSupervisorFactory(
        int rootPid,
        long? memoryLimitBytes) =>
        MacOsDescendantSupervisor.TryStart(
            rootPid,
            memoryLimitBytes: memoryLimitBytes);

    /// <summary>
    /// Stops the supervisor and verifies its tree is gone, containing any failure: a supervisor fault must
    /// cost the verification, never the run's result or the teardown that follows it.
    /// </summary>
    private static async Task<bool> StopDescendantSupervisorAsync(
        MacOsDescendantSupervisor supervisor,
        ILogger? logger)
    {
        try
        {
            return await supervisor
                .StopKillAndVerifyAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Stopping the descendant supervisor failed; descendant containment is unverified.");

            return false;
        }
    }

    /// <summary>
    /// Records a stopped monitor loop. The supervisor has no logger of its own and nothing else reads its
    /// fault, so without this a faulted loop is invisible whenever the run ends in anything but the
    /// memory-ceiling failure: the loop that tracks descendants before they reparent (the
    /// workspace_check containment boundary) simply stops. Read after disposal, when the loop has
    /// finished and the fault is final.
    /// </summary>
    private static void LogMonitorFault(
        MacOsDescendantSupervisor supervisor,
        long? memoryLimitBytes,
        ILogger? logger)
    {
        if (!supervisor.MonitorFaulted)
        {
            return;
        }

        if (memoryLimitBytes is null)
        {
            logger?.LogError(
                supervisor.MonitorFault,
                "The descendant supervisor's monitor loop stopped on a fault; no memory ceiling was configured, so the process tree was not killed, but the loop no longer tracked descendants as they forked, so one that reparented before the teardown's own last scan can escape containment.");

            return;
        }

        logger?.LogError(
            supervisor.MonitorFault,
            "The child-process memory monitor stopped on a fault; the process tree was killed.");
    }

    /// <summary>
    /// Records the descendants the memory ceiling's sum had to leave out. That gap in the ceiling's
    /// coverage exists however the run ended — the likeliest end for a tree the ceiling was not fully
    /// covering is a timeout or a cancel, not a normal exit — so it is reported from the teardown, once,
    /// after disposal, when the count is final.
    /// </summary>
    private static void LogUnreadableFootprints(
        MacOsDescendantSupervisor supervisor,
        ILogger? logger)
    {
        if (supervisor.UnreadableFootprintCount is > 0 and long unreadable)
        {
            logger?.LogWarning(
                "The child-process memory monitor could not read the footprint of {Count} descendant process(es); they were left out of the memory ceiling's sum.",
                unreadable);
        }
    }

    /// <summary>
    /// The result for a run whose macOS memory monitor stopped on a fault: nothing enforced the ceiling
    /// from then on (the supervisor ends the tree when that happens), so the run is not a normal
    /// completion whatever the child's exit status was. Logging the fault is not done here: it is owed on
    /// every path, including the ones that end in a cancel or a timeout and the ones with no ceiling, so
    /// <see cref="TearDownRunAsync"/> owns it.
    /// </summary>
    /// <remarks>
    /// <c>null</c> when the monitor had already recorded a breach before it faulted. That record is the
    /// real cause — the monitor killed the tree for exceeding the ceiling — and the caller's own
    /// classification (<see cref="CheckSignalKillAsync"/> reads it) reports the breach instead of a fault
    /// that only came after it. The outcome is <see cref="CappedChildProcessOutcome.MemoryMonitorStopped"/>,
    /// not an apply failure: the child ran for as long as the monitor lasted, so it must not be described as
    /// a command that never started.
    /// </remarks>
    private static CappedChildProcessRunResult? MemoryMonitorFaultResult(
        MacOsDescendantSupervisor? supervisor,
        ProcessResourceLimiterResult limiterResult,
        long perStreamCapBytes,
        CappedStreamOutput stdout,
        CappedStreamOutput stderr,
        int exitCode)
    {
        if (supervisor?.MonitorFaulted != true
            || limiterResult.MonitoredMemoryLimitBytes is null
            || supervisor.MemoryLimitExceeded)
        {
            return null;
        }

        return new CappedChildProcessRunResult
        {
            Outcome = CappedChildProcessOutcome.MemoryMonitorStopped,

            Stdout = stdout,

            Stderr = stderr,

            ExitCode = exitCode,

            PerStreamCapBytes = perStreamCapBytes,

            FaultException = supervisor.MonitorFault,
        };
    }

    /// <summary>
    /// Closes the write end of the child's redirected stdin pipe so the child observes EOF on its
    /// very first read instead of waiting for input that will never arrive. Closing can race the
    /// child's own exit (broken pipe, already-disposed stream); that race is the EOF the close was
    /// asking for, never a reason to fault an otherwise completed run, so it is swallowed.
    /// </summary>
    private static void CloseChildStandardInput(
        Process process,
        ILogger? logger)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(
                ex,
                "Closing the child process stdin pipe raced the child's exit.");
        }
    }

    private static async Task CleanupSandboxTempPathsAsync(
        ChildProcessSandboxApplyResult? sandboxResult,
        Func<TimeSpan>? getCleanupTimeRemaining,
        ILogger? logger)
    {
        bool cleaned =
            await ChildProcessFilesystemJail.CleanupTempPathsAsync(
                    sandboxResult?.OwnedArtifactsToCleanup,
                    GetCleanupTimeRemaining(
                        getCleanupTimeRemaining))
                .ConfigureAwait(false);

        if (!cleaned)
        {
            logger?.LogWarning(
                "Child-process sandbox temporary-artifact cleanup was incomplete or exceeded its deadline; unmatched artifacts were retained.");
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
    /// On macOS the same delegate reports the runner's footprint monitor, which is also sufficient on
    /// its own because the monitor performed the kill. Falls back to the exit-code-only heuristic
    /// when no such evidence is available (Windows Job Objects, or Linux without a usable cgroup).
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

        // The macOS footprint monitor killed the tree itself, so its record is the attribution
        // whatever status the killed root reported.
        if (limiterResult.MonitoredMemoryLimitBytes is not null
            && limiterResult.WasOomKilledAsync is not null
            && await limiterResult.WasOomKilledAsync().ConfigureAwait(false))
        {
            return ResourceLimitKind.Memory;
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

        if (signal is 9 or 11 && ProcessResourceLimiter.EffectiveMemoryLimitMb(resourceLimits) > 0)
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

    /// <summary>
    /// Attributes a Windows Job Object exit to the memory limit. Internal so the classification can be
    /// pinned on every host: the only caller runs behind <see cref="OperatingSystem.IsWindows"/>.
    /// </summary>
    internal static ResourceLimitKind? ClassifyWindowsJobExit(int exitCode, ResourceLimits resourceLimits)
    {
        // Job Object process/job memory violations commonly surface as STATUS_QUOTA_EXCEEDED.
        // CPU-time kills do not have a stable, documented exit code we can trust across Windows
        // versions, so wall-clock timeout remains the reliable attribution path for CPU on Windows.
        if (exitCode == WindowsJobObjectInterop.StatusQuotaExceeded
            && ProcessResourceLimiter.EffectiveMemoryLimitMb(resourceLimits) > 0)
        {
            return ResourceLimitKind.Memory;
        }

        return null;
    }

    /// <summary>
    /// Last look at both output readers once the post-exit drain has already been given up on.
    /// A reader that finished keeps its output and its spill file. A reader that still cannot finish
    /// is abandoned — its spill file is reclaimed by <see cref="DeleteOutputSpillWhenReaderCompletes"/>
    /// now and again whenever it eventually completes — and what it never delivered is reported as
    /// <see cref="CappedStreamOutput.ReadAbandoned"/> truncation rather than handed to the model as the
    /// command's complete output.
    /// </summary>
    private static async Task<(CappedStreamOutput Stdout, CappedStreamOutput Stderr)>
        DrainAbandonedStreamReadTasksAsync(
        Task<CappedStreamOutput> stdoutTask,
        string? stdoutSpillPath,
        Task<CappedStreamOutput> stderrTask,
        string? stderrSpillPath)
    {
        try
        {
            // One short window for both readers, like the drain it follows.
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(AbandonedOutputDrainRegrace)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timed out, faulted or canceled: whichever reader did not deliver is reported below.
        }

        return (
            AbandonedReaderOutput(stdoutTask, stdoutSpillPath),
            AbandonedReaderOutput(stderrTask, stderrSpillPath));
    }

    /// <summary>
    /// What a reader that has been given up on can still hand back: its output when it completed, and
    /// otherwise — timed out, faulted or canceled — nothing, with the gap reported as abandoned truncation
    /// because the run itself still completed. The reader's state is read once, so the spill is deleted
    /// exactly when the output returned does not point at it.
    /// </summary>
    private static CappedStreamOutput AbandonedReaderOutput(
        Task<CappedStreamOutput> readerTask,
        string? spillPath)
    {
        if (readerTask.IsCompletedSuccessfully)
        {
            return readerTask.Result;
        }

        DeleteOutputSpillWhenReaderCompletes(
            readerTask,
            spillPath);

        return new CappedStreamOutput(
            string.Empty,
            Truncated: true,
            ReadAbandoned: true);
    }

    /// <summary>
    /// Waits for both output readers to reach EOF within one shared <paramref name="grace"/>. A reader that
    /// fails surfaces its exception at once (stdout's first) rather than after the other reader's wait, and
    /// <see cref="TimeoutException"/> reports that either is still held when the grace runs out.
    /// </summary>
    /// <param name="timeProvider">
    /// The clock the grace is measured on. A test supplies one it advances itself, so "one deadline for both
    /// pipes" is proved by when that deadline lapses rather than by how long a real run happened to take on a
    /// loaded host.
    /// </param>
    internal static async Task<(CappedStreamOutput Stdout, CappedStreamOutput Stderr)>
        AwaitPostExitOutputAsync(
        Task<CappedStreamOutput> stdoutTask,
        Task<CappedStreamOutput> stderrTask,
        TimeSpan grace,
        TimeProvider timeProvider)
    {
        Task both = Task.WhenAll(stdoutTask, stderrTask);

        Task anyFailure = FirstFailureAsync(stdoutTask, stderrTask);

        _ = await Task.WhenAny(both, anyFailure)
            .WaitAsync(grace, timeProvider)
            .ConfigureAwait(false);

        if (stdoutTask.IsFaulted || stdoutTask.IsCanceled)
        {
            await stdoutTask.ConfigureAwait(false);
        }

        if (stderrTask.IsFaulted || stderrTask.IsCanceled)
        {
            await stderrTask.ConfigureAwait(false);
        }

        return (stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>Completes as soon as any of <paramref name="tasks"/> faults or is canceled.</summary>
    private static Task FirstFailureAsync(params Task[] tasks)
    {
        TaskCompletionSource failed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        foreach (Task task in tasks)
        {
            _ = task.ContinueWith(
                static (finished, state) =>
                {
                    if (finished.IsFaulted || finished.IsCanceled)
                    {
                        _ = ((TaskCompletionSource)state!).TrySetResult();
                    }
                },
                failed,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return failed.Task;
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
        catch (Exception)
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
        catch (Exception)
        {
        }

        return (stdout, stderr);
    }

    private static async Task<CappedStreamOutput> ReadStreamCappedAsync(
        StreamReader reader,
        long maxBytes,
        CancellationToken cancellationToken,
        string? completeOutputPath,
        OutputSpillBudget? outputSpillBudget)
    {
        StringBuilder builder = new();

        char[] buffer = new char[4096];

        long approximateBytes = 0L;

        long totalBytes = 0L;

        bool truncated = false;

        StreamWriter? completeWriter = null;

        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(
                        buffer.AsMemory(),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read <= 0)
                {
                    break;
                }

                long encodedSize = Encoding.UTF8.GetByteCount(
                    buffer,
                    0,
                    read);

                totalBytes += encodedSize;

                if (truncated)
                {
                    if (completeWriter is not null)
                    {
                        outputSpillBudget?.Reserve(encodedSize);

                        await completeWriter.WriteAsync(
                                buffer.AsMemory(0, read),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                if (approximateBytes + encodedSize <= maxBytes)
                {
                    builder.Append(buffer, 0, read);

                    approximateBytes += encodedSize;

                    continue;
                }

                if (completeOutputPath is not null)
                {
                    outputSpillBudget?.Reserve(
                        checked(approximateBytes + encodedSize));

                    truncated = true;

                    completeWriter = CreateCompleteOutputWriter(
                        completeOutputPath);

                    await completeWriter.WriteAsync(
                            builder.ToString().AsMemory(),
                            cancellationToken)
                        .ConfigureAwait(false);

                    await completeWriter.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                long remaining = maxBytes - approximateBytes;

                if (remaining > 0)
                {
                    int safeChars = Utf8Truncation.ChooseSafeCharCount(
                        buffer.AsSpan(0, read),
                        remaining);

                    builder.Append(buffer, 0, safeChars);
                }

                truncated = true;
            }

            if (completeWriter is not null)
            {
                await completeWriter.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return new CappedStreamOutput(
                builder.ToString(),
                truncated,
                completeWriter is null ? null : completeOutputPath,
                totalBytes);
        }
        catch (Exception ex)
        {
            if (completeOutputPath is not null)
            {
                TryDeleteOutputSpill(completeOutputPath);
            }

            if (truncated
                && ex is IOException or UnauthorizedAccessException
                && ex is not ChildProcessOutputPreservationException)
            {
                throw new ChildProcessOutputPreservationException(ex);
            }

            throw;
        }
        finally
        {
            if (completeWriter is not null)
            {
                try
                {
                    await completeWriter.DisposeAsync()
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                    when (ex is IOException
                          or UnauthorizedAccessException)
                {
                    TryDeleteOutputSpill(completeOutputPath);

                    throw new ChildProcessOutputPreservationException(ex);
                }
            }
        }
    }

    private static StreamWriter CreateCompleteOutputWriter(string path)
    {
        FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            // FileShare.Delete keeps the artifact deletable while this writer is still open, so the
            // cancellation/failure cleanup below (and CommandOutputArtifactStore's DeleteOnClose
            // retention open) cannot be defeated by a Windows sharing violation.
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 4096,
                leaveOpen: false);
        }
        catch
        {
            stream.Dispose();

            TryDeleteOutputSpill(path);

            throw;
        }
    }

    private sealed class OutputSpillBudget(long limitBytes)
    {
        private long _reservedBytes;

        internal void Reserve(long byteCount)
        {
            while (true)
            {
                long current = Volatile.Read(ref _reservedBytes);

                long attempted = checked(current + byteCount);

                if (attempted > limitBytes)
                {
                    throw new CommandOutputSpillLimitException(
                        attempted,
                        limitBytes);
                }

                if (Interlocked.CompareExchange(
                        ref _reservedBytes,
                        attempted,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private static string? BuildOutputSpillPath(
        string? outputSpillDirectory,
        string streamName)
    {
        if (string.IsNullOrWhiteSpace(outputSpillDirectory))
        {
            return null;
        }

        return Path.Combine(
            Path.GetFullPath(outputSpillDirectory),
            streamName + "-" + Guid.NewGuid().ToString("N") + ".utf8");
    }

    private static void CancelWaitWhenOutputReadFails(
        Task<CappedStreamOutput> outputTask,
        CancellationTokenSource outputFailureCts)
    {
        _ = outputTask.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;

                CancellationTokenSource failure =
                    (CancellationTokenSource)state!;

                try
                {
                    failure.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            outputFailureCts,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Exception? GetOutputReadFault(
        Task<CappedStreamOutput> stdoutTask,
        Task<CappedStreamOutput> stderrTask) =>
        stdoutTask.Exception?.GetBaseException()
        ?? stderrTask.Exception?.GetBaseException();

    private static void DeleteOutputSpillsWhenReadersComplete(
        Task<CappedStreamOutput> stdoutTask,
        string? stdoutSpillPath,
        Task<CappedStreamOutput> stderrTask,
        string? stderrSpillPath)
    {
        DeleteOutputSpillWhenReaderCompletes(
            stdoutTask,
            stdoutSpillPath);

        DeleteOutputSpillWhenReaderCompletes(
            stderrTask,
            stderrSpillPath);
    }

    /// <summary>
    /// Removes a partial spill artifact on a cancellation/failure path, and removes it again once an
    /// abandoned reader finally completes. Deleting once is not sufficient: a reader whose pipe an
    /// orphaned descendant still holds open outlives
    /// <see cref="CompleteStreamReadTasksAsync"/>'s bounded wait, so at deletion time it may still
    /// hold its spill writer open — and it may not have crossed the preview cap yet, in which case it
    /// creates the artifact after the deletion.
    /// </summary>
    internal static void DeleteOutputSpillWhenReaderCompletes(
        Task<CappedStreamOutput> readerTask,
        string? spillPath)
    {
        if (spillPath is null)
        {
            return;
        }

        TryDeleteOutputSpill(spillPath);

        if (readerTask.IsCompleted)
        {
            return;
        }

        _ = readerTask.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;

                TryDeleteOutputSpill((string)state!);
            },
            spillPath,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private static void TryDeleteOutputSpill(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
