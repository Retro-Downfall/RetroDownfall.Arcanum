using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal sealed class AttachmentSourceResolver(IHostWorkspaceContext workspaceContext)
    : IAttachmentSourceResolver
{
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

    private static AttachmentSourceResolution Snapshot(AttachmentSourceStatus status, string reason) =>
        new(AttachmentSourceMetadata.SnapshotOnly with { Status = status, DiagnosticReason = reason }, ReadOnlyMemory<byte>.Empty);
}
