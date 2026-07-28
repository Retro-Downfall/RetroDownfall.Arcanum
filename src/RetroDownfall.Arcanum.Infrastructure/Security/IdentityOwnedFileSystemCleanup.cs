using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// A temporary filesystem object whose no-follow identity, kind, and link
/// metadata were captured while it was owned by the creating operation.
/// </summary>
internal readonly record struct IdentityOwnedFileSystemArtifact(
    string Path,
    FileHandleMetadata Metadata);

/// <summary>
/// Best-effort cleanup that refuses to delete a path unless its current
/// no-follow metadata still identifies the object originally created.
/// </summary>
internal static class IdentityOwnedFileSystemCleanup
{
    internal static bool TryCapturePath(
        string path,
        FileSystemObjectKind expectedKind,
        out IdentityOwnedFileSystemArtifact artifact)
    {
        artifact = default;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                path,
                out FileHandleMetadata metadata))
        {
            return false;
        }

        if (!IsOwnedKind(metadata, expectedKind))
        {
            return false;
        }

        artifact = new IdentityOwnedFileSystemArtifact(
            Path.GetFullPath(path),
            metadata);

        return true;
    }

    internal static bool TryCaptureOpenFile(
        string path,
        SafeFileHandle handle,
        out IdentityOwnedFileSystemArtifact artifact)
    {
        artifact = default;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                handle,
                out FileHandleMetadata metadata))
        {
            return false;
        }

        if (!IsOwnedKind(
                metadata,
                FileSystemObjectKind.RegularFile))
        {
            return false;
        }

        artifact = new IdentityOwnedFileSystemArtifact(
            Path.GetFullPath(path),
            metadata);

        return true;
    }

    internal static bool TryDelete(
        IdentityOwnedFileSystemArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.Path))
        {
            return false;
        }

        if (artifact.Metadata.Kind
                is not FileSystemObjectKind.RegularFile
                and not FileSystemObjectKind.Directory)
        {
            return false;
        }

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                artifact.Path,
                out FileHandleMetadata current))
        {
            return !File.Exists(artifact.Path)
                && !Directory.Exists(artifact.Path);
        }

        if (!MatchesOwnedArtifact(
                current,
                artifact.Metadata))
        {
            return false;
        }

        string? parentPath = Path.GetDirectoryName(
            Path.GetFullPath(artifact.Path));

        if (parentPath is null)
        {
            return false;
        }

        if (!TryCreatePrivateQuarantineDirectory(
                parentPath,
                out IdentityOwnedFileSystemArtifact
                    quarantineDirectory))
        {
            return false;
        }

        string quarantinePath = Path.Combine(
            quarantineDirectory.Path,
            "artifact");

        try
        {
            MoveWithoutOverwrite(
                artifact.Path,
                quarantinePath,
                artifact.Metadata.Kind);
        }
        catch (IOException)
        {
            _ = TryDeleteEmptyOwnedDirectory(
                quarantineDirectory);

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            _ = TryDeleteEmptyOwnedDirectory(
                quarantineDirectory);

            return false;
        }

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                quarantinePath,
                out FileHandleMetadata quarantined))
        {
            return false;
        }

        if (!MatchesOwnedArtifact(
                quarantined,
                artifact.Metadata))
        {
            return false;
        }

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                quarantinePath,
                out quarantined)
            || !MatchesOwnedArtifact(
                quarantined,
                artifact.Metadata))
        {
            return false;
        }

        try
        {
            if (quarantined.Kind
                == FileSystemObjectKind.RegularFile)
            {
                File.Delete(quarantinePath);
            }
            else
            {
                Directory.Delete(
                    quarantinePath,
                    recursive: true);
            }

            if (FileHandleIdentityInterop
                .TryGetPathMetadataNoFollow(
                    quarantinePath,
                    out _))
            {
                return false;
            }

            return TryDeleteEmptyOwnedDirectory(
                quarantineDirectory);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool MatchesOwnedArtifact(
        FileHandleMetadata current,
        FileHandleMetadata owned) =>
        current.Identity == owned.Identity
        && current.Kind == owned.Kind
        && (owned.Kind != FileSystemObjectKind.RegularFile
            || (current.HardLinkCount == 1
                && owned.HardLinkCount == 1));

    private static void MoveWithoutOverwrite(
        string sourcePath,
        string destinationPath,
        FileSystemObjectKind kind)
    {
        if (kind == FileSystemObjectKind.Directory)
        {
            Directory.Move(
                sourcePath,
                destinationPath);

            return;
        }

        File.Move(
            sourcePath,
            destinationPath);
    }

    private static bool TryCreatePrivateQuarantineDirectory(
        string parentPath,
        out IdentityOwnedFileSystemArtifact artifact)
    {
        artifact = default;

        string quarantinePath = Path.Combine(
            parentPath,
            $".arcanum-cleanup-{Guid.NewGuid():N}");

        try
        {
            SecureFilePermissions
                .CreateOwnerOnlyDirectoryAtPath(
                    quarantinePath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (!TryCapturePath(
                quarantinePath,
                FileSystemObjectKind.Directory,
                out artifact))
        {
            return false;
        }

        if (!SecureFilePermissions
                .TryEnsureOwnerOnlyDirectoryExistsStrict(
                    quarantinePath)
            || !FileHandleIdentityInterop
                .TryGetPathMetadataNoFollow(
                    quarantinePath,
                    out FileHandleMetadata current)
            || !MatchesOwnedArtifact(
                current,
                artifact.Metadata))
        {
            _ = TryDeleteEmptyOwnedDirectory(artifact);

            return false;
        }

        return true;
    }

    private static bool TryDeleteEmptyOwnedDirectory(
        IdentityOwnedFileSystemArtifact artifact)
    {
        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                artifact.Path,
                out FileHandleMetadata current))
        {
            return !Directory.Exists(artifact.Path);
        }

        if (!MatchesOwnedArtifact(
                current,
                artifact.Metadata))
        {
            return false;
        }

        try
        {
            Directory.Delete(
                artifact.Path,
                recursive: false);

            return !FileHandleIdentityInterop
                .TryGetPathMetadataNoFollow(
                    artifact.Path,
                    out _);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsOwnedKind(
        FileHandleMetadata metadata,
        FileSystemObjectKind expectedKind) =>
        metadata.Kind == expectedKind
        && metadata.HardLinkCount > 0
        && (expectedKind != FileSystemObjectKind.RegularFile
            || metadata.HardLinkCount == 1);
}
