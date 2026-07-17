using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

internal enum ChildProcessSandboxApplyStatus
{

    Applied,

    Unavailable,

    DeniedByWindowsSanctum,

    EscapedUnsandboxed,

}

internal sealed class ChildProcessSandboxApplyResult
{

    internal ChildProcessSandboxApplyStatus Status { get; init; }

    /// <summary>Owner-only temp files (profile/config) to delete after the child exits.</summary>
    internal IReadOnlyList<string> TempPathsToCleanup { get; init; } = [];

    internal string? Detail { get; init; }

}

/// <summary>
/// Rewrites <see cref="ProcessStartInfo"/> so the child runs under an OS filesystem jail
/// (macOS <c>sandbox-exec</c>, Linux Landlock via <c>__sandbox-exec</c>), or fail-closes.
/// </summary>
internal static class ChildProcessFilesystemJail
{

    internal const string HelperArg = "__sandbox-exec";

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

            return ApplyLinux(startInfo, request, logger);

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

            }
            catch (Exception)
            {

                // Best-effort cleanup of owner-only temp profile/config.

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

        // No Sanctum path containment required: Job Objects / rlimits only (S3); no FS jail.
        return new ChildProcessSandboxApplyResult
        {

            Status = ChildProcessSandboxApplyStatus.Applied,

            Detail = "Windows: filesystem jail not required for this invocation.",

        };

    }

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

        List<string> readWriteRoots = NormalizeExistingRoots(request.ReadWriteRoots);

        List<string> readExecuteRoots = NormalizeExistingRoots(request.ReadExecuteRoots);

        if (readWriteRoots.Count == 0 && readExecuteRoots.Count == 0)
        {

            return FailClosedOrEscape(request, logger, "No allowed filesystem roots for the child-process jail.");

        }

        // Align WorkingDirectory with realpath so Seatbelt subpath filters match kernel paths
        // (/var → /private/var on macOS).
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

        string profile;

        try
        {

            profile = MacOsSandboxExecProfileBuilder.Build(readWriteRoots, readExecuteRoots);

        }
        catch (Exception ex)
        {

            logger?.LogError(ex, "Failed to build macOS sandbox-exec profile.");

            return FailClosedOrEscape(request, logger, "Failed to build macOS sandbox-exec profile.");

        }

        string? profilePath = null;

        try
        {

            profilePath = WriteOwnerOnlyTempFile("arcanum-sb-", ".sb", profile);

            WrapWithSandboxExec(startInfo, sandboxExecPath, profilePath);

            return new ChildProcessSandboxApplyResult
            {

                Status = ChildProcessSandboxApplyStatus.Applied,

                TempPathsToCleanup = [profilePath],

            };

        }
        catch (Exception ex)
        {

            if (profilePath is not null)
            {

                CleanupTempPaths([profilePath]);

            }

            logger?.LogError(ex, "Failed to prepare macOS sandbox-exec wrapper.");

            return FailClosedOrEscape(request, logger, "Failed to prepare macOS sandbox-exec wrapper.");

        }

    }

    private static ChildProcessSandboxApplyResult ApplyLinux(
        ProcessStartInfo startInfo,
        ChildProcessSandboxRequest request,
        ILogger? logger)
    {

        string? processPath = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {

            return FailClosedOrEscape(
                request,
                logger,
                "Environment.ProcessPath is unavailable; cannot invoke the Landlock __sandbox-exec helper.");

        }

        List<string> readWriteRoots = NormalizeExistingRoots(request.ReadWriteRoots);

        List<string> readExecuteRoots = NormalizeExistingRoots(request.ReadExecuteRoots);

        if (readWriteRoots.Count == 0 && readExecuteRoots.Count == 0)
        {

            return FailClosedOrEscape(request, logger, "No allowed filesystem roots for the child-process jail.");

        }

        SandboxExecHelperPayload payload = new()
        {
            Target = startInfo.FileName,

            Arguments = [.. startInfo.ArgumentList],

            ReadWriteRoots = [.. readWriteRoots],

            ReadExecuteRoots = [.. readExecuteRoots],

            WorkingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
                ? null
                : startInfo.WorkingDirectory,
        };

        string? configPath = null;

        try
        {

            string json = JsonSerializer.Serialize(payload, SandboxExecJsonContext.Default.SandboxExecHelperPayload);

            configPath = WriteOwnerOnlyTempFile("arcanum-ll-", ".json", json);

            startInfo.ArgumentList.Clear();

            startInfo.ArgumentList.Add(HelperArg);

            startInfo.ArgumentList.Add("--config");

            startInfo.ArgumentList.Add(configPath);

            startInfo.FileName = processPath;

            return new ChildProcessSandboxApplyResult
            {

                Status = ChildProcessSandboxApplyStatus.Applied,

                TempPathsToCleanup = [configPath],

            };

        }
        catch (Exception ex)
        {

            if (configPath is not null)
            {

                CleanupTempPaths([configPath]);

            }

            logger?.LogError(ex, "Failed to prepare Linux Landlock __sandbox-exec helper.");

            return FailClosedOrEscape(request, logger, "Failed to prepare Linux Landlock __sandbox-exec helper.");

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
                "Child process filesystem sandbox unavailable ({Detail}); AllowUnsandboxedToolChildren=true — running without FS jail. {Note}",
                detail,
                ChildProcessSandboxMessages.NotNetworkIsolationNote);

            return new ChildProcessSandboxApplyResult
            {

                Status = ChildProcessSandboxApplyStatus.EscapedUnsandboxed,

                Detail = detail,

            };

        }

        logger?.LogWarning(
            "Child process filesystem sandbox unavailable ({Detail}); refusing unbounded tool child. {Note}",
            detail,
            ChildProcessSandboxMessages.NotNetworkIsolationNote);

        return new ChildProcessSandboxApplyResult
        {

            Status = ChildProcessSandboxApplyStatus.Unavailable,

            Detail = detail,

        };

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

            string full;

            try
            {

                full = Path.GetFullPath(root.Trim());

                if (Directory.Exists(full) || File.Exists(full))
                {

                    // Prefer directory form for Landlock/sandbox-exec subpath rules.
                    if (File.Exists(full) && !Directory.Exists(full))
                    {

                        string? parent = Path.GetDirectoryName(full);

                        if (!string.IsNullOrEmpty(parent))
                        {

                            full = Path.GetFullPath(parent);

                        }

                    }

                }
                else
                {

                    // Still include the intended root so the jail grants the path once created;
                    // sandbox-exec / Landlock accept non-existent paths as allow rules on some hosts.
                    full = Path.GetFullPath(root.Trim());

                }

                // Resolve symlinks when possible so deny/allow filters match kernel paths (e.g. /var → /private/var).
                string? resolved = null;

                try
                {

                    if (Directory.Exists(full))
                    {

                        resolved = Directory.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName
                                   ?? new DirectoryInfo(full).FullName;

                    }
                    else if (File.Exists(full))
                    {

                        resolved = File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName
                                   ?? new FileInfo(full).FullName;

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
