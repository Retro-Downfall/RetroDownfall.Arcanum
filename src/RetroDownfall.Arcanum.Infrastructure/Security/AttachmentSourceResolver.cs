using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal sealed class AttachmentSourceResolver(IHostWorkspaceContext workspaceContext)
    : IAttachmentSourceResolver
{
    internal Func<CancellationToken, Task>? AfterFirstRefreshReadForTesting { get; set; }

    public async Task<AttachmentSourceResolution> ResolveForPersistenceAsync(
        AttachmentSourceClaim claim,
        ReadOnlyMemory<byte> snapshotBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        string? root = NormalizeWorkspaceRoot();
        if (root is null)
        {
            return Snapshot(AttachmentSourceStatus.WorkspaceUnavailable, "No active registered workspace.");
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(claim.AbsolutePath);
        }
        catch (Exception)
        {
            return Snapshot(AttachmentSourceStatus.Unsafe, "The source path is invalid.");
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspace(root, candidate))
        {
            return Snapshot(AttachmentSourceStatus.Unsafe, "The source is outside the active workspace.");
        }

        if (!File.Exists(candidate))
        {
            string? parent = Path.GetDirectoryName(candidate);
            if (parent is null
                || !WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, parent, out _))
            {
                return Snapshot(AttachmentSourceStatus.Unsafe, "The missing source has an unsafe parent path.");
            }

            return Snapshot(AttachmentSourceStatus.Missing, "The source file is missing.");
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, candidate, out string? canonical)
            || canonical is null)
        {
            return Snapshot(AttachmentSourceStatus.Unsafe, "The source is outside the active workspace.");
        }

        try
        {
            await using FileStream stream = new(
                candidate, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (!FileHandleIdentityInterop.TryGetPathIdentity(candidate, out FileHandleIdentity expected)
                || !FileHandleIdentityInterop.TryGetHandleIdentity(stream.SafeFileHandle, out FileHandleIdentity actual)
                || !FileHandleIdentity.IdentitiesMatch(expected, actual)
                || !WorkspacePathPolicy.RevalidatePathBeforeIo(root, candidate))
            {
                return Snapshot(AttachmentSourceStatus.Unsafe, "Source identity or containment changed during validation.");
            }

            using MemoryStream buffer = new();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            byte[] verified = buffer.ToArray();
            string hash = Convert.ToHexString(SHA256.HashData(verified));
            string snapshotHash = Convert.ToHexString(SHA256.HashData(snapshotBytes.Span));
            FileInfo info = new(candidate);
            string relative = Path.GetRelativePath(root, candidate).Replace('\\', '/');
            AttachmentSourceStatus status = hash.Equals(snapshotHash, StringComparison.OrdinalIgnoreCase)
                ? AttachmentSourceStatus.Refreshable
                : AttachmentSourceStatus.PriorVersion;

            AttachmentSourceMetadata metadata = new(
                AttachmentSourceKind.WorkspaceFile,
                WorkspaceIdentity(root),
                relative,
                canonical,
                hash,
                $"{actual.VolumeId:X16}:{actual.FileId:X16}",
                info.LastWriteTimeUtc,
                info.Length,
                status,
                status == AttachmentSourceStatus.PriorVersion
                    ? "The persisted snapshot represents a prior source version."
                    : null);

            return new AttachmentSourceResolution(metadata, verified);
        }
        catch (FileNotFoundException)
        {
            return Snapshot(AttachmentSourceStatus.Missing, "The source file is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            return Snapshot(AttachmentSourceStatus.Missing, "The source file is missing.");
        }
        catch (UnauthorizedAccessException)
        {
            return Snapshot(AttachmentSourceStatus.Inaccessible, "The source file is inaccessible.");
        }
        catch (IOException)
        {
            return Snapshot(AttachmentSourceStatus.Inaccessible, "The source file could not be safely opened.");
        }
    }

    public async Task<AttachmentSourceMetadata> RevalidateAsync(
        AttachmentSourceMetadata source,
        CancellationToken cancellationToken = default)
    {
        if (source.Kind != AttachmentSourceKind.WorkspaceFile
            || string.IsNullOrWhiteSpace(source.WorkspaceRelativePath))
        {
            return AttachmentSourceMetadata.SnapshotOnly;
        }

        string? root = NormalizeWorkspaceRoot();
        if (root is null)
        {
            return source with { Status = AttachmentSourceStatus.WorkspaceUnavailable, DiagnosticReason = "No active registered workspace." };
        }

        if (!string.Equals(source.WorkspaceIdentity, WorkspaceIdentity(root), StringComparison.Ordinal))
        {
            return source with { Status = AttachmentSourceStatus.WorkspaceChanged, DiagnosticReason = "The active workspace identity changed." };
        }

        AttachmentSourceResolution resolution = await ResolveForPersistenceAsync(
            new AttachmentSourceClaim(Path.Combine(root, source.WorkspaceRelativePath)),
            ReadOnlyMemory<byte>.Empty,
            cancellationToken).ConfigureAwait(false);

        AttachmentSourceMetadata current = resolution.Metadata;
        if (current.Kind == AttachmentSourceKind.SnapshotOnly)
        {
            return source with { Status = current.Status, DiagnosticReason = current.DiagnosticReason };
        }

        AttachmentSourceStatus status = string.Equals(
            current.LastObservedContentSha256,
            source.LastObservedContentSha256,
            StringComparison.OrdinalIgnoreCase)
            ? AttachmentSourceStatus.Refreshable
            : AttachmentSourceStatus.PriorVersion;
        return current with { Status = status };
    }

    public async Task<AttachmentSourceResolution> ResolveCurrentAsync(
        AttachmentSourceMetadata source,
        string expectedSnapshotSha256,
        long maxBytes,
        AttachmentSourcePathAuthorizer authorizeCanonicalPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSnapshotSha256);
        ArgumentNullException.ThrowIfNull(authorizeCanonicalPath);

        if (source.Kind != AttachmentSourceKind.WorkspaceFile
            || string.IsNullOrWhiteSpace(source.WorkspaceIdentity)
            || string.IsNullOrWhiteSpace(source.WorkspaceRelativePath))
        {
            return Fail(source, AttachmentSourceStatus.CorruptMetadata,
                "The attachment does not carry verified refreshable workspace source metadata.");
        }

        string? root = NormalizeWorkspaceRoot();
        if (root is null)
        {
            return Fail(source, AttachmentSourceStatus.WorkspaceUnavailable,
                "No active registered workspace.");
        }

        if (!string.Equals(source.WorkspaceIdentity, WorkspaceIdentity(root), StringComparison.Ordinal))
        {
            return Fail(source, AttachmentSourceStatus.WorkspaceChanged,
                "The active workspace identity changed.");
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, source.WorkspaceRelativePath));
        }
        catch (Exception)
        {
            return Fail(source, AttachmentSourceStatus.Unsafe, "The stored source path is invalid.");
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspace(root, candidate))
        {
            return Fail(source, AttachmentSourceStatus.Unsafe,
                "The stored source path escapes the active workspace.");
        }

        if (!File.Exists(candidate))
        {
            return Fail(source, AttachmentSourceStatus.Missing, "The source file is missing.");
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, candidate, out string? canonical)
            || canonical is null)
        {
            return Fail(source, AttachmentSourceStatus.Unsafe,
                "The source is outside the active workspace after link resolution.");
        }

        if (!string.IsNullOrWhiteSpace(source.LastKnownCanonicalPath)
            && !PathEquals(source.LastKnownCanonicalPath, canonical))
        {
            return Fail(source, AttachmentSourceStatus.Unsafe,
                "The source link target changed since provenance was verified.");
        }

        try
        {
            await using FileStream stream = new(
                candidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (!FileHandleIdentityInterop.TryGetPathIdentity(candidate, out FileHandleIdentity expected)
                || !FileHandleIdentityInterop.TryGetHandleIdentity(stream.SafeFileHandle, out FileHandleIdentity actual)
                || !FileHandleIdentity.IdentitiesMatch(expected, actual)
                || !WorkspacePathPolicy.RevalidatePathBeforeIo(root, candidate))
            {
                return Fail(source, AttachmentSourceStatus.Unsafe,
                    "Source identity or containment changed during validation.");
            }

            if (!await authorizeCanonicalPath(canonical, cancellationToken).ConfigureAwait(false))
            {
                return Fail(source, AttachmentSourceStatus.Inaccessible,
                    "Sanctum denied access to the attachment source.");
            }

            if (stream.Length > maxBytes)
            {
                return Fail(source, AttachmentSourceStatus.Inaccessible,
                    $"The source exceeds the refresh size limit of {maxBytes} bytes.");
            }

            byte[] first = await ReadBoundedAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);

            if (AfterFirstRefreshReadForTesting is not null)
            {
                await AfterFirstRefreshReadForTesting(cancellationToken).ConfigureAwait(false);
            }

            if (!WorkspacePathPolicy.RevalidatePathBeforeIo(root, candidate)
                || !FileHandleIdentityInterop.TryGetPathIdentity(candidate, out FileHandleIdentity afterPath)
                || !FileHandleIdentityInterop.TryGetHandleIdentity(stream.SafeFileHandle, out FileHandleIdentity afterHandle)
                || !FileHandleIdentity.IdentitiesMatch(afterPath, afterHandle))
            {
                return Fail(source, AttachmentSourceStatus.Unsafe,
                    "Source identity or containment changed while it was read.");
            }

            stream.Position = 0;
            byte[] stable = await ReadBoundedAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
            byte[] firstHash = SHA256.HashData(first);
            byte[] stableHash = SHA256.HashData(stable);

            if (!CryptographicOperations.FixedTimeEquals(firstHash, stableHash))
            {
                return Fail(source, AttachmentSourceStatus.Unsafe,
                    "The source changed while it was read.");
            }

            string hash = Convert.ToHexString(stableHash);
            FileInfo info = new(candidate);
            AttachmentSourceStatus status = hash.Equals(
                expectedSnapshotSha256,
                StringComparison.OrdinalIgnoreCase)
                    ? AttachmentSourceStatus.Refreshable
                    : AttachmentSourceStatus.PriorVersion;
            AttachmentSourceMetadata current = source with
            {
                LastKnownCanonicalPath = canonical,
                LastObservedContentSha256 = hash,
                LastObservedFileIdentity = $"{afterHandle.VolumeId:X16}:{afterHandle.FileId:X16}",
                LastObservedWriteTime = info.LastWriteTimeUtc,
                LastObservedByteLength = stable.LongLength,
                Status = status,
                DiagnosticReason = status == AttachmentSourceStatus.PriorVersion
                    ? "The persisted snapshot represents a prior source version."
                    : null,
            };

            return new AttachmentSourceResolution(
                current,
                stable,
                AttachmentMimeDetector.Detect(stable, source.WorkspaceRelativePath));
        }
        catch (FileNotFoundException)
        {
            return Fail(source, AttachmentSourceStatus.Missing, "The source file is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            return Fail(source, AttachmentSourceStatus.Missing, "The source file is missing.");
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(source, AttachmentSourceStatus.Inaccessible, "The source file is inaccessible.");
        }
        catch (IOException)
        {
            return Fail(source, AttachmentSourceStatus.Inaccessible,
                "The source file could not be safely opened or read.");
        }
    }

    private string? NormalizeWorkspaceRoot()
    {
        string? root = workspaceContext.WorkspacePath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        return Path.GetFullPath(root);
    }

    private static string WorkspaceIdentity(string root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream output = new();
        byte[] buffer = new byte[64 * 1024];
        long total = 0;

        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new IOException("The source grew beyond the refresh size limit while being read.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static AttachmentSourceResolution Fail(
        AttachmentSourceMetadata source,
        AttachmentSourceStatus status,
        string reason) =>
        new(source with { Status = status, DiagnosticReason = reason }, ReadOnlyMemory<byte>.Empty);

    private static AttachmentSourceResolution Snapshot(AttachmentSourceStatus status, string reason) =>
        new(AttachmentSourceMetadata.SnapshotOnly with { Status = status, DiagnosticReason = reason }, ReadOnlyMemory<byte>.Empty);
}
