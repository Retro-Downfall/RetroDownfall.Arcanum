using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Coordination;

internal enum RetainedExclusiveFileLockAcquisitionDisposition : byte
{

    Unsafe,

    Contended,

    Acquired,

}

internal readonly record struct RetainedExclusiveFileLockAcquisitionResult(
    RetainedExclusiveFileLockAcquisitionDisposition Disposition,
    RetainedExclusiveFileLock? Lock);

/// <summary>
/// One hardened, owner-only, retained exclusive-file lock. Installation coordination wrappers own
/// the path naming and domain-specific lifetime; this primitive owns the shared topology, permission,
/// identity, and operating-system contention proof.
/// </summary>
internal sealed class RetainedExclusiveFileLock : IDisposable
{

    private FileStream? _stream;

    private RetainedExclusiveFileLock(string path, FileStream stream)
    {

        Path = path;

        _stream = stream;

    }

    internal string Path { get; }

    internal static RetainedExclusiveFileLockAcquisitionResult Acquire(string path)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = System.IO.Path.GetFullPath(path);

        string parent = System.IO.Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "A retained lock path must have an owning directory.",
                nameof(path));

        try
        {

            Result<NoFollowPathTopologyKind> beforeCreation =
                NoFollowPathTopology.Classify(parent);

            if (beforeCreation.IsFailure
                || beforeCreation.Value is NoFollowPathTopologyKind.RegularFile)
            {

                return Unsafe();

            }

            if (beforeCreation.Value is NoFollowPathTopologyKind.Absent)
            {

                SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath(parent);

            }

            if (!SecureFilePermissions.TryEnsureOwnerOnlyDirectoryExistsStrict(
                    parent,
                    logFailure: false))
            {

                return Unsafe();

            }

            Result<NoFollowPathTopologyKind> afterCreation =
                NoFollowPathTopology.Classify(parent);

            if (afterCreation.IsFailure
                || afterCreation.Value is not NoFollowPathTopologyKind.Directory)
            {

                return Unsafe();

            }

            Result<NoFollowPathTopologyKind> lockLeaf =
                NoFollowPathTopology.Classify(fullPath);

            if (lockLeaf.IsFailure
                || lockLeaf.Value is NoFollowPathTopologyKind.Directory)
            {

                return Unsafe();

            }

            FileHandleMetadata? namedBeforeOpen = null;

            if (lockLeaf.Value is NoFollowPathTopologyKind.RegularFile)
            {

                if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                        fullPath,
                        out FileHandleMetadata named)
                    || named.Kind is not FileSystemObjectKind.RegularFile
                    || named.HardLinkCount != 1)
                {

                    return Unsafe();

                }

                namedBeforeOpen = named;

            }

            FileStream stream;

            try
            {

                stream = new FileStream(
                    fullPath,
                    new FileStreamOptions
                    {

                        Mode = FileMode.OpenOrCreate,

                        Access = FileAccess.ReadWrite,

                        Share = FileShare.None,

                        Options = FileOptions.WriteThrough,

                    });

            }
            catch (IOException exception)
            {

                return IsVerifiedSharingViolation(exception)
                    ? Contended()
                    : Unsafe();

            }
            catch (UnauthorizedAccessException)
            {

                return Unsafe();

            }

            try
            {

                if (!OpenedLeafMatchesCanonicalPath(
                        stream,
                        fullPath,
                        namedBeforeOpen)
                    || !SecureFilePermissions.TryApplyOwnerOnlyFileStrict(
                        fullPath,
                        logFailure: false))
                {

                    stream.Dispose();

                    return Unsafe();

                }

                stream.SetLength(0);

                using StreamWriter writer = new(stream, leaveOpen: true);

                writer.Write(
                    $"{System.Environment.MachineName}:{System.Environment.ProcessId}");

                writer.Flush();

                stream.Flush(flushToDisk: true);

                if (!OpenedLeafMatchesCanonicalPath(
                        stream,
                        fullPath,
                        namedBeforeOpen))
                {

                    stream.Dispose();

                    return Unsafe();

                }

                return new RetainedExclusiveFileLockAcquisitionResult(
                    RetainedExclusiveFileLockAcquisitionDisposition.Acquired,
                    new RetainedExclusiveFileLock(fullPath, stream));

            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {

                stream.Dispose();

                return Unsafe();

            }

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return Unsafe();

        }

    }

    internal static bool IsVerifiedSharingViolation(IOException exception)
    {

        if (OperatingSystem.IsWindows())
        {

            return exception.HResult == unchecked((int)0x80070020);

        }

        if (OperatingSystem.IsLinux())
        {

            return exception.HResult == 11;

        }

        if (OperatingSystem.IsMacOS())
        {

            return exception.HResult == 35;

        }

        return false;

    }

    internal void AssertHeldAt(string expectedPath, object owner)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);

        ObjectDisposedException.ThrowIf(_stream is null, owner);

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(
                System.IO.Path.GetFullPath(expectedPath),
                Path,
                comparison))
        {

            throw new InvalidOperationException(
                "The retained lock supplied for this operation guards a different control path.");

        }

    }

    public void Dispose()
    {

        FileStream? stream = _stream;

        _stream = null;

        if (stream is null)
        {

            return;

        }

        try
        {

            stream.Dispose();

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

        }

    }

    private static bool OpenedLeafMatchesCanonicalPath(
        FileStream stream,
        string path,
        FileHandleMetadata? namedBeforeOpen)
    {

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                stream.SafeFileHandle,
                out FileHandleMetadata opened)
            || opened.Kind is not FileSystemObjectKind.RegularFile
            || opened.HardLinkCount != 1
            || (namedBeforeOpen is { } expected
                && !FileHandleIdentity.IdentitiesMatch(
                    opened.Identity,
                    expected.Identity)))
        {

            return false;

        }

        if (OperatingSystem.IsWindows())
        {

            return true;

        }

        return FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                path,
                out FileHandleMetadata named)
            && named.Kind is FileSystemObjectKind.RegularFile
            && named.HardLinkCount == 1
            && FileHandleIdentity.IdentitiesMatch(
                opened.Identity,
                named.Identity);

    }

    private static RetainedExclusiveFileLockAcquisitionResult Contended() =>
        new(
            RetainedExclusiveFileLockAcquisitionDisposition.Contended,
            Lock: null);

    private static RetainedExclusiveFileLockAcquisitionResult Unsafe() =>
        new(
            RetainedExclusiveFileLockAcquisitionDisposition.Unsafe,
            Lock: null);

}
