using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal enum EncryptedBlobPresence : byte
{
    Absent,
    Present,
    Indeterminate,
}

internal interface IEncryptedBlobPresenceInspector
{
    EncryptedBlobPresence Inspect(CancellationToken cancellationToken = default);
}

internal sealed class EncryptedBlobPresenceInspector(
    string attachmentsDirectory,
    string filesDirectory) : IEncryptedBlobPresenceInspector
{
    private static ReadOnlySpan<byte> EnvelopeMagic => "ARCABLOB"u8;

    public EncryptedBlobPresenceInspector()
        : this(ArcanumPaths.AttachmentsDirectory, ArcanumPaths.FilesDirectory)
    {
    }

    public EncryptedBlobPresence Inspect(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EncryptedBlobPresence attachments = InspectRoot(
            attachmentsDirectory,
            recurse: true,
            cancellationToken);
        if (attachments is EncryptedBlobPresence.Present)
        {
            return EncryptedBlobPresence.Present;
        }

        EncryptedBlobPresence files = InspectRoot(
            filesDirectory,
            recurse: false,
            cancellationToken);
        if (files is EncryptedBlobPresence.Present)
        {
            return EncryptedBlobPresence.Present;
        }

        return attachments is EncryptedBlobPresence.Indeterminate
            || files is EncryptedBlobPresence.Indeterminate
            ? EncryptedBlobPresence.Indeterminate
            : EncryptedBlobPresence.Absent;
    }

    private static EncryptedBlobPresence InspectRoot(
        string path,
        bool recurse,
        CancellationToken cancellationToken)
    {
        RootDirectoryOpenState openState = TryOpenRoot(
            path,
            cancellationToken,
            out SafeFileHandle? directory,
            out FileHandleIdentity openedIdentity);
        if (openState is RootDirectoryOpenState.Absent)
        {
            return EncryptedBlobPresence.Absent;
        }

        if (openState is not RootDirectoryOpenState.Open || directory is null)
        {
            return EncryptedBlobPresence.Indeterminate;
        }

        using (directory)
        {
            EncryptedBlobPresence result = InspectDirectory(
                directory,
                recurse,
                cancellationToken);
            if (result is EncryptedBlobPresence.Present)
            {
                return result;
            }

            RootDirectoryOpenState revalidation = TryOpenRoot(
                path,
                cancellationToken,
                out SafeFileHandle? current,
                out FileHandleIdentity currentIdentity);
            using (current)
            {
                return revalidation is RootDirectoryOpenState.Open
                    && current is not null
                    && FileHandleIdentity.IdentitiesMatch(
                        openedIdentity,
                        currentIdentity)
                    ? result
                    : EncryptedBlobPresence.Indeterminate;
            }
        }
    }

    private static RootDirectoryOpenState TryOpenRoot(
        string path,
        CancellationToken cancellationToken,
        out SafeFileHandle? directory,
        out FileHandleIdentity identity)
    {
        directory = null;
        identity = default;

        string fullPath;
        try
        {
            fullPath = NoFollowPathTopology.NormalizeMacOsSystemAlias(
                Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException)
        {
            return RootDirectoryOpenState.Indeterminate;
        }

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return RootDirectoryOpenState.Indeterminate;
        }

        string[] components = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                root,
                requestEnumeration: components.Length == 0,
                out SafeFileHandle openedRoot,
                out FileHandleMetadata currentMetadata))
        {
            openedRoot.Dispose();

            return RootDirectoryOpenState.Indeterminate;
        }

        SafeFileHandle? current = openedRoot;
        try
        {
            for (int index = 0; index < components.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool final = index == components.Length - 1;
                if (FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(
                        current!,
                        components[index],
                        requestReadControl: false,
                        requestEnumeration: final,
                        out SafeFileHandle child,
                        out FileHandleMetadata childMetadata))
                {
                    current.Dispose();
                    current = child;
                    currentMetadata = childMetadata;

                    continue;
                }

                child.Dispose();
                SecureFileOpenStatus status =
                    FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(
                        current!,
                        components[index],
                        out SafeFileHandle? nonDirectory);
                nonDirectory?.Dispose();

                return status is SecureFileOpenStatus.NotFound
                    ? RootDirectoryOpenState.Absent
                    : RootDirectoryOpenState.Indeterminate;
            }

            directory = current;
            identity = currentMetadata.Identity;
            current = null;

            return RootDirectoryOpenState.Open;
        }
        finally
        {
            current?.Dispose();
        }
    }

    private static EncryptedBlobPresence InspectDirectory(
        SafeFileHandle directory,
        bool recurse,
        CancellationToken cancellationToken)
    {
        if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                directory,
                out FileHandleMetadata root)
            || root.Kind is not FileSystemObjectKind.Directory)
        {
            return EncryptedBlobPresence.Indeterminate;
        }

        HashSet<FileHandleIdentity> visited = [root.Identity];
        Stack<PendingDirectory> pending = [];
        pending.Push(new PendingDirectory(directory, OwnsHandle: false, root.Identity));
        bool indeterminate = false;

        try
        {
            while (pending.TryPop(out PendingDirectory current))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                            current.Handle,
                            out FileHandleMetadata currentMetadata)
                        || currentMetadata.Kind is not FileSystemObjectKind.Directory
                        || !FileHandleIdentity.IdentitiesMatch(
                            current.Identity,
                            currentMetadata.Identity)
                        || !SecureDirectoryNameEnumerator.TryEnumerate(
                            current.Handle,
                            cancellationToken,
                            out string[] names))
                    {
                        indeterminate = true;
                        continue;
                    }

                    foreach (string name in names)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        SecureFileOpenStatus fileStatus =
                            FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(
                                current.Handle,
                                name,
                                out SafeFileHandle? file);
                        using (file)
                        {
                            if (fileStatus is SecureFileOpenStatus.Success && file is not null)
                            {
                                EncryptedBlobPresence candidate = InspectFile(file);
                                if (candidate is EncryptedBlobPresence.Present)
                                {
                                    return candidate;
                                }

                                indeterminate |=
                                    candidate is EncryptedBlobPresence.Indeterminate;
                                continue;
                            }
                        }

                        if (fileStatus is not SecureFileOpenStatus.Rejected
                            || !FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(
                                current.Handle,
                                name,
                                requestReadControl: false,
                                requestEnumeration: true,
                                out SafeFileHandle childDirectory,
                                out FileHandleMetadata childMetadata))
                        {
                            indeterminate = true;
                            continue;
                        }

                        if (!recurse)
                        {
                            childDirectory.Dispose();
                            continue;
                        }

                        if (!visited.Add(childMetadata.Identity))
                        {
                            childDirectory.Dispose();
                            indeterminate = true;
                            continue;
                        }

                        pending.Push(
                            new PendingDirectory(
                                childDirectory,
                                OwnsHandle: true,
                                childMetadata.Identity));
                    }
                }
                finally
                {
                    if (current.OwnsHandle)
                    {
                        current.Handle.Dispose();
                    }
                }
            }
        }
        finally
        {
            while (pending.TryPop(out PendingDirectory remaining))
            {
                if (remaining.OwnsHandle)
                {
                    remaining.Handle.Dispose();
                }
            }
        }

        return indeterminate
            ? EncryptedBlobPresence.Indeterminate
            : EncryptedBlobPresence.Absent;
    }

    private static EncryptedBlobPresence InspectFile(SafeFileHandle file)
    {
        try
        {
            if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                    file,
                    out FileHandleMetadata before)
                || before.Kind is not FileSystemObjectKind.RegularFile)
            {
                return EncryptedBlobPresence.Indeterminate;
            }

            Span<byte> magic = stackalloc byte[8];
            int read = RandomAccess.Read(file, magic, fileOffset: 0);

            if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                    file,
                    out FileHandleMetadata after)
                || after.Kind is not FileSystemObjectKind.RegularFile
                || !FileHandleIdentity.IdentitiesMatch(
                    before.Identity,
                    after.Identity))
            {
                return EncryptedBlobPresence.Indeterminate;
            }

            return read == magic.Length
                && CryptographicOperations.FixedTimeEquals(magic, EnvelopeMagic)
                ? EncryptedBlobPresence.Present
                : EncryptedBlobPresence.Absent;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or ObjectDisposedException)
        {
            return EncryptedBlobPresence.Indeterminate;
        }
    }

    private readonly record struct PendingDirectory(
        SafeFileHandle Handle,
        bool OwnsHandle,
        FileHandleIdentity Identity);

    private enum RootDirectoryOpenState : byte
    {
        Absent,
        Open,
        Indeterminate,
    }
}
