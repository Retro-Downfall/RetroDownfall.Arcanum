namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Builds <see cref="ChildProcessSandboxRequest"/> roots for tool children.
/// </summary>
internal static class ChildProcessSandboxRoots
{

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

            ToolName = toolName ?? "execute_command",

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

            ToolName = toolName ?? "run_spell_script",

            CampaignIdForLog = campaignId,

            WorkspaceRootForLog = workspaceRoot,
        };

    }

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
