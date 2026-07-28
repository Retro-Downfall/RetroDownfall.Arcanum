using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Result of attempting to apply an OS filesystem jail to a tool child.
/// Windows without Sanctum path-boundary uses <see cref="NoFilesystemJail"/> — never <see cref="Applied"/>.
/// </summary>
internal enum ChildProcessSandboxApplyStatus
{

    /// <summary>macOS Seatbelt / sandbox-exec profile is active.</summary>
    Applied,

    /// <summary>Jail required but unavailable (Linux beta default, missing sandbox-exec, setup failure).</summary>
    Unavailable,

    /// <summary>Windows Sanctum Enabled + EnforcePathBoundary — child tools denied.</summary>
    DeniedByWindowsSanctum,

    /// <summary>Operator escape hatch: ran without FS jail (rlimits may still apply).</summary>
    EscapedByOperator,

    /// <summary>Windows without Sanctum path-boundary: no FS jail; Job Objects only.</summary>
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

            return ApplyWindows(request, logger);

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

    private static ChildProcessSandboxApplyResult ApplyWindows(
        ChildProcessSandboxRequest request,
        ILogger? logger)
    {

        if (request.WindowsPathBoundaryRequired)
        {

            logger?.LogWarning(
                "Refusing execute_command/run_spell_script on Windows: Sanctum path-boundary enforcement is enabled and no FS jail is available. {Note}",
                ChildProcessSandboxMessages.NotNetworkIsolationNote);

            return new ChildProcessSandboxApplyResult
            {

                Status = ChildProcessSandboxApplyStatus.DeniedByWindowsSanctum,

                Detail = ChildProcessSandboxMessages.WindowsSanctumPathBoundaryDenied,

            };

        }

        logger?.LogDebug(
            "Windows: no filesystem jail for this invocation (Job Object resource limits only). {Note}",
            ChildProcessSandboxMessages.NotNetworkIsolationNote);

        return new ChildProcessSandboxApplyResult
        {

            Status = ChildProcessSandboxApplyStatus.NoFilesystemJail,

            Detail = "Windows: no filesystem jail; Job Object resource limits only.",

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

    private static List<string> NormalizeExistingRoots(IReadOnlyList<string> roots)
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
