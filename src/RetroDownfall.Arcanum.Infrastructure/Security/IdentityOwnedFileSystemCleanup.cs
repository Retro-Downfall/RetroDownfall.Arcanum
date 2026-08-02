using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// A temporary filesystem object whose no-follow identity, kind, and link
/// metadata were captured while it was owned by the creating operation.
/// </summary>
internal readonly record struct IdentityOwnedFileSystemArtifact(
    string Path,
    FileHandleMetadata Metadata);

internal readonly record struct IdentityOwnedFileSystemQuarantine(
    IdentityOwnedFileSystemArtifact Original,
    IdentityOwnedFileSystemArtifact Quarantined,
    IdentityOwnedFileSystemArtifact Directory);

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
        if (!TryQuarantine(
                artifact,
                out IdentityOwnedFileSystemQuarantine quarantine))
        {
            return false;
        }

        return quarantine == default
            || TryDeleteQuarantined(quarantine);
    }

    internal static bool TryQuarantine(
        IdentityOwnedFileSystemArtifact artifact,
        out IdentityOwnedFileSystemQuarantine quarantine) =>
        TryQuarantine(
            artifact,
            ".arcanum-cleanup-",
            out quarantine);

    internal static bool TryQuarantine(
        IdentityOwnedFileSystemArtifact artifact,
        string quarantineDirectoryPrefix,
        out IdentityOwnedFileSystemQuarantine quarantine)
    {
        quarantine = default;

        if (string.IsNullOrWhiteSpace(artifact.Path))
        {
            return false;
        }

        if (!IsSafeQuarantineDirectoryPrefix(quarantineDirectoryPrefix))
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
                quarantineDirectoryPrefix,
                out IdentityOwnedFileSystemArtifact
                    quarantineDirectory))
        {
            return false;
        }

        string quarantinePath = Path.Combine(
            quarantineDirectory.Path,
            Path.GetFileName(artifact.Path));

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

        quarantine = new IdentityOwnedFileSystemQuarantine(
            artifact,
            new IdentityOwnedFileSystemArtifact(
                quarantinePath,
                artifact.Metadata),
            quarantineDirectory);

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                quarantinePath,
                out FileHandleMetadata quarantined))
        {
            if (TryRestoreQuarantined(quarantine))
            {
                quarantine = default;
            }

            return false;
        }

        if (!MatchesOwnedArtifact(
                quarantined,
                artifact.Metadata))
        {
            if (TryRestoreQuarantined(quarantine))
            {
                quarantine = default;
            }

            return false;
        }

        quarantine = new IdentityOwnedFileSystemQuarantine(
            artifact,
            new IdentityOwnedFileSystemArtifact(
                quarantinePath,
                quarantined),
            quarantineDirectory);

        return true;
    }

    internal static bool TryDeleteQuarantined(
        IdentityOwnedFileSystemQuarantine quarantine)
    {
        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                quarantine.Quarantined.Path,
                out FileHandleMetadata quarantined)
            || !MatchesOwnedArtifact(
                quarantined,
                quarantine.Quarantined.Metadata))
        {
            return false;
        }

        try
        {
            if (quarantined.Kind
                == FileSystemObjectKind.RegularFile)
            {
                File.Delete(quarantine.Quarantined.Path);
            }
            else
            {
                Directory.Delete(
                    quarantine.Quarantined.Path,
                    recursive: true);
            }

            if (FileHandleIdentityInterop
                .TryGetPathMetadataNoFollow(
                    quarantine.Quarantined.Path,
                    out _))
            {
                return false;
            }

            _ = TryDeleteEmptyOwnedDirectory(
                quarantine.Directory);

            return true;
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

    internal static bool TryRestoreQuarantined(
        IdentityOwnedFileSystemQuarantine quarantine)
    {
        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                quarantine.Quarantined.Path,
                out FileHandleMetadata quarantined)
            || !MatchesOwnedArtifact(
                quarantined,
                quarantine.Quarantined.Metadata))
        {
            return false;
        }

        if (FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                quarantine.Original.Path,
                out _)
            || File.Exists(quarantine.Original.Path)
            || Directory.Exists(quarantine.Original.Path))
        {
            return false;
        }

        try
        {
            MoveWithoutOverwrite(
                quarantine.Quarantined.Path,
                quarantine.Original.Path,
                quarantined.Kind);

            if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                    quarantine.Original.Path,
                    out FileHandleMetadata restored)
                || !MatchesOwnedArtifact(
                    restored,
                    quarantine.Original.Metadata))
            {
                return false;
            }

            return TryDeleteEmptyOwnedDirectory(
                quarantine.Directory);
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
        string quarantineDirectoryPrefix,
        out IdentityOwnedFileSystemArtifact artifact)
    {
        artifact = default;

        string quarantinePath = Path.Combine(
            parentPath,
            quarantineDirectoryPrefix + Guid.NewGuid().ToString("N"));

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

    private static bool IsSafeQuarantineDirectoryPrefix(string prefix) =>
        prefix.StartsWith(".arcanum-", StringComparison.Ordinal)
        && prefix.EndsWith("-", StringComparison.Ordinal)
        && string.Equals(prefix, Path.GetFileName(prefix), StringComparison.Ordinal)
        && prefix.All(static character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-');

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
