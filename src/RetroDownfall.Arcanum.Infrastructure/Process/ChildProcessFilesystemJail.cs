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

    /// <summary>Owner-only temp files (profile/config/invocation TMPDIR) to delete after the child exits.</summary>
    internal IReadOnlyList<string> TempPathsToCleanup { get; init; } = [];

    internal string? Detail { get; init; }

}

/// <summary>
/// Rewrites <see cref="ProcessStartInfo"/> so the child runs under an OS filesystem jail
/// (macOS <c>sandbox-exec</c> for Apple Silicon beta), or fail-closes.
/// Linux Landlock helper code remains in-tree but is <b>inactive</b> for this beta.
/// </summary>
internal static class ChildProcessFilesystemJail
{

    internal const string HelperArg = "__sandbox-exec";

    internal const string LinuxDeferredDetail =
        "Linux filesystem jail deferred for macOS-ARM beta; Landlock not active.";

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

    internal static void CleanupTempPaths(IReadOnlyList<string>? paths)
    {

        if (paths is null || paths.Count == 0)
        {

            return;

        }

        foreach (string path in paths)
        {

            if (string.IsNullOrWhiteSpace(path))
            {

                continue;

            }

            try
            {

                if (File.Exists(path))
                {

                    File.Delete(path);

                }
                else if (Directory.Exists(path))
                {

                    Directory.Delete(path, recursive: true);

                }

            }
            catch (Exception)
            {

                // Best-effort cleanup of owner-only temp profile/config/TMPDIR.

            }

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
    /// Linux Landlock is present in-tree but inactive for the macOS-ARM beta.
    /// Never invokes <c>__sandbox-exec</c> / Landlock from this path.
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

        string? invocationTempDir = null;

        string? profilePath = null;

        try
        {

            List<string> readWriteRoots = NormalizeExistingRoots(request.ReadWriteRoots);

            List<string> readExecuteRoots = NormalizeExistingRoots(request.ReadExecuteRoots);

            if (readWriteRoots.Count == 0 && readExecuteRoots.Count == 0)
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

            invocationTempDir = CreateOwnerOnlyTempDirectory("arcanum-child-tmp-");

            startInfo.Environment["TMPDIR"] = invocationTempDir;

            startInfo.Environment["TMP"] = invocationTempDir;

            startInfo.Environment["TEMP"] = invocationTempDir;

            string profile = MacOsSandboxExecProfileBuilder.Build(
                readWriteRoots,
                readExecuteRoots,
                invocationTempDir);

            profilePath = WriteOwnerOnlyTempFile("arcanum-sb-", ".sb", profile);

            WrapWithSandboxExec(startInfo, sandboxExecPath, profilePath);

            return new ChildProcessSandboxApplyResult
            {

                Status = ChildProcessSandboxApplyStatus.Applied,

                TempPathsToCleanup = [profilePath, invocationTempDir],

            };

        }
        catch (Exception ex)
        {

            List<string> cleanup = [];

            if (profilePath is not null)
            {

                cleanup.Add(profilePath);

            }

            if (invocationTempDir is not null)
            {

                cleanup.Add(invocationTempDir);

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

    private static string CreateOwnerOnlyTempDirectory(string prefix)
    {

        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(path);

        SecureFilePermissions.ApplyOwnerOnlyDirectory(path);

        return path;

    }

    private static string WriteOwnerOnlyTempFile(string prefix, string extension, string contents)
    {

        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + extension);

        byte[] bytes = Encoding.UTF8.GetBytes(contents);

        using (FileStream stream = new(
                   path,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.None))
        {

            stream.Write(bytes, 0, bytes.Length);

            stream.Flush(flushToDisk: true);

        }

        SecureFilePermissions.ApplyOwnerOnlyFile(path);

        return path;

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
