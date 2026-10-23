using System.Diagnostics;

using System.Text;

using RetroDownfall.Arcanum.Core.Platform;

using RetroDownfall.Arcanum.Core.Sanctum;

using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Parses a document in a child process: this host's own binary re-launched in the hidden worker mode, under the capped
/// runner with the code-owned limits of the document kind's <see cref="DocumentWorkerProfile"/>, a required filesystem jail
/// that is the same for every kind, no network and a cleared environment, with the document on stdin. The runner kills the
/// child's whole tree on its deadline, its limits, or the caller's cancellation, and returns only after it is gone, so a
/// crash, a hang or a memory blow-up in the parser ends as a bounded result here.
/// </summary>
/// <remarks>
/// Every result is the worker's frame or one of the profile's four code-owned reasons. No exception text, path, exit code
/// or stderr reaches a reason. An environment that cannot run the worker at all is the profile's unavailable reason;
/// retrying it would only start the same refusal again, so the operator re-indexes after fixing the host.
/// </remarks>
internal sealed class ProcessDocumentExtractionWorker(IProcessResourceLimiter resourceLimiter) : IDocumentExtractionWorker
{
    private const string ReplacedExecutableCode = "comprehension-worker-executable-changed";

    /// <summary>SIGXFSZ, the same number on macOS and Linux: a write past the profile's file-size ceiling.</summary>
    private const int FileSizeSignal = 25;

    private static readonly UTF8Encoding Utf8WithoutPreamble = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly AsyncLocal<DocumentWorkerLaunch?> LaunchForTests = new();

    private static readonly AsyncLocal<IDocumentExtractionWorker?> SubstituteForTests = new();

    public async Task<DocumentWorkerResult> RunAsync(
        DocumentWorkerFormat format,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        DocumentWorkerProfile profile = DocumentWorkerProfile.For(format);

        cancellationToken.ThrowIfCancellationRequested();

        if (SubstituteForTests.Value is { } substitute)
        {
            return await substitute.RunAsync(format, bytes, cancellationToken).ConfigureAwait(false);
        }

        // The worker would refuse it at stdin anyway; starting a process to hear that is not worth it.
        if (bytes.Length > profile.InputCeilingBytes)
        {
            return DocumentWorkerResult.Failed(profile.FailedReason);
        }

        DocumentWorkerLaunch? launch = LaunchForTests.Value ?? ComprehensionWorkerEntry.HostLaunch;

        if (launch is null)
        {
            return DocumentWorkerResult.Failed(profile.UnavailableReason);
        }

        DocumentWorkerRunRequest request = CreateRunRequest(launch, profile);

        CappedChildProcessRunResult run;

        try
        {
            run = await CappedChildProcessRunner.RunAsync(
                request.StartInfo,
                launch.EnvironmentProfile,
                request.TotalOutputCapBytes,
                request.WallClock,
                request.Limits,
                resourceLimiter,
                cancellationToken,
                request.Sandbox,
                preStartValidation: () => launch.IsUnchanged()
                    ? new CappedChildProcessPreStartValidationResult(true, null)
                    : new CappedChildProcessPreStartValidationResult(false, profile.UnavailableReason, ReplacedExecutableCode),
                standardInput: bytes,
                rawStandardOutput: true,
                maxFileSizeBytes: request.MaxFileSizeBytes).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DocumentWorkerResult.Failed(profile.FailedReason);
        }

        if (run.Outcome is CappedChildProcessOutcome.Canceled
            or CappedChildProcessOutcome.CanceledBeforeStart
            or CappedChildProcessOutcome.CanceledWhileReadingOutput)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Map(run, profile);
    }

    /// <summary>
    /// The one run the worker hands the runner for <paramref name="launch"/> and <paramref name="profile"/>: the re-launch with
    /// the profile's argument, the profile's clock, limits and output bounds, and the jail every kind runs under.
    /// </summary>
    internal static DocumentWorkerRunRequest CreateRunRequest(DocumentWorkerLaunch launch, DocumentWorkerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(launch);

        ArgumentNullException.ThrowIfNull(profile);

        return new DocumentWorkerRunRequest(
            CreateStartInfo(launch, profile),
            CreateSandbox(launch),
            LimitsFor(launch, profile),
            profile.WallClock,
            profile.TotalOutputCapBytes,
            profile.MaxFileSizeBytes);
    }

    /// <summary>Reads one run: the frame of a worker that completed cleanly, or the code-owned reason it gave none.</summary>
    internal static DocumentWorkerResult Map(CappedChildProcessRunResult run, DocumentWorkerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(run);

        ArgumentNullException.ThrowIfNull(profile);

        return run.Outcome switch
        {
            CappedChildProcessOutcome.Completed => MapCompleted(run, profile),
            CappedChildProcessOutcome.TimedOut => DocumentWorkerResult.Failed(profile.DeadlineReason),
            CappedChildProcessOutcome.ResourceLimitExceeded
                or CappedChildProcessOutcome.MemoryMonitorStopped => DocumentWorkerResult.Failed(profile.ResourceLimitsReason),
            CappedChildProcessOutcome.FilesystemSandboxUnavailable
                or CappedChildProcessOutcome.FilesystemSandboxDeniedByWindowsSanctum
                or CappedChildProcessOutcome.ResourceLimitApplyFailed
                or CappedChildProcessOutcome.FailedToStart
                or CappedChildProcessOutcome.IoErrorOnStart
                or CappedChildProcessOutcome.AccessDeniedOnStart
                or CappedChildProcessOutcome.PreStartValidationFailed => DocumentWorkerResult.Failed(profile.UnavailableReason),
            _ => DocumentWorkerResult.Failed(profile.FailedReason),
        };
    }

    /// <summary>The profile's code-owned limits, with a test launch's descriptor ceiling when it names one.</summary>
    internal static ResourceLimits LimitsFor(DocumentWorkerLaunch launch, DocumentWorkerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(launch);

        ArgumentNullException.ThrowIfNull(profile);

        return launch.MaxFileDescriptors is { } descriptors ? profile.Limits with { MaxFileDescriptors = descriptors } : profile.Limits;
    }

    /// <summary>
    /// The re-launch of <paramref name="launch"/> in worker mode for the profile's kind, with UTF-8 pipes and exactly the
    /// launch's environment.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(DocumentWorkerLaunch launch, DocumentWorkerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(launch);

        ArgumentNullException.ThrowIfNull(profile);

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

        startInfo.ArgumentList.Add(profile.FormatArgument);

        // Nothing of this host's environment is inherited, whichever profile the runner then applies.
        startInfo.Environment.Clear();

        foreach (KeyValuePair<string, string> variable in launch.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        return startInfo;
    }

    /// <summary>
    /// The jail, the same for every kind of worker: it must be applied (no operator escape, as for <c>workspace_check</c>), with no network, no second
    /// process, nothing writable at all (the run's private temp directory is only readable, and of the devices only
    /// <c>/dev/null</c> takes a write, so neither a large file nor many small ones can fill the disk), nothing readable
    /// beyond the system runtime roots but the executable's own directory, nothing listed or stat'ed outside those roots but
    /// their ancestors, and no other process's arguments, environment or path readable and no process list, since whatever
    /// the worker prints reaches model context.
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
            ConfineProcessInformation = true,
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

    private static DocumentWorkerResult MapCompleted(CappedChildProcessRunResult run, DocumentWorkerProfile profile)
    {
        // The kernel ended the worker for a write past its file-size ceiling, reported through the launch chain's
        // supervising shell (128 + the signal) or directly (the negated signal).
        if (run.ExitCode is 128 + FileSizeSignal or -FileSizeSignal)
        {
            return DocumentWorkerResult.Failed(profile.ResourceLimitsReason);
        }

        if (run.ExitCode == (int)ComprehensionWorkerExitCode.Deadline)
        {
            return DocumentWorkerResult.Failed(profile.DeadlineReason);
        }

        // The frame is judged on the bytes the worker wrote, never on a decoding of them: a text reader would detect a
        // byte-order mark and switch encoding, strip a UTF-8 one, or replace an invalid byte, and the grammar's own
        // UTF-8 and header rules would then pass a frame they refuse.
        if (run.ExitCode != (int)ComprehensionWorkerExitCode.FrameWritten || run.Stdout.Truncated || run.Stdout.Bytes is not { } written)
        {
            return DocumentWorkerResult.Failed(profile.FailedReason);
        }

        ComprehensionWorkerFrameResult frame = ComprehensionWorkerFrame.Decode(written, profile.MaxOutputCharacters);

        return frame.IsValid
            ? DocumentWorkerResult.Completed(frame.Outcome!)
            : DocumentWorkerResult.Failed(profile.FailedReason);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>
/// Everything one worker run hands the capped runner, composed from a launch and a kind's profile by
/// <see cref="ProcessDocumentExtractionWorker.CreateRunRequest"/>, so the jail a run gets is the one a test can read for every
/// kind.
/// </summary>
internal sealed record DocumentWorkerRunRequest(
    ProcessStartInfo StartInfo,
    ChildProcessSandboxRequest Sandbox,
    ResourceLimits Limits,
    TimeSpan WallClock,
    long TotalOutputCapBytes,
    long MaxFileSizeBytes);
