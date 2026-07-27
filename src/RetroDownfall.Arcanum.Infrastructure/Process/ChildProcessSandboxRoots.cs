using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Builds <see cref="ChildProcessSandboxRequest"/> roots for tool children.
/// </summary>
internal static class ChildProcessSandboxRoots
{

    /// <summary>
    /// Workspace checks always require an active jail. The source workspace and immutable package
    /// caches are read-only; only server-created per-run roots are writable.
    /// </summary>
    internal static ChildProcessSandboxRequest ForWorkspaceCheck(
        string workspaceRoot,
        IReadOnlyList<string> trustedReadOnlyRoots,
        IReadOnlyList<string> perRunWritableRoots,
        string? perRunControlRoot = null)
    {

        List<string> readOnly = [];
        List<string> readExecute = SystemRuntimeRoots();
        List<string> readWrite = [];

        string workspace = CanonicalRequiredDirectory(
            workspaceRoot,
            "workspace source root");

        AddRoot(readOnly, workspace);
        string? controlRoot = perRunControlRoot is null
            ? null
            : CanonicalRequiredDirectory(
                perRunControlRoot,
                "per-run control root");

        foreach (string root in trustedReadOnlyRoots)
        {

            string canonical = CanonicalRequiredDirectory(
                root,
                "trusted read-only root");

            AddRoot(readOnly, canonical);

            string leaf = Path.GetFileName(
                Path.TrimEndingDirectorySeparator(canonical));

            if (leaf.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
            {

                AddRoot(readExecute, canonical);
            }

        }

        foreach (string root in perRunWritableRoots)
        {

            string canonical = CanonicalRequiredDirectory(
                root,
                "per-run writable root");

            if (readOnly.Any(protectedRoot =>
                    PathsOverlap(canonical, protectedRoot)))
            {

                throw new InvalidOperationException(
                    "A workspace-check writable root overlaps the source, package cache, or SDK roots.");

            }

            AddRoot(readWrite, canonical);
        }

        AddRoot(readOnly, controlRoot);

        return new ChildProcessSandboxRequest
        {
            ReadWriteRoots = readWrite,
            ReadOnlyRoots = readOnly,
            ReadExecuteRoots = readExecute,
            AllowUnsandboxed = false,
            WindowsPathBoundaryRequired = true,
            RequireAppliedFilesystemJail = true,
            ToolName = ToolRiskClassifier.WorkspaceCheckToolName,
            WorkspaceRootForLog = workspaceRoot,
            SourceReadOnlyRoot = workspace,
            PackageReadOnlyRoot = trustedReadOnlyRoots.Count > 0
                ? CanonicalRequiredDirectory(
                    trustedReadOnlyRoots[0],
                    "package read-only root")
                : null,
        };
    }

    internal static ChildProcessSandboxRequest ForExecuteCommand(
        string workspaceRoot,
        IReadOnlyList<string>? sanctumAllowedPaths,
        bool allowUnsandboxed,
        bool windowsPathBoundaryRequired,
        string? toolName = null,
        string? campaignId = null)
    {

        List<string> readWrite = [];

        AddRoot(readWrite, workspaceRoot);

        if (sanctumAllowedPaths is not null)
        {

            foreach (string path in sanctumAllowedPaths)
            {

                AddRoot(readWrite, path);

            }

        }

        return new ChildProcessSandboxRequest
        {
            ReadWriteRoots = readWrite,

            ReadExecuteRoots = SystemRuntimeRoots(),

            AllowUnsandboxed = allowUnsandboxed,

            WindowsPathBoundaryRequired = windowsPathBoundaryRequired,

            ToolName = toolName ?? ToolRiskClassifier.ExecuteCommandToolName,

            CampaignIdForLog = campaignId,

            WorkspaceRootForLog = workspaceRoot,
        };

    }

    /// <summary>
    /// Spell scripts: script roots are read+execute only; workspace / AllowedPaths remain read+write.
    /// Global spell roots are not writable unless also listed as an AllowedPath / workspace.
    /// </summary>
    internal static ChildProcessSandboxRequest ForSpellScript(
        IReadOnlyList<string> scriptRoots,
        string? workspaceRoot,
        IReadOnlyList<string>? sanctumAllowedPaths,
        bool allowUnsandboxed,
        bool windowsPathBoundaryRequired,
        string? toolName = null,
        string? campaignId = null)
    {

        List<string> readWrite = [];

        List<string> readExecute = SystemRuntimeRoots();

        foreach (string root in scriptRoots)
        {

            AddRoot(readExecute, root);

            // Parent of scripts/ is often the spell dir; include for relative asset reads (R+X).
            try
            {

                string? parent = Path.GetDirectoryName(Path.GetFullPath(root.Trim()));

                AddRoot(readExecute, parent);

            }
            catch (Exception)
            {
                // Invalid/unresolvable path — skip this root candidate.
                System.Diagnostics.Debug.WriteLine("ChildProcessSandboxRoots: skipped unresolvable script parent path.");
            }

        }

        AddRoot(readWrite, workspaceRoot);

        if (sanctumAllowedPaths is not null)
        {

            foreach (string path in sanctumAllowedPaths)
            {

                AddRoot(readWrite, path);

            }

        }

        return new ChildProcessSandboxRequest
        {
            ReadWriteRoots = readWrite,

            ReadExecuteRoots = readExecute,

            AllowUnsandboxed = allowUnsandboxed,

            WindowsPathBoundaryRequired = windowsPathBoundaryRequired,

            ToolName = toolName ?? HostProcessToolPolicy.RunSpellScriptToolName,

            CampaignIdForLog = campaignId,

            WorkspaceRootForLog = workspaceRoot,
        };

    }

    private static string CanonicalRequiredDirectory(
        string path,
        string description)
    {

        if (string.IsNullOrWhiteSpace(path))
        {

            throw new InvalidOperationException(
                $"The workspace-check {description} is missing.");

        }

        try
        {

            string full = Path.GetFullPath(path.Trim());
            DirectoryInfo directory = new(full);

            if (!directory.Exists)
            {

                throw new InvalidOperationException(
                    $"The workspace-check {description} does not exist.");

            }

            string canonical = Path.GetFullPath(
                directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? directory.FullName);

            if (canonical is "/" or "\\")
            {

                throw new InvalidOperationException(
                    $"The workspace-check {description} cannot be a whole-volume root.");

            }

            return Path.TrimEndingDirectorySeparator(canonical);
        }
        catch (InvalidOperationException)
        {

            throw;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            throw new InvalidOperationException(
                $"The workspace-check {description} could not be canonicalized.",
                ex);

        }
    }

    private static bool PathsOverlap(string left, string right) =>
        WorkspaceRootPath.IsWithinOrEqual(left, right)
        || WorkspaceRootPath.IsWithinOrEqual(right, left);

    private static List<string> SystemRuntimeRoots()
    {

        List<string> roots = [];

        if (OperatingSystem.IsMacOS())
        {

            AddRoot(roots, "/usr");

            AddRoot(roots, "/bin");

            AddRoot(roots, "/sbin");

            AddRoot(roots, "/System");

            AddRoot(roots, "/Library");

            AddRoot(roots, "/private/preboot");

        }
        else if (OperatingSystem.IsLinux())
        {

            AddRoot(roots, "/usr");

            AddRoot(roots, "/bin");

            AddRoot(roots, "/sbin");

            AddRoot(roots, "/lib");

            AddRoot(roots, "/lib64");

            AddRoot(roots, "/etc");

        }

        // Common interpreter locations when installed outside the system prefixes.
        AddRoot(roots, "/opt/homebrew");

        AddRoot(roots, "/usr/local");

        return roots;

    }

    private static void AddRoot(List<string> roots, string? path)
    {

        if (string.IsNullOrWhiteSpace(path))
        {

            return;

        }

        foreach (char c in path)
        {

            if (char.IsControl(c))
            {

                return;

            }

        }

        try
        {

            string full = Path.GetFullPath(path.Trim());

            if (full is "/" or "\\")
            {

                return;

            }

            try
            {

                if (Directory.Exists(full))
                {

                    string? resolved = Directory.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName;

                    if (!string.IsNullOrEmpty(resolved))
                    {

                        full = Path.GetFullPath(resolved);

                    }
                    else
                    {

                        full = new DirectoryInfo(full).FullName;

                    }

                }
                else if (File.Exists(full))
                {

                    string? resolved = File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName;

                    if (!string.IsNullOrEmpty(resolved))
                    {

                        full = Path.GetFullPath(resolved);

                    }

                }

            }
            catch (Exception)
            {
                // Symlink resolution failed — keep the unresolved path.
                System.Diagnostics.Debug.WriteLine("ChildProcessSandboxRoots: symlink resolution failed; keeping path as-is.");
            }

            if (full is "/" or "\\")
            {

                return;

            }

            if (roots.Exists(r => string.Equals(r, full, StringComparison.Ordinal)))
            {

                return;

            }

            roots.Add(full);

        }
        catch (Exception)
        {
            // Invalid/unresolvable path — skip this root candidate.
            System.Diagnostics.Debug.WriteLine("ChildProcessSandboxRoots: skipped unresolvable sandbox root path.");
        }

    }

}
