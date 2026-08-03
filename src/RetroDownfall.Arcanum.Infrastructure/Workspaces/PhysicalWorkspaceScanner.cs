using System.Text;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

public sealed class PhysicalWorkspaceScanner : IWorkspaceScanner
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git" };

    public Task<string> BuildProjectSummaryAsync(string? rootPath = null, CancellationToken cancellationToken = default)
    {
        string root = string.IsNullOrWhiteSpace(rootPath) ? Environment.CurrentDirectory : Path.GetFullPath(rootPath);

        if (!Directory.Exists(root))
        {
            return Task.FromResult($"Root path not found: {root}");
        }

        List<string> solutionFiles = [];

        try
        {
            foreach (string file in EnumerateSolutionFiles(root, cancellationToken))
            {
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

    private static IEnumerable<string> EnumerateSolutionFiles(
        string root,
        CancellationToken cancellationToken)
    {
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        string canonicalRoot = ResolveCanonicalPath(new DirectoryInfo(root));

        string rootPrefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;

        Stack<DirectoryInfo> pending = new();

        HashSet<string> visited = new(pathComparer);

        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DirectoryInfo directory = pending.Pop();

            string canonicalDirectory;

            try
            {
                canonicalDirectory = ResolveCanonicalPath(directory);
            }
            catch (Exception exception) when (IsSkippableFileSystemException(exception))
            {
                continue;
            }

            if (!IsWithinRoot(canonicalDirectory, canonicalRoot, rootPrefix, pathComparer) ||
                !visited.Add(canonicalDirectory))
            {
                continue;
            }

            FileSystemInfo[] entries;

            try
            {
                entries = directory.GetFileSystemInfos();
            }
            catch (Exception exception) when (IsSkippableFileSystemException(exception))
            {
                continue;
            }

            Array.Sort(entries, static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));

            for (int index = entries.Length - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileSystemInfo entry = entries[index];

                if (entry is DirectoryInfo childDirectory)
                {
                    if (!IgnoredDirectoryNames.Contains(childDirectory.Name))
                    {
                        pending.Push(childDirectory);
                    }

                    continue;
                }

                if (entry is not FileInfo file ||
                    !file.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string canonicalFile;

                try
                {
                    canonicalFile = ResolveCanonicalPath(file);
                }
                catch (Exception exception) when (IsSkippableFileSystemException(exception))
                {
                    continue;
                }

                if (IsWithinRoot(canonicalFile, canonicalRoot, rootPrefix, pathComparer))
                {
                    yield return file.FullName;
                }
            }
        }
    }

    private static string ResolveCanonicalPath(FileSystemInfo entry)
    {
        FileSystemInfo? target = entry.LinkTarget is null
            ? null
            : entry.ResolveLinkTarget(returnFinalTarget: true);

        return Path.GetFullPath(target?.FullName ?? entry.FullName);
    }

    private static bool IsWithinRoot(
        string candidate,
        string root,
        string rootPrefix,
        StringComparer comparer) =>
        comparer.Equals(candidate, root) ||
        candidate.StartsWith(
            rootPrefix,
            comparer == StringComparer.OrdinalIgnoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool IsSkippableFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}
