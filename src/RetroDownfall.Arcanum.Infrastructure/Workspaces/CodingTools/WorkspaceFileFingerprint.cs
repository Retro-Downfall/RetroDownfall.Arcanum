using System.Security.Cryptography;

using Microsoft.Win32.SafeHandles;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal enum WorkspaceMutationRejection
{
    InvalidRelativePath,
    PathEscapesWorkspace,
    SymbolicLink,
    NotRegularFile,
    MetadataUnavailable,
    HardLinkedFile,
    ConcurrentModification,
}

internal sealed class WorkspaceMutationRejectedException(
    WorkspaceMutationRejection rejection,
    string message,
    Exception? innerException = null)
    : IOException(message, innerException)
{

    internal WorkspaceMutationRejection Rejection { get; } = rejection;

}

/// <summary>
/// Content and identity state captured through an open handle. Equality intentionally includes
/// content, identity, link count, length, mtime, and promised Unix mode metadata.
/// </summary>
internal readonly record struct WorkspaceFileFingerprint(
    bool Exists,
    FileHandleIdentity Identity,
    ulong HardLinkCount,
    long Length,
    long LastWriteTimeUtcTicks,
    string ContentSha256,
    UnixFileMode? UnixMode,
    string? MissingParentRelativePath,
    FileHandleIdentity? MissingParentIdentity)
{

    internal static WorkspaceFileFingerprint Missing { get; } =
        new(
            Exists: false,
            Identity: default,
            HardLinkCount: 0,
            Length: 0,
            LastWriteTimeUtcTicks: 0,
            ContentSha256: string.Empty,
            UnixMode: null,
            MissingParentRelativePath: null,
            MissingParentIdentity: null);

}

internal readonly record struct WorkspaceMutationRead(
    byte[] Bytes,
    WorkspaceFileFingerprint Fingerprint);

internal sealed class WorkspaceMutationReadLimitExceededException(
    long observedLength,
    long maximumLength)
    : IOException(
        $"The mutation source length {observedLength} exceeds the {maximumLength} byte read limit.")
{
    internal long ObservedLength { get; } = observedLength;

    internal long MaximumLength { get; } = maximumLength;
}

internal static class WorkspaceFileFingerprintService
{

    internal static async Task<WorkspaceFileFingerprint> CaptureForMutationAsync(
        string workspaceRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {

        (string root, string absolutePath, _) = Resolve(workspaceRoot, relativePath);

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                root,
                absolutePath,
                out string? resolvedFinalPath))
        {

            throw Rejected(
                WorkspaceMutationRejection.PathEscapesWorkspace,
                "The mutation path leaves the workspace.");

        }

        if (resolvedFinalPath is not null
            && !string.Equals(
                absolutePath,
                Path.GetFullPath(resolvedFinalPath),
                WorkspaceRelativePath.Comparison))
        {

            throw Rejected(
                WorkspaceMutationRejection.SymbolicLink,
                "Existing symbolic-link mutation targets are not supported.");

        }

        RejectSymlinkComponents(root, absolutePath);

        if (Directory.Exists(absolutePath))
        {

            throw Rejected(
                WorkspaceMutationRejection.NotRegularFile,
                "The mutation target is a directory, not a regular file.");

        }

        if (!File.Exists(absolutePath))
        {

            if (!WorkspacePathPolicy.RevalidatePathBeforeIo(root, absolutePath))
            {

                throw Rejected(
                    WorkspaceMutationRejection.PathEscapesWorkspace,
                    "The mutation path failed containment revalidation.");

            }

            if (File.Exists(absolutePath) || Directory.Exists(absolutePath))
            {

                throw Rejected(
                    WorkspaceMutationRejection.ConcurrentModification,
                    "The mutation target appeared while its missing state was being captured.");

            }

            return CaptureMissingFingerprint(root, absolutePath);

        }

        if (!FileHandleIdentityInterop.TryGetPathMetadata(
                absolutePath,
                out FileHandleMetadata pathMetadata)
            || pathMetadata.HardLinkCount == 0
            || pathMetadata.Kind != FileSystemObjectKind.RegularFile)
        {

            throw Rejected(
                WorkspaceMutationRejection.MetadataUnavailable,
                "File identity and hard-link metadata could not be obtained.");

        }

        RejectMultipleLinks(pathMetadata);

        DateTime beforeMtime;

        UnixFileMode? unixMode;

        try
        {

            beforeMtime = File.GetLastWriteTimeUtc(absolutePath);

            unixMode = OperatingSystem.IsWindows()
                ? null
                : File.GetUnixFileMode(absolutePath);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {

            throw Rejected(
                WorkspaceMutationRejection.MetadataUnavailable,
                "Promised file metadata could not be captured.",
                exception);

        }

        await using FileStream stream = OpenForFingerprint(absolutePath);

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                stream.SafeFileHandle,
                out FileHandleMetadata openedMetadata)
            || openedMetadata.HardLinkCount == 0
            || openedMetadata.Kind != FileSystemObjectKind.RegularFile)
        {

            throw Rejected(
                WorkspaceMutationRejection.MetadataUnavailable,
                "Opened file identity and hard-link metadata could not be obtained.");

        }

        RejectMultipleLinks(openedMetadata);

        if (!FileHandleIdentity.IdentitiesMatch(
                pathMetadata.Identity,
                openedMetadata.Identity))
        {

            throw Rejected(
                WorkspaceMutationRejection.ConcurrentModification,
                "The file identity changed while opening it.");

        }

        long length = stream.Length;

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                stream.SafeFileHandle,
                out FileHandleMetadata finalHandleMetadata)
            || !FileHandleIdentityInterop.TryGetPathMetadata(
                absolutePath,
                out FileHandleMetadata finalPathMetadata))
        {

            throw Rejected(
                WorkspaceMutationRejection.MetadataUnavailable,
                "File metadata could not be revalidated after hashing.");

        }

        DateTime afterMtime = File.GetLastWriteTimeUtc(absolutePath);

        bool changed =
            finalHandleMetadata.HardLinkCount != 1
            || finalPathMetadata.HardLinkCount != 1
            || finalHandleMetadata.Kind != FileSystemObjectKind.RegularFile
            || finalPathMetadata.Kind != FileSystemObjectKind.RegularFile
            || stream.Length != length
            || beforeMtime != afterMtime
            || !FileHandleIdentity.IdentitiesMatch(
                pathMetadata.Identity,
                finalHandleMetadata.Identity)
            || !FileHandleIdentity.IdentitiesMatch(
                pathMetadata.Identity,
                finalPathMetadata.Identity);

        if (changed)
        {

            throw Rejected(
                WorkspaceMutationRejection.ConcurrentModification,
                "The file changed while its mutation fingerprint was being captured.");

        }

        return new WorkspaceFileFingerprint(
            Exists: true,
            pathMetadata.Identity,
            HardLinkCount: 1,
            Length: length,
            LastWriteTimeUtcTicks: beforeMtime.Ticks,
            ContentSha256: Convert.ToHexString(hash),
            UnixMode: unixMode,
            MissingParentRelativePath: null,
            MissingParentIdentity: null);

    }

    internal static async Task<bool> MatchesCurrentAsync(
        string workspaceRoot,
        string relativePath,
        WorkspaceFileFingerprint expected,
        CancellationToken cancellationToken)
    {

        WorkspaceFileFingerprint current = await CaptureForMutationAsync(
            workspaceRoot,
            relativePath,
            cancellationToken).ConfigureAwait(false);

        if (expected.Exists || current.Exists)
        {

            return current == expected;

        }

        return MatchesMissingParent(workspaceRoot, expected);

    }

    internal static async Task<WorkspaceMutationRead> ReadForMutationAsync(
        string workspaceRoot,
        string relativePath,
        CancellationToken cancellationToken,
        Action? afterHandleOpened = null,
        long maxBytes = long.MaxValue)
    {

        (string root, string absolutePath, _) = Resolve(
            workspaceRoot,
            relativePath);

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                root,
                absolutePath,
                out string? resolvedFinalPath))
        {
            throw Rejected(
                WorkspaceMutationRejection.PathEscapesWorkspace,
                "The mutation path leaves the workspace.");
        }

        if (resolvedFinalPath is not null
            && !string.Equals(
                absolutePath,
                Path.GetFullPath(resolvedFinalPath),
                WorkspaceRelativePath.Comparison))
        {
            throw Rejected(
                WorkspaceMutationRejection.SymbolicLink,
                "Existing symbolic-link mutation targets are not supported.");
        }

        RejectSymlinkComponents(root, absolutePath);

        if (Directory.Exists(absolutePath))
        {
            throw Rejected(
                WorkspaceMutationRejection.NotRegularFile,
                "The mutation target is a directory, not a regular file.");
        }

        if (!File.Exists(absolutePath))
        {
            return new WorkspaceMutationRead(
                [],
                WorkspaceFileFingerprint.Missing);
        }

        if (!FileHandleIdentityInterop.TryGetPathMetadata(
                absolutePath,
                out FileHandleMetadata pathMetadata)
            || pathMetadata.HardLinkCount == 0
            || pathMetadata.Kind != FileSystemObjectKind.RegularFile)
        {
            throw Rejected(
                WorkspaceMutationRejection.MetadataUnavailable,
                "File identity and hard-link metadata could not be obtained.");
        }

        RejectMultipleLinks(pathMetadata);

        DateTime beforeMtime;
        UnixFileMode? unixMode;

        try
        {
            beforeMtime = File.GetLastWriteTimeUtc(absolutePath);
            unixMode = OperatingSystem.IsWindows()
                ? null
                : File.GetUnixFileMode(absolutePath);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            throw Rejected(
                WorkspaceMutationRejection.MetadataUnavailable,
                "Promised file metadata could not be captured.",
                exception);
        }

        await using FileStream stream = OpenForFingerprint(absolutePath);

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                stream.SafeFileHandle,
                out FileHandleMetadata openedMetadata)
            || openedMetadata.HardLinkCount == 0
            || openedMetadata.Kind != FileSystemObjectKind.RegularFile)
        {
            throw Rejected(
                WorkspaceMutationRejection.MetadataUnavailable,
                "Opened file identity and hard-link metadata could not be obtained.");
        }

        RejectMultipleLinks(openedMetadata);

        if (!FileHandleIdentity.IdentitiesMatch(
                pathMetadata.Identity,
                openedMetadata.Identity))
        {
            throw Rejected(
                WorkspaceMutationRejection.ConcurrentModification,
                "The file identity changed while opening it.");
        }

        afterHandleOpened?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        long streamLength = stream.Length;

        if (streamLength > maxBytes)
        {

            throw new WorkspaceMutationReadLimitExceededException(
                streamLength,
                maxBytes);

        }

        if (streamLength > int.MaxValue)
        {
            throw Rejected(
                WorkspaceMutationRejection.MetadataUnavailable,
                "The patch target is too large to read safely.");
        }

        int length = checked((int)streamLength);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(
            bytes,
            cancellationToken).ConfigureAwait(false);
        byte[] hash = SHA256.HashData(bytes);

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(root, absolutePath))
        {
            throw Rejected(
                WorkspaceMutationRejection.PathEscapesWorkspace,
                "The mutation path failed containment revalidation.");
        }

        RejectSymlinkComponents(root, absolutePath);

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                stream.SafeFileHandle,
                out FileHandleMetadata finalHandleMetadata)
            || !FileHandleIdentityInterop.TryGetPathMetadata(
                absolutePath,
                out FileHandleMetadata finalPathMetadata))
        {
            throw Rejected(
                WorkspaceMutationRejection.MetadataUnavailable,
                "File metadata could not be revalidated after reading.");
        }

        DateTime afterMtime = File.GetLastWriteTimeUtc(absolutePath);

        bool changed =
            finalHandleMetadata.HardLinkCount != 1
            || finalPathMetadata.HardLinkCount != 1
            || finalHandleMetadata.Kind != FileSystemObjectKind.RegularFile
            || finalPathMetadata.Kind != FileSystemObjectKind.RegularFile
            || stream.Length != length
            || beforeMtime != afterMtime
            || !FileHandleIdentity.IdentitiesMatch(
                pathMetadata.Identity,
                finalHandleMetadata.Identity)
            || !FileHandleIdentity.IdentitiesMatch(
                pathMetadata.Identity,
                finalPathMetadata.Identity);

        if (changed)
        {
            throw Rejected(
                WorkspaceMutationRejection.ConcurrentModification,
                "The file changed while it was being read for mutation.");
        }

        return new WorkspaceMutationRead(
            bytes,
            new WorkspaceFileFingerprint(
                Exists: true,
                pathMetadata.Identity,
                HardLinkCount: 1,
                Length: length,
                LastWriteTimeUtcTicks: beforeMtime.Ticks,
                ContentSha256: Convert.ToHexString(hash),
                UnixMode: unixMode,
                MissingParentRelativePath: null,
                MissingParentIdentity: null));

    }

    internal static bool TryGetDirectoryIdentity(
        string absolutePath,
        out FileHandleIdentity identity)
    {

        identity = default;

        return Directory.Exists(absolutePath)
            && FileHandleIdentityInterop.TryGetPathMetadata(
                absolutePath,
                out FileHandleMetadata metadata)
            && metadata.Kind == FileSystemObjectKind.Directory
            && AssignIdentity(metadata.Identity, out identity);

    }

    internal static bool MatchesMissingParent(
        string workspaceRoot,
        WorkspaceFileFingerprint expected)
    {

        if (expected.MissingParentRelativePath is null
            || expected.MissingParentIdentity is not FileHandleIdentity parentIdentity)
        {

            return true;

        }

        string root = Path.GetFullPath(workspaceRoot);

        string parentPath = expected.MissingParentRelativePath.Length == 0
            ? root
            : Path.GetFullPath(
                Path.Combine(
                    root,
                    expected.MissingParentRelativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));

        return WorkspacePathPolicy.RevalidatePathBeforeIo(root, parentPath)
            && TryGetDirectoryIdentity(parentPath, out FileHandleIdentity currentParent)
            && FileHandleIdentity.IdentitiesMatch(parentIdentity, currentParent);

    }

    private static WorkspaceFileFingerprint CaptureMissingFingerprint(
        string workspaceRoot,
        string absolutePath)
    {

        string? parent = Path.GetDirectoryName(absolutePath);

        while (!string.IsNullOrEmpty(parent))
        {

            if (Directory.Exists(parent))
            {

                if (!WorkspacePathPolicy.RevalidatePathBeforeIo(
                        workspaceRoot,
                        parent)
                    || !TryGetDirectoryIdentity(
                        parent,
                        out FileHandleIdentity identity))
                {

                    throw Rejected(
                        WorkspaceMutationRejection.MetadataUnavailable,
                        "The nearest existing parent identity could not be captured.");

                }

                string relative = string.Equals(
                    parent,
                    workspaceRoot,
                    WorkspaceRelativePath.Comparison)
                        ? string.Empty
                        : WorkspaceRelativePath.FromAbsolute(
                            workspaceRoot,
                            parent);

                return WorkspaceFileFingerprint.Missing with
                {
                    MissingParentRelativePath = relative,
                    MissingParentIdentity = identity,
                };

            }

            if (File.Exists(parent)
                || string.Equals(
                    parent,
                    workspaceRoot,
                    WorkspaceRelativePath.Comparison))
            {

                throw Rejected(
                    WorkspaceMutationRejection.NotRegularFile,
                    "A missing mutation target has a non-directory parent.");

            }

            parent = Path.GetDirectoryName(parent);

        }

        throw Rejected(
            WorkspaceMutationRejection.MetadataUnavailable,
            "A missing mutation target has no verifiable existing parent.");

    }

    internal static (string Root, string AbsolutePath, string RelativePath) Resolve(
        string workspaceRoot,
        string relativePath)
    {

        string root = Path.GetFullPath(workspaceRoot);

        if (!WorkspaceRelativePath.TryResolve(
                root,
                relativePath,
                out string? absolutePath,
                out string? normalized))
        {

            throw Rejected(
                WorkspaceMutationRejection.InvalidRelativePath,
                "The mutation path must be a normalized workspace-relative path.");

        }

        return (root, absolutePath, normalized);

    }

    private static FileStream OpenForFingerprint(string absolutePath)
    {

        try
        {

            return new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or DirectoryNotFoundException
                or UnauthorizedAccessException
                or IOException)
        {

            throw Rejected(
                WorkspaceMutationRejection.ConcurrentModification,
                "The file could not be opened after its path metadata was captured.",
                exception);

        }

    }

    private static void RejectMultipleLinks(FileHandleMetadata metadata)
    {

        if (metadata.HardLinkCount > 1)
        {

            throw Rejected(
                WorkspaceMutationRejection.HardLinkedFile,
                "Mutation of files with multiple hard links is rejected.");

        }

    }

    private static void RejectSymlinkComponents(
        string workspaceRoot,
        string absolutePath)
    {

        string relative = Path.GetRelativePath(workspaceRoot, absolutePath);

        string current = workspaceRoot;

        foreach (string segment in relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {

            current = Path.Combine(current, segment);

            if (!File.Exists(current) && !Directory.Exists(current))
            {

                continue;

            }

            try
            {

                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {

                    throw Rejected(
                        WorkspaceMutationRejection.SymbolicLink,
                        "Mutation through symbolic-link path components is rejected.");

                }

            }
            catch (WorkspaceMutationRejectedException)
            {

                throw;

            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException)
            {

                throw Rejected(
                    WorkspaceMutationRejection.MetadataUnavailable,
                    "A mutation path component could not be revalidated.",
                    exception);

            }

        }

    }

    private static bool AssignIdentity(
        FileHandleIdentity value,
        out FileHandleIdentity identity)
    {

        identity = value;

        return true;

    }

    private static WorkspaceMutationRejectedException Rejected(
        WorkspaceMutationRejection rejection,
        string message,
        Exception? exception = null) =>
        new(rejection, message, exception);

}
