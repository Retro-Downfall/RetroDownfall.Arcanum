using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

/// <summary>Retains the admitted root while every body and sidecar is checked on its opened handle.</summary>
internal sealed class SpellReadAuthority(SafeFileHandle root, FileHandleIdentity identity) : IDisposable
{
    internal FileHandleIdentity Identity { get; } = identity;

    internal static SpellReadAuthority? TryOpen(string? sourceRoot, FileHandleIdentity? expectedIdentity = null)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot)
            || !WorkspacePathPolicy.TryCanonicalize(Path.GetFullPath(sourceRoot), out string? canonical, out bool exists)
            || !exists
            || !FileHandleIdentityInterop.TryOpenDirectoryMetadata(canonical, out SafeFileHandle handle, out FileHandleMetadata metadata))
        {
            return null;
        }

        if (expectedIdentity is { } expected && expected != metadata.Identity)
        {
            handle.Dispose();

            return null;
        }

        return new SpellReadAuthority(handle, metadata.Identity);
    }

    internal FileStream? OpenFile(string path, FileHandleIdentity? expectedIdentity, out FileHandleMetadata metadata)
    {
        if (SecureFileReader.TryOpenRegularFile(path, expectedIdentity, out FileStream? stream, out metadata)
            is not SecureFileOpenStatus.Success || stream is null)
        {
            return null;
        }

        if (!Contains(stream))
        {
            stream.Dispose();

            return null;
        }

        return stream;
    }

    internal bool TryReadFileNames(string path, CancellationToken cancellationToken, out string[] names)
    {
        names = [];

        cancellationToken.ThrowIfCancellationRequested();

        if (!WorkspacePathPolicy.TryCanonicalize(Path.GetFullPath(path), out string? canonical, out bool exists)
            || !exists)
        {
            return false;
        }

        if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(canonical, requestEnumeration: true, out SafeFileHandle directory, out _))
        {
            directory.Dispose();

            return false;
        }

        using (directory)
        {
            if (!Contains(directory, path)
                || !SecureDirectoryNameEnumerator.TryEnumerate(directory, cancellationToken, out string[] entries))
            {
                return false;
            }

            int fileCount = 0;

            foreach (string leaf in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool isDirectory = FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(
                    directory, leaf, requestReadControl: false, out SafeFileHandle child, out _);

                child.Dispose();

                // Keep non-regular leaves so export can report omitted artifacts, but do not
                // descend into directories or resolve their contents through a pathname.
                if (!isDirectory)
                {
                    entries[fileCount++] = leaf;
                }
            }

            if (!Contains(directory, path))
            {
                return false;
            }

            if (fileCount != entries.Length)
            {
                Array.Resize(ref entries, fileCount);
            }

            names = entries;

            return true;
        }
    }

    internal bool Contains(FileStream stream) => Contains(stream.SafeFileHandle, stream.Name);

    private bool Contains(SafeFileHandle handle, string path) =>
        FileHandleIdentityInterop.TryGetHandleKernelPath(root, out string? rootPath)
        && FileHandleIdentityInterop.TryGetHandleKernelPath(handle, out string? filePath)
        && WorkspacePathPolicy.IsPathUnderWorkspace(rootPath, filePath)
        && FileHandleIdentityInterop.TryGetHandleMetadata(handle, out FileHandleMetadata opened)
        && FileHandleIdentityInterop.TryGetPathMetadata(path, out FileHandleMetadata current)
        && opened.Identity == current.Identity;

    public void Dispose() => root.Dispose();
}
