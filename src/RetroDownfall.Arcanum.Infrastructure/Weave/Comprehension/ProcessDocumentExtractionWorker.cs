using System.Diagnostics;

using System.Text;

using RetroDownfall.Arcanum.Core.Platform;

using RetroDownfall.Arcanum.Core.Sanctum;

using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Parses a document in a child process: this host's own binary re-launched in the hidden worker mode, under the capped
/// runner with code-owned limits, a required filesystem jail, no network and a cleared environment, with the document on
/// stdin. The runner kills the child's whole tree on its deadline, its limits, or the caller's cancellation, and returns
/// only after it is gone, so a crash, a hang or a memory blow-up in the parser ends as a bounded result here.
/// </summary>
/// <remarks>
/// Every result is the worker's frame or one of four code-owned reasons. No exception text, path, exit code or stderr
/// reaches a reason. An environment that cannot run the worker at all is <see cref="UnavailableReason"/>; retrying it
/// would only start the same refusal again, so the operator re-indexes after fixing the host.
/// </remarks>
internal sealed class ProcessDocumentExtractionWorker(IProcessResourceLimiter resourceLimiter) : IDocumentExtractionWorker
{
    /// <summary>The worker gave no usable frame: it crashed, exited with a code of its own, or wrote something else.</summary>
    internal const string FailedReason = "PDF extraction failed.";

    /// <summary>The worker outlived the wall clock, or gave up at its own deadline first.</summary>
    internal const string DeadlineReason = "PDF extraction exceeded its deadline.";

    /// <summary>The worker crossed its memory or CPU ceiling, or the memory monitor that enforces it stopped.</summary>
    internal const string ResourceLimitsReason = "PDF extraction exceeded its resource limits.";

    /// <summary>This host cannot run the worker: not a Native AOT binary (a dev host or a managed publish), no re-launchable binary, no applied jail, no limits, or a replaced binary.</summary>
    internal const string UnavailableReason = "PDF extraction is unavailable on this host.";

    /// <summary>
    /// The runner's total output cap. Each stream gets half, and stdout must hold the largest frame the output ceiling
    /// allows: 262,144 UTF-16 characters at three UTF-8 bytes each, plus the header and trailer.
    /// </summary>
    internal const long TotalOutputCapBytes = 2L * 1024 * 1024;

    /// <summary>
    /// The kernel ceiling on any one file the worker writes. It writes none and its jail grants it nowhere to write (see
    /// <see cref="CreateSandbox"/>); the ceiling stays as defence in depth should a write ever be granted, though it bounds
    /// each file and not how many. Not enforced on Windows, which has no per-process file-size limit.
    /// </summary>
    internal const long MaxFileSizeBytes = 1024 * 1024;

    /// <summary>The wall clock from before the child starts until it is gone, cold start included.</summary>
    internal static readonly TimeSpan WallClock = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The code-owned process limits, never configuration: a 768 MB memory ceiling (the macOS footprint monitor, a
    /// Windows job object), 15 s of CPU, one process and 64 file descriptors. <c>MaxFileWriteMb</c> is Sanctum's write-tool
    /// budget and stays 0; the kernel's ceiling on a file the worker writes is <see cref="MaxFileSizeBytes"/>.
    /// </summary>
    internal static ResourceLimits Limits { get; } = new(
        MaxProcessMemoryMb: 768,
        MaxProcessCount: 1,
        MaxFileWriteMb: 0,
        ProcessTimeoutSeconds: 10,
        MaxCpuSeconds: 15,
        MaxMemoryMb: 768,
        MaxFileDescriptors: 64);

    private const string ReplacedExecutableCode = "comprehension-worker-executable-changed";

    /// <summary>SIGXFSZ, the same number on macOS and Linux: a write past <see cref="MaxFileSizeBytes"/>.</summary>
    private const int FileSizeSignal = 25;

    private static readonly UTF8Encoding Utf8WithoutPreamble = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly AsyncLocal<DocumentWorkerLaunch?> LaunchForTests = new();

    private static readonly AsyncLocal<IDocumentExtractionWorker?> SubstituteForTests = new();

    public async Task<DocumentWorkerResult> RunAsync(
        DocumentWorkerFormat format,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        if (format != DocumentWorkerFormat.Pdf)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "The worker reads only PDF documents.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (SubstituteForTests.Value is { } substitute)
        {
            return await substitute.RunAsync(format, bytes, cancellationToken).ConfigureAwait(false);
        }

        // The worker would refuse it at stdin anyway; starting a process to hear that is not worth it.
        if (bytes.Length > ComprehensionWorkerEntry.InputCeilingBytes)
        {
            return DocumentWorkerResult.Failed(FailedReason);
        }

        DocumentWorkerLaunch? launch = LaunchForTests.Value ?? ComprehensionWorkerEntry.HostLaunch;

        if (launch is null)
        {
            return DocumentWorkerResult.Failed(UnavailableReason);
        }

        CappedChildProcessRunResult run;

        try
        {
            run = await CappedChildProcessRunner.RunAsync(
                CreateStartInfo(launch),
                launch.EnvironmentProfile,
                TotalOutputCapBytes,
                WallClock,
                LimitsFor(launch),
                resourceLimiter,
                cancellationToken,
                CreateSandbox(launch),
                preStartValidation: () => launch.IsUnchanged()
                    ? new CappedChildProcessPreStartValidationResult(true, null)
                    : new CappedChildProcessPreStartValidationResult(false, UnavailableReason, ReplacedExecutableCode),
                standardInput: bytes,
                rawStandardOutput: true,
                maxFileSizeBytes: MaxFileSizeBytes).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DocumentWorkerResult.Failed(FailedReason);
        }

        if (run.Outcome is CappedChildProcessOutcome.Canceled
            or CappedChildProcessOutcome.CanceledBeforeStart
            or CappedChildProcessOutcome.CanceledWhileReadingOutput)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Map(run);
    }

    /// <summary>Reads one run: the frame of a worker that completed cleanly, or the code-owned reason it gave none.</summary>
    internal static DocumentWorkerResult Map(CappedChildProcessRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return run.Outcome switch
        {
            CappedChildProcessOutcome.Completed => MapCompleted(run),
            CappedChildProcessOutcome.TimedOut => DocumentWorkerResult.Failed(DeadlineReason),
            CappedChildProcessOutcome.ResourceLimitExceeded
                or CappedChildProcessOutcome.MemoryMonitorStopped => DocumentWorkerResult.Failed(ResourceLimitsReason),
            CappedChildProcessOutcome.FilesystemSandboxUnavailable
                or CappedChildProcessOutcome.FilesystemSandboxDeniedByWindowsSanctum
                or CappedChildProcessOutcome.ResourceLimitApplyFailed
                or CappedChildProcessOutcome.FailedToStart
                or CappedChildProcessOutcome.IoErrorOnStart
                or CappedChildProcessOutcome.AccessDeniedOnStart
                or CappedChildProcessOutcome.PreStartValidationFailed => DocumentWorkerResult.Failed(UnavailableReason),
            _ => DocumentWorkerResult.Failed(FailedReason),
        };
    }

    /// <summary>The code-owned <see cref="Limits"/>, with a test launch's descriptor ceiling when it names one.</summary>
    internal static ResourceLimits LimitsFor(DocumentWorkerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);

        return launch.MaxFileDescriptors is { } descriptors ? Limits with { MaxFileDescriptors = descriptors } : Limits;
    }

    /// <summary>The re-launch of <paramref name="launch"/> in worker mode, with UTF-8 pipes and exactly its environment.</summary>
    internal static ProcessStartInfo CreateStartInfo(DocumentWorkerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);

        ProcessStartInfo startInfo = new()
        {
            FileName = launch.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8WithoutPreamble,
            StandardErrorEncoding = Utf8WithoutPreamble,
        };

        startInfo.ArgumentList.Add(ComprehensionWorkerEntry.ModeArgument);

        startInfo.ArgumentList.Add(ComprehensionWorkerEntry.PdfFormatArgument);

        // Nothing of this host's environment is inherited, whichever profile the runner then applies.
        startInfo.Environment.Clear();

        foreach (KeyValuePair<string, string> variable in launch.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        return startInfo;
    }

    /// <summary>
    /// The jail: it must be applied (no operator escape, as for <c>workspace_check</c>), with no network, no second
    /// process, nothing writable at all (the run's private temp directory is only readable, and of the devices only
    /// <c>/dev/null</c> takes a write, so neither a large file nor many small ones can fill the disk), nothing readable
    /// beyond the system runtime roots but the executable's own directory, and nothing listed or stat'ed outside those
    /// roots but their ancestors, since whatever the worker prints reaches model context.
    /// </summary>
    internal static ChildProcessSandboxRequest CreateSandbox(DocumentWorkerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);

        return new ChildProcessSandboxRequest
        {
            ReadWriteRoots = [],
            ReadExecuteRoots = [Path.GetDirectoryName(launch.Executable)!, .. launch.AdditionalReadExecuteRoots],
            AllowUnsandboxed = false,
            RequireAppliedFilesystemJail = true,
            DenyNetwork = true,
            DenyProcessFork = launch.DenyProcessFork,
            ConfineMetadataToRoots = true,
            DenyFileWrites = true,
            ToolName = "comprehension-worker",
        };
    }

    /// <summary>
    /// Points the worker at another executable for the calling async flow: a test host cannot re-launch itself, because
    /// re-executing it starts the test platform. The executable is captured now and revalidated before every start, as
    /// the host's own is. Never set in production.
    /// </summary>
    internal static IDisposable UseExecutableForTests(
        string executable,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? additionalReadExecuteRoots = null,
        int? maxFileDescriptors = null,
        bool allowProcessFork = false) =>
        UseLaunch(DocumentWorkerLaunch.ForTests(executable, environment, additionalReadExecuteRoots, maxFileDescriptors, allowProcessFork));

    /// <summary>
    /// Points the worker at another executable through exactly the launch the host's own binary gets, environment profile,
    /// limits and jail included, for the calling async flow: a stand-in then meets the production boundary itself. Never
    /// set in production.
    /// </summary>
    internal static IDisposable UseHostLaunchForTests(string executable) =>
        UseLaunch(DocumentWorkerLaunch.ForHostExecutable(executable, nativeAot: true)
            ?? throw new InvalidOperationException("The test executable has no host launch."));

    private static IDisposable UseLaunch(DocumentWorkerLaunch launch)
    {
        DocumentWorkerLaunch? previous = LaunchForTests.Value;

        LaunchForTests.Value = launch;

        return new Scope(() => LaunchForTests.Value = previous);
    }

    /// <summary>
    /// Replaces the whole worker for the calling async flow, so a test can drive a production path that reaches the
    /// registry's worker without starting a process. Never set in production.
    /// </summary>
    internal static IDisposable UseSubstituteForTests(IDocumentExtractionWorker substitute)
    {
        ArgumentNullException.ThrowIfNull(substitute);

        IDocumentExtractionWorker? previous = SubstituteForTests.Value;

        SubstituteForTests.Value = substitute;

        return new Scope(() => SubstituteForTests.Value = previous);
    }

    private static DocumentWorkerResult MapCompleted(CappedChildProcessRunResult run)
    {
        // The kernel ended the worker for a write past its file-size ceiling, reported through the launch chain's
        // supervising shell (128 + the signal) or directly (the negated signal).
        if (run.ExitCode is 128 + FileSizeSignal or -FileSizeSignal)
        {
            return DocumentWorkerResult.Failed(ResourceLimitsReason);
        }

        if (run.ExitCode == (int)ComprehensionWorkerExitCode.Deadline)
        {
            return DocumentWorkerResult.Failed(DeadlineReason);
        }

        // The frame is judged on the bytes the worker wrote, never on a decoding of them: a text reader would detect a
        // byte-order mark and switch encoding, strip a UTF-8 one, or replace an invalid byte, and the grammar's own
        // UTF-8 and header rules would then pass a frame they refuse.
        if (run.ExitCode != (int)ComprehensionWorkerExitCode.FrameWritten || run.Stdout.Truncated || run.Stdout.Bytes is not { } written)
        {
            return DocumentWorkerResult.Failed(FailedReason);
        }

        ComprehensionWorkerFrameResult frame = ComprehensionWorkerFrame.Decode(
            written,
            PdfExtractionLimits.Default.MaxOutputCharacters);

        return frame.IsValid
            ? DocumentWorkerResult.Completed(frame.Outcome!)
            : DocumentWorkerResult.Failed(FailedReason);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
