using System.Text;

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
        bool windowsPathBoundaryRequired)
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
        };

    }

    internal static ChildProcessSandboxRequest ForSpellScript(
        IReadOnlyList<string> scriptRoots,
        string? workspaceRoot,
        IReadOnlyList<string>? sanctumAllowedPaths,
        bool allowUnsandboxed,
        bool windowsPathBoundaryRequired)
    {

        List<string> readWrite = [];

        foreach (string root in scriptRoots)
        {

            AddRoot(readWrite, root);

            // Parent of scripts/ is often the spell dir; include it for relative asset reads.
            try
            {

                string? parent = Path.GetDirectoryName(Path.GetFullPath(root.Trim()));

                AddRoot(readWrite, parent);

            }
            catch (Exception)
            {

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

            ReadExecuteRoots = SystemRuntimeRoots(),

            AllowUnsandboxed = allowUnsandboxed,

            WindowsPathBoundaryRequired = windowsPathBoundaryRequired,
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

        try
        {

            string full = Path.GetFullPath(path.Trim());

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

            }

            if (roots.Exists(r => string.Equals(r, full, StringComparison.Ordinal)))
            {

                return;

            }

            roots.Add(full);

        }
        catch (Exception)
        {

        }

    }

}
