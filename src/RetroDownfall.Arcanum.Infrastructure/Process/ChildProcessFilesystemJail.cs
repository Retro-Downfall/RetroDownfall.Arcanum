using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Result of attempting to apply an OS filesystem jail to a tool child.
/// Windows AppContainer and macOS Seatbelt use <see cref="Applied"/>.
/// </summary>
internal enum ChildProcessSandboxApplyStatus
{
    /// <summary>macOS Seatbelt / sandbox-exec profile is active.</summary>
    Applied,

    /// <summary>Jail required but unavailable (Linux beta default, missing sandbox-exec, setup failure).</summary>
    Unavailable,

    /// <summary>Legacy status retained for serialized/result compatibility.</summary>
    DeniedByWindowsSanctum,

    /// <summary>Operator escape hatch: ran without FS jail (rlimits may still apply).</summary>
    EscapedByOperator,

    /// <summary>Legacy status retained for serialized/result compatibility.</summary>
    NoFilesystemJail,
}

internal sealed class ChildProcessSandboxApplyResult
{
    internal ChildProcessSandboxApplyStatus Status { get; init; }

    /// <summary>
    /// Owner-only temp artifacts whose no-follow identities were captured at creation.
    /// </summary>
    internal IReadOnlyList<IdentityOwnedFileSystemArtifact>
        OwnedArtifactsToCleanup
    { get; init; } = [];

    /// <summary>
    /// Owner-only undo log the Windows broker journals its ACL grants and profile creation to. The
    /// host replays whatever survives the child, because a killed broker never runs its own restore.
    /// </summary>
    internal string? WindowsRestoreJournalPath { get; init; }

    internal string? WindowsJobAssignedSignalPath { get; init; }

    internal string? Detail { get; init; }
}

/// <summary>
/// Rewrites <see cref="ProcessStartInfo"/> so the child runs under an OS filesystem jail
/// (macOS <c>/usr/bin/sandbox-exec</c> Seatbelt for Apple Silicon beta), or fail-closes.
/// Linux Landlock / internal <c>__sandbox-exec</c> helper code remains in-tree but is <b>inactive</b>
/// for this beta (probe-first: not wired until end-to-end Landlock activation is validated).
/// Do not conflate the Apple <c>sandbox-exec</c> binary with the internal helper argv.
/// </summary>
internal static class ChildProcessFilesystemJail
{
    internal const string HelperArg = "__sandbox-exec";

    /// <summary>Public model-visible denial when Linux FS jail is inactive (fail-closed).</summary>
    internal const string LinuxDeferredDetail =
        ToolChildSandboxCapabilityReporter.LinuxBetaDenialMessage;

    internal static ChildProcessSandboxApplyResult Apply(
        ProcessStartInfo startInfo,
        ChildProcessSandboxRequest request,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        ArgumentNullException.ThrowIfNull(request);

        if (OperatingSystem.IsWindows())
        {
            return ApplyWindows(startInfo, request, logger);
        }

        if (OperatingSystem.IsMacOS())
        {
            return ApplyMacOs(startInfo, request, logger);
        }

        if (OperatingSystem.IsLinux())
        {
            return ApplyLinux(request, logger);
        }

        return FailClosedOrEscape(request, logger, "Unsupported OS for child-process filesystem jail.");
    }

    internal static bool CleanupTempPaths(
        IReadOnlyList<IdentityOwnedFileSystemArtifact>? artifacts)
    {
        if (artifacts is null || artifacts.Count == 0)
        {
            return true;
        }

        bool complete = true;

        foreach (IdentityOwnedFileSystemArtifact artifact in artifacts)
        {
            if (!IdentityOwnedFileSystemCleanup.TryDelete(artifact))
            {
                complete = false;
            }
        }

        return complete;
    }

    internal static async Task<bool> CleanupTempPathsAsync(
        IReadOnlyList<IdentityOwnedFileSystemArtifact>? artifacts,
        TimeSpan remaining,
        Func<IReadOnlyList<IdentityOwnedFileSystemArtifact>?, bool>?
            cleanupAction = null)
    {
        if (artifacts is null || artifacts.Count == 0)
        {
            return true;
        }

        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        Task<bool> cleanup = Task.Run(
            () => (cleanupAction ?? CleanupTempPaths)(artifacts));

        try
        {
            return await cleanup
                .WaitAsync(remaining)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = cleanup.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Undoes whatever the Windows broker did not undo itself. The broker restores the directory
    /// ACLs it granted and deletes the per-run AppContainer profile in a <c>finally</c>, but the
    /// runner kills it with TerminateProcess on timeout, cancellation, or a Job Object resource kill,
    /// and managed finally blocks do not run then. Left alone, the workspace root would keep one
    /// inheritable Allow ACE for a dead AppContainer SID per killed run until the DACL hits the
    /// 64 KiB limit and the jail stops working. Returns <c>false</c> when residue remains.
    /// </summary>
    internal static bool RestoreWindowsAppContainerState(
        ChildProcessSandboxApplyResult? sandboxResult,
        ILogger? logger)
    {
        string? journalPath = sandboxResult?.WindowsRestoreJournalPath;
        if (string.IsNullOrWhiteSpace(journalPath)
            || !OperatingSystem.IsWindows())
        {
            return true;
        }

        return ReplayWindowsRestoreJournal(journalPath, logger);
    }

    /// <summary>
    /// Tells the Windows broker that the host has assigned it to this run's own Job Object. Called by
    /// the runner only after <c>AssignProcessToJobObject</c> succeeded for the started broker; until
    /// then the broker refuses to create the target, whatever job it may have inherited. A run without
    /// a Windows broker has nothing to confirm. Returns <c>false</c> when the confirmation could not be
    /// written, in which case the broker will refuse the run.
    /// </summary>
    internal static bool ConfirmWindowsJobAssignment(
        ChildProcessSandboxApplyResult? sandboxResult)
    {
        string? signalPath = sandboxResult?.WindowsJobAssignedSignalPath;
        if (string.IsNullOrWhiteSpace(signalPath))
        {
            return true;
        }

        try
        {
            WindowsAppContainerJobAssignment.Confirm(signalPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lets the Windows-lane broker smoke run the real broker from a published apphost: the xunit host
    /// cannot broker, because re-executing it starts the test platform. Scoped to the calling async
    /// flow and never set in production.
    /// </summary>
    internal static IDisposable UseWindowsBrokerExecutableForTests(string brokerExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerExecutable);

        string? previous = WindowsBrokerExecutableForTests.Value;
        WindowsBrokerExecutableForTests.Value = brokerExecutable;
        return new BrokerExecutableScope(previous);
    }

    private static readonly AsyncLocal<string?> WindowsBrokerExecutableForTests = new();

    private sealed class BrokerExecutableScope(string? previous) : IDisposable
    {
        public void Dispose() => WindowsBrokerExecutableForTests.Value = previous;
    }

    [SupportedOSPlatform("windows")]
    private static bool ReplayWindowsRestoreJournal(
        string journalPath,
        ILogger? logger)
    {
        bool restored;

        // The replay blocks the tool call that owns it, so every root-lock wait in it shares one
        // deadline rather than each taking a full timeout.
        WindowsAppContainerRootLockBudget lockBudget = WindowsAppContainerRootLockBudget.StartPerRun();
        try
        {
            restored = WindowsAppContainerRestoreJournal.Replay(
                journalPath,
                (path, sid) => WindowsAppContainerLauncher.RemoveGrant(path, sid, lockBudget),
                WindowsAppContainerLauncher.DeleteProfile);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to read the Windows AppContainer undo log.");
            return false;
        }

        if (!restored)
        {
            logger?.LogError(
                "Windows AppContainer residue could not be fully undone from the broker undo log; an allowed root may retain an AppContainer access-control entry or the per-run profile may remain registered.");
        }

        return restored;
    }

    private static ChildProcessSandboxApplyResult ApplyWindows(
        ProcessStartInfo startInfo,
        ChildProcessSandboxRequest request,
        ILogger? logger)
    {
        // This jail does not wrap the child, it replaces it: the real target is handed to a re-execution
        // of this process in broker mode. A process whose own entry point does not route argv through
        // SandboxExecHelper.TryHandle has no broker mode, so re-executing it would run something other than
        // the tool — File.Exists on Environment.ProcessPath cannot tell the difference. Refuse before
        // touching startInfo, so an operator-escaped run still starts the untouched target rather than a
        // half-rewritten one.
        string? brokerOverride = WindowsBrokerExecutableForTests.Value;
        if (brokerOverride is null && !SandboxExecHelper.IsBrokerCapableHost)
        {
            return WindowsFailClosedOrEscape(request, logger, "This host process cannot act as the Windows sandbox broker.");
        }

        if (!WindowsAppContainerPolicy.IsSupported())
        {
            return WindowsFailClosedOrEscape(request, logger, "Windows AppContainer APIs are unavailable.");
        }

        // The broker starts the target with a non-null lpApplicationName, which Win32 uses as-is: no
        // PATH search, no default extension. Resolve it here, against the child's scrubbed PATH, so the
        // payload carries an absolute path. A target that cannot run that way is a refusal, not a
        // sandbox outage, so the operator escape hatch does not apply to it.
        Result<WindowsBrokerTarget> target = WindowsBrokerTargetResolver.Resolve(
            startInfo.FileName,
            startInfo.Environment.TryGetValue("PATH", out string? searchPath) ? searchPath : null,
            startInfo.Environment.TryGetValue("PATHEXT", out string? pathExt) ? pathExt : null,
            File.Exists,
            startInfo.WorkingDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (target.IsFailure)
        {
            logger?.LogWarning(
                "Windows sandboxed tool child refused before brokering: {Code}.",
                target.Error.Code);
            return new ChildProcessSandboxApplyResult
            {
                Status = ChildProcessSandboxApplyStatus.Unavailable,
                Detail = target.Error.Message,
            };
        }

        IdentityOwnedFileSystemArtifact? temp = null;
        IdentityOwnedFileSystemArtifact? config = null;
        IdentityOwnedFileSystemArtifact? journal = null;
        IdentityOwnedFileSystemArtifact? jobSignal = null;

        // TMP and TEMP are pointed at the per-run temp directory before the remaining artifacts are
        // written; a failure after that deletes the directory, so the entries are put back for an
        // operator-escaped run.
        EnvironmentEntrySnapshot tempEnvironment = EnvironmentEntrySnapshot.Capture(startInfo, "TMP", "TEMP");

        try
        {
            List<string> readWrite = NormalizeExistingRoots(request.ReadWriteRoots);
            List<string> readOnly = NormalizeExistingRoots(request.ReadOnlyRoots);
            List<string> readExecute = NormalizeExistingRoots(
                target.Value.ReadExecuteRoot is null
                    ? request.ReadExecuteRoots
                    : [.. request.ReadExecuteRoots, target.Value.ReadExecuteRoot]);
            if (readWrite.Concat(readOnly).Concat(readExecute)
                .Any(static root => !WindowsAppContainerPolicy.IsSafeRoot(root)))
            {
                return WindowsFailClosedOrEscape(request, logger, "Windows jail root validation failed.");
            }

            temp = CreateOwnerOnlyTempDirectory("arcanum-win-child-");
            readWrite.Add(temp.Value.Path);
            startInfo.Environment["TMP"] = temp.Value.Path;
            startInfo.Environment["TEMP"] = temp.Value.Path;

            // The undo log lives outside the jailed roots and is created here, by the host, so it
            // survives the broker being terminated without ever being reachable from the child.
            journal = WriteOwnerOnlyTempFile("arcanum-win-acl-", ".journal", string.Empty);

            // Written by the runner only once the started broker is in this run's Job Object.
            jobSignal = WriteOwnerOnlyTempFile("arcanum-win-job-", ".signal", string.Empty);

            SandboxExecHelperPayload payload = new()
            {
                Target = target.Value.Path,
                Arguments = [.. startInfo.ArgumentList],
                WorkingDirectory = startInfo.WorkingDirectory,
                ReadWriteRoots = [.. readWrite],
                ReadOnlyRoots = [.. readOnly],
                ReadExecuteRoots = [.. readExecute],
                WindowsProfileName = WindowsAppContainerPolicy.CreateProfileName(),
                WindowsRestoreJournalPath = journal.Value.Path,
                WindowsJobAssignedSignalPath = jobSignal.Value.Path,
            };
            string json = System.Text.Json.JsonSerializer.Serialize(
                payload,
                SandboxExecJsonContext.Default.SandboxExecHelperPayload);
            config = WriteOwnerOnlyTempFile("arcanum-win-sb-", ".json", json);

            string? host = brokerOverride ?? Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(host) || !File.Exists(host))
            {
                throw new InvalidOperationException("Trusted sandbox broker executable is unavailable.");
            }

            startInfo.FileName = host;
            startInfo.ArgumentList.Clear();
            startInfo.ArgumentList.Add(HelperArg);
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(config.Value.Path);
            startInfo.WorkingDirectory = temp.Value.Path;

            return new ChildProcessSandboxApplyResult
            {
                Status = ChildProcessSandboxApplyStatus.Applied,
                OwnedArtifactsToCleanup = [config.Value, temp.Value, journal.Value, jobSignal.Value],
                WindowsRestoreJournalPath = journal.Value.Path,
                WindowsJobAssignedSignalPath = jobSignal.Value.Path,
            };
        }
        catch (Exception ex)
        {
            List<IdentityOwnedFileSystemArtifact> cleanup = [];
            if (config is not null) cleanup.Add(config.Value);
            if (temp is not null) cleanup.Add(temp.Value);
            if (journal is not null) cleanup.Add(journal.Value);
            if (jobSignal is not null) cleanup.Add(jobSignal.Value);
            CleanupTempPaths(cleanup);
            tempEnvironment.Restore(startInfo);
            logger?.LogError(ex, "Failed to prepare Windows AppContainer broker.");
            return WindowsFailClosedOrEscape(request, logger, "Windows AppContainer setup failed.");
        }
    }

    private static ChildProcessSandboxApplyResult WindowsFailClosedOrEscape(
        ChildProcessSandboxRequest request,
        ILogger? logger,
        string detail)
    {
        if (!request.WindowsPathBoundaryRequired)
        {
            return FailClosedOrEscape(request, logger, detail);
        }

        logger?.LogWarning(
            "Windows AppContainer setup was not active while Sanctum path-boundary enforcement was required; refusing the tool child.");
        return new ChildProcessSandboxApplyResult
        {
            Status = ChildProcessSandboxApplyStatus.Unavailable,
            Detail = detail,
        };
    }

    /// <summary>
    /// Linux Landlock / <c>__sandbox-exec</c> helper remains in-tree but is not invoked for this beta.
    /// Fail-closed with an actionable public message unless the escape hatch is enabled.
    /// </summary>
    private static ChildProcessSandboxApplyResult ApplyLinux(
        ChildProcessSandboxRequest request,
        ILogger? logger) =>
        FailClosedOrEscape(request, logger, LinuxDeferredDetail);

    private static ChildProcessSandboxApplyResult ApplyMacOs(
        ProcessStartInfo startInfo,
        ChildProcessSandboxRequest request,
        ILogger? logger)
    {
        const string sandboxExecPath = "/usr/bin/sandbox-exec";

        if (!File.Exists(sandboxExecPath))
        {
            return FailClosedOrEscape(
                request,
                logger,
                "macOS sandbox-exec is not present on this host (deprecated Apple tool; may be absent on future releases).");
        }

        IdentityOwnedFileSystemArtifact? invocationTempArtifact = null;

        IdentityOwnedFileSystemArtifact? profileArtifact = null;

        // The per-run temp variables are pointed at the invocation temp directory before the profile is
        // built, so a failure after that point leaves them naming a directory the catch below deletes.
        // The operator escape then runs the child with no jail and those dangling variables.
        EnvironmentEntrySnapshot tempEnvironment = EnvironmentEntrySnapshot.Capture(startInfo, "TMPDIR", "TMP", "TEMP");

        try
        {
            List<string> readWriteRoots = NormalizeExistingRoots(request.ReadWriteRoots);

            List<string> readOnlyRoots = NormalizeExistingRoots(request.ReadOnlyRoots);

            List<string> readExecuteRoots = NormalizeExistingRoots(request.ReadExecuteRoots);

            if (readWriteRoots.Count == 0
                && readOnlyRoots.Count == 0
                && readExecuteRoots.Count == 0)
            {
                return FailClosedOrEscape(request, logger, "No allowed filesystem roots for the child-process jail.");
            }

            if (!string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
            {
                try
                {
                    string cwdFull = Path.GetFullPath(startInfo.WorkingDirectory);

                    if (Directory.Exists(cwdFull))
                    {
                        string? resolved = Directory.ResolveLinkTarget(cwdFull, returnFinalTarget: true)?.FullName
                                           ?? new DirectoryInfo(cwdFull).FullName;

                        startInfo.WorkingDirectory = Path.GetFullPath(resolved);
                    }
                }
                catch (Exception)
                {
                }
            }

            invocationTempArtifact =
                CreateOwnerOnlyTempDirectory("arcanum-child-tmp-");

            string invocationTempDir =
                invocationTempArtifact.Value.Path;

            startInfo.Environment["TMPDIR"] = invocationTempDir;

            startInfo.Environment["TMP"] = invocationTempDir;

            startInfo.Environment["TEMP"] = invocationTempDir;

            string profile = MacOsSandboxExecProfileBuilder.Build(
                readWriteRoots,
                readExecuteRoots,
                invocationTempDir,
                readOnlyRoots);

            profileArtifact = WriteOwnerOnlyTempFile(
                "arcanum-sb-",
                ".sb",
                profile);

            string profilePath = profileArtifact.Value.Path;

            WrapWithSandboxExec(startInfo, sandboxExecPath, profilePath);

            return new ChildProcessSandboxApplyResult
            {
                Status = ChildProcessSandboxApplyStatus.Applied,

                OwnedArtifactsToCleanup =
                [
                    profileArtifact.Value,
                    invocationTempArtifact.Value,
                ],
            };
        }
        catch (Exception ex)
        {
            List<IdentityOwnedFileSystemArtifact> cleanup = [];

            if (profileArtifact is not null)
            {
                cleanup.Add(profileArtifact.Value);
            }

            if (invocationTempArtifact is not null)
            {
                cleanup.Add(invocationTempArtifact.Value);
            }

            CleanupTempPaths(cleanup);

            tempEnvironment.Restore(startInfo);

            logger?.LogError(ex, "Failed to prepare macOS sandbox-exec wrapper.");

            return FailClosedOrEscape(request, logger, "Failed to prepare macOS sandbox-exec wrapper.");
        }
    }

    private static ChildProcessSandboxApplyResult FailClosedOrEscape(
        ChildProcessSandboxRequest request,
        ILogger? logger,
        string detail)
    {
        if (request.AllowUnsandboxed)
        {
            logger?.LogWarning(
                "Filesystem jail disabled by operator (AllowUnsandboxedToolChildren=true). Platform={Platform} Tool={ToolName} Workspace={Workspace} Campaign={CampaignId}. Detail={Detail}. {Note}",
                GetPlatformLabel(),
                string.IsNullOrWhiteSpace(request.ToolName) ? "(unknown)" : request.ToolName,
                string.IsNullOrWhiteSpace(request.WorkspaceRootForLog) ? "(none)" : RedactPathForLog(request.WorkspaceRootForLog),
                string.IsNullOrWhiteSpace(request.CampaignIdForLog) ? "(none)" : request.CampaignIdForLog,
                detail,
                ChildProcessSandboxMessages.NotNetworkIsolationNote);

            return new ChildProcessSandboxApplyResult
            {
                Status = ChildProcessSandboxApplyStatus.EscapedByOperator,

                Detail = detail,
            };
        }

        logger?.LogWarning(
            "Child process filesystem sandbox unavailable ({Detail}); refusing unbounded tool child. Platform={Platform} Tool={ToolName}. {Note}",
            detail,
            GetPlatformLabel(),
            string.IsNullOrWhiteSpace(request.ToolName) ? "(unknown)" : request.ToolName,
            ChildProcessSandboxMessages.NotNetworkIsolationNote);

        return new ChildProcessSandboxApplyResult
        {
            Status = ChildProcessSandboxApplyStatus.Unavailable,

            Detail = detail,
        };
    }

    /// <summary>
    /// The state of a few <see cref="ProcessStartInfo.Environment"/> entries before a prepare step
    /// rewrote them, so a prepare that fails can hand the caller back the environment it was given.
    /// </summary>
    private readonly struct EnvironmentEntrySnapshot
    {
        private readonly (string Name, bool Present, string? Value)[] _entries;

        private EnvironmentEntrySnapshot((string Name, bool Present, string? Value)[] entries) =>
            _entries = entries;

        internal static EnvironmentEntrySnapshot Capture(ProcessStartInfo startInfo, params string[] names)
        {
            (string Name, bool Present, string? Value)[] entries = new (string, bool, string?)[names.Length];

            for (int index = 0; index < names.Length; index++)
            {
                bool present = startInfo.Environment.TryGetValue(names[index], out string? value);

                entries[index] = (names[index], present, value);
            }

            return new EnvironmentEntrySnapshot(entries);
        }

        internal void Restore(ProcessStartInfo startInfo)
        {
            foreach ((string name, bool present, string? value) in _entries)
            {
                if (present)
                {
                    startInfo.Environment[name] = value;
                }
                else
                {
                    _ = startInfo.Environment.Remove(name);
                }
            }
        }
    }

    private static string GetPlatformLabel()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        return "Unknown";
    }

    private static string RedactPathForLog(string path)
    {
        // Keep only the last path segment for diagnostics — avoid dumping full home trees.
        try
        {
            return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                   ?? "(path)";
        }
        catch (Exception)
        {
            return "(path)";
        }
    }

    private static void WrapWithSandboxExec(ProcessStartInfo startInfo, string sandboxExecPath, string profilePath)
    {
        string target = startInfo.FileName;

        List<string> originalArgs = [.. startInfo.ArgumentList];

        startInfo.ArgumentList.Clear();

        startInfo.ArgumentList.Add("-f");

        startInfo.ArgumentList.Add(profilePath);

        startInfo.ArgumentList.Add(target);

        foreach (string arg in originalArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.FileName = sandboxExecPath;
    }

    private static IdentityOwnedFileSystemArtifact
        CreateOwnerOnlyTempDirectory(
            string prefix)
    {
        // Unix-domain socket paths are length-bounded on macOS. dotnet format's MSBuild build host
        // creates CoreFxPipe sockets beneath TMPDIR, so the normal per-user /var/folders path plus
        // our invocation name can exceed that limit and leave the host waiting forever. A unique
        // owner-only child directly beneath /private/tmp remains a narrow per-run jail grant.
        string tempRoot = OperatingSystem.IsMacOS()
            && Directory.Exists("/private/tmp")
                ? "/private/tmp"
                : Path.GetTempPath();

        string nonce = Guid.NewGuid().ToString("N");
        string directoryName = OperatingSystem.IsMacOS()
            ? "a-" + nonce[..10]
            : prefix + nonce;
        string path = Path.Combine(tempRoot, directoryName);

        Directory.CreateDirectory(path);

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                path,
                FileSystemObjectKind.Directory,
                out IdentityOwnedFileSystemArtifact artifact))
        {
            throw new IOException(
                "Could not capture child temp-directory identity.");
        }

        try
        {
            SecureFilePermissions.ApplyOwnerOnlyDirectory(path);

            return artifact;
        }
        catch
        {
            _ = IdentityOwnedFileSystemCleanup.TryDelete(artifact);

            throw;
        }
    }

    private static IdentityOwnedFileSystemArtifact
        WriteOwnerOnlyTempFile(
            string prefix,
            string extension,
            string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + extension);

        byte[] bytes = Encoding.UTF8.GetBytes(contents);

        IdentityOwnedFileSystemArtifact artifact = default;

        bool captured = false;

        try
        {
            using (FileStream stream = new(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.None))
            {
                if (!IdentityOwnedFileSystemCleanup.TryCaptureOpenFile(
                        path,
                        stream.SafeFileHandle,
                        out artifact))
                {
                    throw new IOException(
                        "Could not capture child sandbox-profile identity.");
                }

                captured = true;

                stream.Write(bytes, 0, bytes.Length);

                stream.Flush(flushToDisk: true);
            }

            SecureFilePermissions.ApplyOwnerOnlyFile(path);

            return artifact;
        }
        catch
        {
            if (captured)
            {
                _ = IdentityOwnedFileSystemCleanup.TryDelete(
                    artifact);
            }

            throw;
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    internal static List<string> NormalizeExistingRoots(IReadOnlyList<string> roots)
    {
        List<string> result = [];

        HashSet<string> seen = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            // Reject control characters / newlines before profile generation.
            foreach (char c in root)
            {
                if (char.IsControl(c))
                {
                    throw new InvalidOperationException(
                        "Sandbox root paths must not contain control characters or newlines.");
                }
            }

            string full;

            try
            {
                full = Path.GetFullPath(root.Trim());

                // Honour the name: a root that is neither a directory nor a file is dropped on every
                // platform. Kept, it is inert in a Seatbelt profile but makes the Windows broker's
                // grant throw, failing the run with no output.
                if (!Directory.Exists(full) && !File.Exists(full))
                {
                    continue;
                }

                if (File.Exists(full) && !Directory.Exists(full))
                {
                    string? parent = Path.GetDirectoryName(full);

                    if (!string.IsNullOrEmpty(parent))
                    {
                        full = Path.GetFullPath(parent);
                    }
                }

                string? resolved = null;

                try
                {
                    if (Directory.Exists(full))
                    {
                        resolved = Directory.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName
                                   ?? new DirectoryInfo(full).FullName;
                    }
                }
                catch (Exception)
                {
                    resolved = null;
                }

                if (!string.IsNullOrEmpty(resolved))
                {
                    full = Path.GetFullPath(resolved);
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            if (seen.Add(full))
            {
                result.Add(full);
            }
        }

        return result;
    }
}
