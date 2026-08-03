using System.Security;

using System.IO.Enumeration;

using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

public sealed class PhysicalFileSystemBrowser : IFileSystemBrowser
{

    private const int ListPageSize = 500;

    public PhysicalFileSystemBrowser(IOptionsMonitor<ArcanumSettings> optionsMonitor)
    {
        ArgumentNullException.ThrowIfNull(optionsMonitor);
    }

    public Task<Result<FileListResult>> ListAsync(
        WorkspaceInfo workspace,
        string? relativePath,
        bool recursive,
        string? searchPattern,
        CancellationToken ct)
        => ListAsync(
            workspace,
            relativePath,
            recursive,
            searchPattern,
            cursor: null,
            ct);

    public Task<Result<FileListResult>> ListAsync(
        WorkspaceInfo workspace,
        string? relativePath,
        bool recursive,
        string? searchPattern,
        string? cursor,
        CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        if (searchPattern is not null && (searchPattern.Contains('/') || searchPattern.Contains('\\')))
        {
            return Task.FromResult<Result<FileListResult>>(
                new Error("Workspace.InvalidSearchPattern", "Search pattern cannot contain path separators."));
        }

        Result<string> resolvedResult = WorkspacePathResolver.ResolveRelativePath(workspace, relativePath);

        if (resolvedResult.IsFailure)
        {
            return Task.FromResult<Result<FileListResult>>(resolvedResult.Error);
        }

        string resolvedDir = resolvedResult.Value;

        if (!Directory.Exists(resolvedDir))
        {
            return Task.FromResult<Result<FileListResult>>(
                new Error("Workspace.FileNotFound", "The file or directory was not found."));
        }

        string workspaceRoot = Path.GetFullPath(workspace.Path);

        byte[] queryFingerprint =
            FileBrowserContinuationCursor.CreateQueryFingerprint(
                workspaceRoot,
                resolvedDir,
                recursive,
                searchPattern);

        FileBrowserContinuationDecodeResult cursorResult =
            FileBrowserContinuationCursor.TryDecode(
                cursor,
                queryFingerprint,
                out FileBrowserContinuationCheckpoint? continuationCheckpoint);

        if (cursorResult != FileBrowserContinuationDecodeResult.Success)
        {

            string message = cursorResult == FileBrowserContinuationDecodeResult.QueryMismatch
                ? "The continuation cursor belongs to different list arguments. Restart with cursor omitted."
                : "The file-list continuation cursor is invalid. Restart with cursor omitted.";

            return Task.FromResult<Result<FileListResult>>(
                new Error(ErrorCodes.Workspace.ContinuationInvalid, message));

        }

        SortedSet<FileEntry> candidates = new(
            new FileEntryRelativePathComparer());

        bool continuationCheckpointFound = continuationCheckpoint is null;

        Stack<IEnumerator<string>> directoryEnumerators = [];

        HashSet<string> visitedDirectories = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        try
        {

            _ = visitedDirectories.Add(Path.GetFullPath(resolvedDir));

            directoryEnumerators.Push(
                Directory.EnumerateFileSystemEntries(
                        resolvedDir,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .GetEnumerator());

            while (directoryEnumerators.Count > 0)
            {

                ct.ThrowIfCancellationRequested();

                IEnumerator<string> enumerator = directoryEnumerators.Peek();

                if (!enumerator.MoveNext())
                {

                    enumerator.Dispose();

                    _ = directoryEnumerators.Pop();

                    continue;

                }

                string fullPath = enumerator.Current;

                ct.ThrowIfCancellationRequested();

                if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                        workspaceRoot,
                        fullPath,
                        out string? resolvedPath))
                {

                    continue;

                }

                FileEntry? entry = TryMapToFileEntry(workspaceRoot, fullPath);

                if (entry is null)
                {

                    continue;

                }

                bool traverseDirectory = false;

                if (recursive && entry.Type == FileEntryType.Directory)
                {

                    string directoryIdentity = Path.GetFullPath(
                        resolvedPath ?? fullPath);

                    traverseDirectory = visitedDirectories.Add(directoryIdentity);

                }

                bool matchesPattern = searchPattern is null
                    || FileSystemName.MatchesSimpleExpression(
                        searchPattern,
                        entry.Name,
                        ignoreCase: OperatingSystem.IsWindows());

                if (matchesPattern)
                {

                    if (continuationCheckpoint?.Matches(entry) == true)
                    {

                        continuationCheckpointFound = true;

                    }

                    string normalizedRelativePath =
                        FileBrowserContinuationCursor.NormalizeRelativePath(
                            entry.RelativePath);

                    bool followsCursor = continuationCheckpoint is null
                        || FileBrowserContinuationCursor.PathComparer.Compare(
                            normalizedRelativePath,
                            continuationCheckpoint.RelativePath) > 0;

                    if (followsCursor)
                    {

                        _ = candidates.Add(entry);

                        if (candidates.Count > ListPageSize + 1)
                        {

                            _ = candidates.Remove(candidates.Max!);

                        }

                    }

                }

                if (traverseDirectory)
                {

                    directoryEnumerators.Push(
                        Directory.EnumerateFileSystemEntries(
                                fullPath,
                                "*",
                                SearchOption.TopDirectoryOnly)
                            .GetEnumerator());

                }

            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return Task.FromResult<Result<FileListResult>>(
                new Error("Workspace.AccessDenied", "Insufficient permissions to read the directory."));
        }
        finally
        {

            while (directoryEnumerators.Count > 0)
            {

                directoryEnumerators.Pop().Dispose();

            }

        }

        if (!continuationCheckpointFound)
        {

            return Task.FromResult<Result<FileListResult>>(
                new Error(
                    ErrorCodes.Workspace.ContinuationCheckpointMissing,
                    "The workspace changed and the continuation checkpoint no longer exists. Restart with cursor omitted."));

        }

        string? parentPath = ComputeParentRelativePath(workspaceRoot, resolvedDir);

        FileEntry[] orderedCandidates = candidates.ToArray();

        bool hasMore = orderedCandidates.Length > ListPageSize;

        FileEntry[] entries = hasMore
            ? orderedCandidates[..ListPageSize]
            : orderedCandidates;

        string? nextCursor = hasMore
            ? FileBrowserContinuationCursor.Encode(
                queryFingerprint,
                entries[^1])
            : null;

        return Task.FromResult<Result<FileListResult>>(
            new FileListResult(
                entries.ToArray(),
                parentPath,
                nextCursor,
                nextCursor is null
                    ? null
                    : "Call the workspace files endpoint again with cursor set to nextCursor and the same list arguments."));
    }

    public async Task<Result<FileReadResult>> ReadAsync(
        WorkspaceInfo workspace,
        string relativePath,
        CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        Result<string> resolvedResult = WorkspacePathResolver.ResolveRelativePath(workspace, relativePath);

        if (resolvedResult.IsFailure)
        {
            return resolvedResult.Error;
        }

        string resolvedPath = resolvedResult.Value;

        string workspaceRoot = Path.GetFullPath(workspace.Path);

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, resolvedPath))
        {
            return new Error(
                ErrorCodes.Workspace.SymbolicLinkEscape,
                "The path resolves outside the workspace via a symbolic link.");
        }

        if (!FileHandleIdentityInterop.TryGetPathMetadata(
                resolvedPath,
                out FileHandleMetadata expectedMetadata))
        {
            return !File.Exists(resolvedPath)
                   && !Directory.Exists(resolvedPath)
                ? new Error(
                    ErrorCodes.Workspace.FileNotFound,
                    "The file or directory was not found.")
                : new Error(
                    ErrorCodes.Workspace.SymbolicLinkEscape,
                    "The path could not be proven to be a safe workspace file.");
        }

        if (expectedMetadata.Kind == FileSystemObjectKind.Directory)
        {
            return new Error(
                ErrorCodes.Workspace.FileNotFound,
                "The file or directory was not found.");
        }

        if (expectedMetadata.Kind != FileSystemObjectKind.RegularFile
            || expectedMetadata.HardLinkCount != 1)
        {
            return new Error(
                ErrorCodes.Workspace.SymbolicLinkEscape,
                "The path could not be proven to be a safe workspace file.");
        }

        int maxBytes = checked((int)GetMaxFileReadSizeBytes());

        SecureUtf8FileReadResult readResult =
            await SecureFileReader.ReadUtf8TextAsync(
                    resolvedPath,
                    maxBytes,
                    ct,
                    expectedMetadata.Identity)
                .ConfigureAwait(false);

        if (readResult.Status is not SecureFileReadStatus.Success
            || readResult.Text is null)
        {
            return MapSecureReadError(readResult.Status);
        }

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(
                workspaceRoot,
                resolvedPath)
            || !FileHandleIdentityInterop.TryGetPathMetadata(
                resolvedPath,
                out FileHandleMetadata finalMetadata)
            || finalMetadata.Kind != FileSystemObjectKind.RegularFile
            || finalMetadata.HardLinkCount != 1
            || !FileHandleIdentity.IdentitiesMatch(
                readResult.Metadata.Identity,
                finalMetadata.Identity))
        {
            return new Error(
                ErrorCodes.Workspace.SymbolicLinkEscape,
                "The path could not be proven to remain inside the workspace.");
        }

        try
        {
            string entryRelativePath =
                Path.GetRelativePath(workspaceRoot, resolvedPath);

            return new FileReadResult(
                entryRelativePath,
                readResult.Text,
                "utf-8",
                readResult.ByteLength,
                File.GetLastWriteTimeUtc(resolvedPath));
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or SecurityException)
        {
            return new Error("Workspace.AccessDenied", "Insufficient permissions to read the file.");
        }
    }

    public Task<Result<FileEntry>> GetInfoAsync(
        WorkspaceInfo workspace,
        string? relativePath,
        CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        Result<string> resolvedResult = WorkspacePathResolver.ResolveRelativePath(workspace, relativePath);

        if (resolvedResult.IsFailure)
        {
            return Task.FromResult<Result<FileEntry>>(resolvedResult.Error);
        }

        string resolvedPath = resolvedResult.Value;

        string workspaceRoot = Path.GetFullPath(workspace.Path);

        if (WorkspaceRootPolicy.IsSamePath(workspaceRoot, resolvedPath))
        {
            DirectoryInfo rootInfo = new(workspaceRoot);

            return Task.FromResult<Result<FileEntry>>(new FileEntry(
                rootInfo.Name,
                string.Empty,
                workspaceRoot,
                FileEntryType.Directory,
                0,
                rootInfo.LastWriteTimeUtc));
        }

        if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
        {
            return Task.FromResult<Result<FileEntry>>(
                new Error("Workspace.FileNotFound", "The file or directory was not found."));
        }

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, resolvedPath))
        {
            return Task.FromResult<Result<FileEntry>>(
                new Error("Workspace.SymbolicLinkEscape", "The path resolves outside the workspace via a symbolic link."));
        }

        try
        {

            FileEntry? entry = TryMapToFileEntry(workspaceRoot, resolvedPath);

            if (entry is null)
            {
                return Task.FromResult<Result<FileEntry>>(
                    new Error("Workspace.FileNotFound", "The file or directory was not found."));
            }

            return Task.FromResult<Result<FileEntry>>(entry);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return Task.FromResult<Result<FileEntry>>(
                new Error("Workspace.AccessDenied", "Insufficient permissions to read the file or directory."));
        }
    }

    private sealed class FileEntryRelativePathComparer : IComparer<FileEntry>
    {

        public int Compare(FileEntry? left, FileEntry? right)
        {

            if (ReferenceEquals(left, right))
            {

                return 0;

            }

            if (left is null)
            {

                return -1;

            }

            if (right is null)
            {

                return 1;

            }

            int pathComparison = FileBrowserContinuationCursor.PathComparer.Compare(
                FileBrowserContinuationCursor.NormalizeRelativePath(
                    left.RelativePath),
                FileBrowserContinuationCursor.NormalizeRelativePath(
                    right.RelativePath));

            return pathComparison != 0
                ? pathComparison
                : left.Type.CompareTo(right.Type);

        }

    }

    private long GetMaxFileReadSizeBytes()
    {
        long configured = ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes;

        return ArcanumSettingClamps.MaxFileReadSizeBytes(configured);
    }

    private static Error MapSecureReadError(
        SecureFileReadStatus status) =>
        status switch
        {
            SecureFileReadStatus.NotFound =>
                new Error(
                    ErrorCodes.Workspace.FileNotFound,
                    "The file or directory was not found."),
            SecureFileReadStatus.TooLarge =>
                new Error(
                    ErrorCodes.Workspace.FileTooLarge,
                    "The file exceeds the maximum read size limit."),
            SecureFileReadStatus.Rejected =>
                new Error(
                    ErrorCodes.Workspace.SymbolicLinkEscape,
                    "The path could not be proven to be a safe workspace file."),
            _ =>
                new Error(
                    ErrorCodes.Workspace.AccessDenied,
                    "The file could not be read safely."),
        };

    private static FileEntry? TryMapToFileEntry(string workspaceRoot, string fullPath)
    {

        try
        {

            if (Directory.Exists(fullPath))
            {

                DirectoryInfo dirInfo = new(fullPath);

                string relativePath = Path.GetRelativePath(workspaceRoot, fullPath);

                return new FileEntry(
                    dirInfo.Name,
                    relativePath,
                    fullPath,
                    FileEntryType.Directory,
                    0,
                    dirInfo.LastWriteTimeUtc);
            }

            if (File.Exists(fullPath))
            {

                FileInfo fileInfo = new(fullPath);

                string relativePath = Path.GetRelativePath(workspaceRoot, fullPath);

                return new FileEntry(
                    fileInfo.Name,
                    relativePath,
                    fullPath,
                    FileEntryType.File,
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private static string? ComputeParentRelativePath(string workspaceRoot, string resolvedDir)
    {

        if (WorkspaceRootPolicy.IsSamePath(workspaceRoot, resolvedDir))
        {
            return null;
        }

        string parentFull = Path.GetDirectoryName(resolvedDir) ?? workspaceRoot;

        if (WorkspaceRootPolicy.IsSamePath(workspaceRoot, parentFull))
        {
            return string.Empty;
        }

        return Path.GetRelativePath(workspaceRoot, parentFull);
    }

}
