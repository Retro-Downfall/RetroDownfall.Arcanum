using System.Diagnostics.CodeAnalysis;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal enum WorkspaceTraversalNodeKind
{
    File,
    Directory,
    FileSymlink,
    DirectorySymlink,
}

internal readonly record struct WorkspaceTraversalNode(
    string Name,
    WorkspaceTraversalNodeKind Kind);

internal interface IWorkspaceTraversalFileSystem
{

    IEnumerable<WorkspaceTraversalNode> EnumerateDirectory(
        string workspaceRoot,
        string relativeDirectory,
        CancellationToken cancellationToken);

}

internal readonly record struct WorkspaceTraversalLimits(int MaxSteps, int MaxFiles);

internal readonly record struct WorkspaceTraversalFile(
    string RelativePath,
    bool IsSymbolicLink = false);

internal sealed record WorkspaceTraversalResult(
    IReadOnlyList<WorkspaceTraversalFile> Files,
    int Steps,
    int Skipped,
    bool Truncated,
    bool StepLimitReached = false,
    bool FileLimitReached = false,
    bool StopRequested = false,
    int SkippedDirectorySymlinkCount = 0,
    int SkippedFilteredFileCount = 0,
    int SkippedPrunedDirectoryCount = 0);

/// <summary>
/// Bounded deterministic traversal. The provider seam makes ordering and exhaustion behavior
/// directly testable; physical enumeration order is never exposed. Directory symlinks are skipped.
/// File symlinks may be returned and must be identity-deduplicated by the consuming search.
/// </summary>
internal static class DeterministicWorkspaceTraversal
{

    internal static WorkspaceTraversalResult Traverse(
        string workspaceRoot,
        WorkspaceTraversalLimits limits,
        IWorkspaceTraversalFileSystem? fileSystem = null,
        CancellationToken cancellationToken = default,
        Func<bool>? shouldStop = null,
        Func<string, bool>? includeFile = null,
        Func<string, bool>? traverseDirectory = null)
    {

        if (limits.MaxSteps <= 0)
        {

            throw new ArgumentOutOfRangeException(nameof(limits), "MaxSteps must be positive.");

        }

        if (limits.MaxFiles <= 0)
        {

            throw new ArgumentOutOfRangeException(nameof(limits), "MaxFiles must be positive.");

        }

        string root = Path.GetFullPath(workspaceRoot);

        IWorkspaceTraversalFileSystem provider =
            fileSystem ?? PhysicalWorkspaceTraversalFileSystem.Instance;

        PriorityQueue<PendingNode, string> pending =
            new(WorkspaceRelativePath.DeterministicComparer);

        List<WorkspaceTraversalFile> files = [];

        int steps = 0;

        int skipped = 0;

        int skippedDirectorySymlinks = 0;

        int skippedFilteredFiles = 0;

        int skippedPrunedDirectories = 0;

        bool stepLimitReached = false;

        bool fileLimitReached = false;

        bool stopRequested = false;

        bool truncated = !EnqueueChildren(
            root,
            string.Empty,
            provider,
            pending,
            limits.MaxSteps,
            cancellationToken,
            shouldStop,
            ref steps,
            ref skipped,
            ref stepLimitReached,
            ref stopRequested,
            includeFile,
            traverseDirectory,
            ref skippedFilteredFiles,
            ref skippedPrunedDirectories);

        while (pending.Count > 0)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (shouldStop?.Invoke() == true)
            {

                stopRequested = true;

                truncated = true;

                break;

            }

            PendingNode current = pending.Dequeue();

            switch (current.Kind)
            {
                case WorkspaceTraversalNodeKind.File:
                    if (files.Count >= limits.MaxFiles)
                    {

                        fileLimitReached = true;

                        truncated = true;

                        break;

                    }

                    files.Add(new WorkspaceTraversalFile(current.RelativePath));

                    break;

                case WorkspaceTraversalNodeKind.FileSymlink:
                    if (files.Count >= limits.MaxFiles)
                    {

                        fileLimitReached = true;

                        truncated = true;

                        break;

                    }

                    files.Add(
                        new WorkspaceTraversalFile(
                            current.RelativePath,
                            IsSymbolicLink: true));

                    break;

                case WorkspaceTraversalNodeKind.Directory:
                    truncated |= !EnqueueChildren(
                        root,
                        current.RelativePath,
                        provider,
                        pending,
                        limits.MaxSteps,
                        cancellationToken,
                        shouldStop,
                        ref steps,
                        ref skipped,
                        ref stepLimitReached,
                        ref stopRequested,
                        includeFile,
                        traverseDirectory,
                        ref skippedFilteredFiles,
                        ref skippedPrunedDirectories);

                    break;

                case WorkspaceTraversalNodeKind.DirectorySymlink:
                    skipped++;

                    skippedDirectorySymlinks++;

                    break;

                default:
                    skipped++;

                    break;
            }

            if (fileLimitReached)
            {

                break;

            }

        }

        return new WorkspaceTraversalResult(
            files.AsReadOnly(),
            steps,
            skipped,
            truncated,
            stepLimitReached,
            fileLimitReached,
            stopRequested,
            skippedDirectorySymlinks,
            skippedFilteredFiles,
            skippedPrunedDirectories);

    }

    private static bool EnqueueChildren(
        string workspaceRoot,
        string relativeDirectory,
        IWorkspaceTraversalFileSystem provider,
        PriorityQueue<PendingNode, string> pending,
        int maxSteps,
        CancellationToken cancellationToken,
        Func<bool>? shouldStop,
        ref int steps,
        ref int skipped,
        ref bool stepLimitReached,
        ref bool stopRequested,
        Func<string, bool>? includeFile,
        Func<string, bool>? traverseDirectory,
        ref int skippedFilteredFiles,
        ref int skippedPrunedDirectories)
    {

        try
        {

            using IEnumerator<WorkspaceTraversalNode> enumerator = provider
                .EnumerateDirectory(
                    workspaceRoot,
                    relativeDirectory,
                    cancellationToken)
                .GetEnumerator();

            while (true)
            {

                cancellationToken.ThrowIfCancellationRequested();

                if (shouldStop?.Invoke() == true)
                {

                    stopRequested = true;

                    return false;

                }

                if (steps >= maxSteps)
                {

                    stepLimitReached = true;

                    return false;

                }

                if (!enumerator.MoveNext())
                {

                    return true;

                }

                cancellationToken.ThrowIfCancellationRequested();

                if (shouldStop?.Invoke() == true)
                {

                    stopRequested = true;

                    return false;

                }

                steps++;

                WorkspaceTraversalNode child = enumerator.Current;

                if (!TryCombine(relativeDirectory, child.Name, out string? relativePath))
                {

                    skipped++;

                    continue;

                }

                if (child.Kind == WorkspaceTraversalNodeKind.Directory
                    && traverseDirectory?.Invoke(relativePath) == false)
                {

                    skipped++;

                    skippedPrunedDirectories++;

                    continue;

                }

                if (child.Kind is WorkspaceTraversalNodeKind.File
                        or WorkspaceTraversalNodeKind.FileSymlink
                    && includeFile?.Invoke(relativePath) == false)
                {

                    skipped++;

                    skippedFilteredFiles++;

                    continue;

                }

                PendingNode node = new(relativePath, child.Kind);

                pending.Enqueue(node, relativePath);

            }

        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or DirectoryNotFoundException)
        {

            skipped++;

            return true;

        }

    }

    private static bool TryCombine(
        string relativeDirectory,
        string name,
        [NotNullWhen(true)] out string? relativePath)
    {

        relativePath = null;

        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.Contains('/') || name.Contains('\\') || name.Contains('\0'))
        {

            return false;

        }

        string candidate = relativeDirectory.Length == 0
            ? name
            : $"{relativeDirectory}/{name}";

        return WorkspaceRelativePath.TryNormalize(candidate, out relativePath);

    }

    private readonly record struct PendingNode(
        string RelativePath,
        WorkspaceTraversalNodeKind Kind);

    private sealed class PhysicalWorkspaceTraversalFileSystem : IWorkspaceTraversalFileSystem
    {

        internal static PhysicalWorkspaceTraversalFileSystem Instance { get; } = new();

        public IEnumerable<WorkspaceTraversalNode> EnumerateDirectory(
            string workspaceRoot,
            string relativeDirectory,
            CancellationToken cancellationToken)
        {

            string absoluteDirectory = relativeDirectory.Length == 0
                ? workspaceRoot
                : Path.Combine(
                    workspaceRoot,
                    relativeDirectory.Replace('/', Path.DirectorySeparatorChar));

            foreach (string entry in Directory.EnumerateFileSystemEntries(absoluteDirectory))
            {

                cancellationToken.ThrowIfCancellationRequested();

                FileAttributes attributes = File.GetAttributes(entry);

                bool isDirectory = attributes.HasFlag(FileAttributes.Directory);

                bool isSymlink = attributes.HasFlag(FileAttributes.ReparsePoint);

                WorkspaceTraversalNodeKind kind = (isDirectory, isSymlink) switch
                {
                    (true, true) => WorkspaceTraversalNodeKind.DirectorySymlink,
                    (true, false) => WorkspaceTraversalNodeKind.Directory,
                    (false, true) => WorkspaceTraversalNodeKind.FileSymlink,
                    _ => WorkspaceTraversalNodeKind.File,
                };

                yield return new WorkspaceTraversalNode(Path.GetFileName(entry), kind);

            }

        }

    }

}
