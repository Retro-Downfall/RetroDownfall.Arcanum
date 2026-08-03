using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed class OwnedTemporaryDirectory
{

    private readonly IdentityOwnedFileSystemArtifact _artifact;

    private OwnedTemporaryDirectory(
        string path,
        IdentityOwnedFileSystemArtifact artifact)
    {

        Path = path;

        _artifact = artifact;

    }

    public string Path { get; }

    internal ulong VolumeId => _artifact.Metadata.Identity.VolumeId;

    internal ulong FileId => _artifact.Metadata.Identity.FileId;

    public static OwnedTemporaryDirectory Create(string path)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = System.IO.Path.GetFullPath(path);

        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {

            throw new IOException("The protected temporary directory path already exists.");

        }

        string parent = System.IO.Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                "The protected temporary directory has no parent directory.");

        Directory.CreateDirectory(parent);

        SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath(fullPath);

        SecureFilePermissions.ApplyOwnerOnlyDirectory(fullPath);

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                fullPath,
                FileSystemObjectKind.Directory,
                out IdentityOwnedFileSystemArtifact artifact))
        {

            throw new IOException(
                "The protected temporary directory identity could not be established.");

        }

        return new OwnedTemporaryDirectory(fullPath, artifact);

    }

    public bool TryDelete() =>
        IdentityOwnedFileSystemCleanup.TryDelete(_artifact);

    internal static bool TryDelete(
        string path,
        ulong volumeId,
        ulong fileId)
    {

        string fullPath;

        try
        {

            fullPath = System.IO.Path.GetFullPath(path);

        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return false;

        }

        IdentityOwnedFileSystemArtifact artifact = new(
            fullPath,
            new FileHandleMetadata(
                new FileHandleIdentity(volumeId, fileId),
                HardLinkCount: 1,
                FileSystemObjectKind.Directory));

        return IdentityOwnedFileSystemCleanup.TryDelete(artifact);

    }

}
