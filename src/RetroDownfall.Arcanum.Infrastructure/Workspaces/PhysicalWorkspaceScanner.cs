using System.Text;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

public sealed class PhysicalWorkspaceScanner : IWorkspaceScanner
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git" };

    // Bound the recursive enumeration so a deep tree or directory-symlink cycle cannot scan unbounded,
    // consistent with the EyeOfTheWorldService step-budget approach.
    private const int MaxEnumerationSteps = 50_000;

    private const int MaxRecursionDepth = 64;

    public Task<string> BuildProjectSummaryAsync(string? rootPath = null, CancellationToken cancellationToken = default)
    {
        string root = string.IsNullOrWhiteSpace(rootPath) ? Environment.CurrentDirectory : Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            return Task.FromResult($"Root path not found: {root}");
        }

        List<string> solutionFiles = [];
        EnumerationOptions enumerationOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = MaxRecursionDepth,
        };
        int steps = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.sln", enumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (steps >= MaxEnumerationSteps)
                {
                    break;
                }
                steps++;
                if (IsUnderIgnoredPath(file, root))
                {
                    continue;
                }
                solutionFiles.Add(Path.GetRelativePath(root, file));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Workspace scan failed: {ex.Message}");
        }
        StringBuilder sb = new(512);
        sb.Append("Working directory: ").AppendLine(root);
        sb.Append("Solution files (excluding bin/obj/.git): ");
        if (solutionFiles.Count == 0)
        {
            sb.AppendLine("(none found)");
        }
        else
        {
            sb.AppendLine();
            foreach (string rel in solutionFiles.OrderBy(s => s, StringComparer.Ordinal))
            {
                sb.Append("  - ").AppendLine(rel);
            }
        }

        return Task.FromResult(sb.ToString());
    }

    private static bool IsUnderIgnoredPath(string fullPath, string root)
    {
        string rel = Path.GetRelativePath(root, fullPath);
        foreach (string part in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (IgnoredDirectoryNames.Contains(part))
            {
                return true;
            }
        }

        return false;
    }
}
