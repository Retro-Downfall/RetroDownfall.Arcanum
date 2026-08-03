using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed class OwnedTemporaryFile
{

    private static readonly UnixFileMode OwnerOnlyFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly IdentityOwnedFileSystemArtifact _artifact;

    private OwnedTemporaryFile(
        IdentityOwnedFileSystemArtifact artifact)
    {

        _artifact = artifact;

    }

    public string Path => _artifact.Path;

    public static OwnedTemporaryFile Create(
        string path,
        out FileStream stream)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = System.IO.Path.GetFullPath(path);

        FileStream? created = null;

        IdentityOwnedFileSystemArtifact handleArtifact = default;

        bool handleCaptured = false;

        stream = null!;

        try
        {

            FileStreamOptions options = new()
            {

                Mode = FileMode.CreateNew,

                Access = FileAccess.Write,

                Share = FileShare.None,

                BufferSize = 4096,

                Options = FileOptions.Asynchronous,

            };

            if (!OperatingSystem.IsWindows())
            {

                options.UnixCreateMode = OwnerOnlyFileMode;

            }

            created = new FileStream(fullPath, options);

            if (!IdentityOwnedFileSystemCleanup.TryCaptureOpenFile(
                    fullPath,
                    created.SafeFileHandle,
                    out handleArtifact))
            {

                throw new IOException(
                    "The protected temporary file identity could not be established.");

            }

            handleCaptured = true;

            if (!SecureFilePermissions.TryApplyOwnerOnlyFileStrict(fullPath))
            {

                throw new IOException(
                    "Owner-only permissions could not be applied and verified for the protected temporary file.");

            }

            if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                    fullPath,
                    FileSystemObjectKind.RegularFile,
                    out IdentityOwnedFileSystemArtifact pathArtifact)
                || pathArtifact.Metadata != handleArtifact.Metadata)
            {

                throw new IOException(
                    "The protected temporary file path changed during creation.");

            }

            stream = created;

            return new OwnedTemporaryFile(handleArtifact);

        }
        catch
        {

            created?.Dispose();

            if (handleCaptured)
            {

                _ = IdentityOwnedFileSystemCleanup.TryDelete(
                    handleArtifact);

            }

            throw;

        }

    }

    public bool TryDelete() => TryDeleteAtPath(Path);

    internal bool TryDeleteAtPath(string path)
    {

        if (string.IsNullOrWhiteSpace(path))
        {

            return false;

        }

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

        return IdentityOwnedFileSystemCleanup.TryDelete(
            new IdentityOwnedFileSystemArtifact(
                fullPath,
                _artifact.Metadata));

    }

    internal bool MatchesPathIdentity() => MatchesPathIdentity(Path);

    internal bool MatchesPathIdentity(string path) =>
        IdentityOwnedFileSystemCleanup.TryCapturePath(
            path,
            FileSystemObjectKind.RegularFile,
            out IdentityOwnedFileSystemArtifact current)
        && current.Metadata == _artifact.Metadata;

}
