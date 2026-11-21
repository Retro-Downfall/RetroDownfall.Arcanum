using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildWorkspaceAuthority
{
    internal async Task<Result<DirectReadScope>> OpenDirectReadScopeAsync(
        GuildGrant grant, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (workspaceId != registration.WorkspaceId || grant.Workspaces.IsDefaultOrEmpty)
        {
            return Forbidden;
        }

        Result valid = await ValidateAsync(grant, cancellationToken).ConfigureAwait(false);

        if (valid.IsFailure)
        {
            return valid.Error;
        }

        Dictionary<string, string> roots = new(StringComparer.Ordinal);

        foreach (GuildWorkspaceGrant scope in grant.Workspaces)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string path = scope.RelativeRoot == "" ? "." : scope.RelativeRoot;

            using DirectDirectory? directory = IsDirectDirectoryPath(path) ? TryOpenDirectDirectory(path) : null;

            if (directory is null)
            {
                return Unavailable;
            }

            roots[path] = directory.Fingerprint;
        }

        return new DirectReadScope(this, roots);
    }

    internal static bool IsDirectDirectoryPath(string path) => path is "." || IsDirectFilePath(path);

    internal static bool IsDirectFilePath(string path) =>
        IsRelativePath(path) && path.Split('/').All(WorkspaceLeaseIdentityResolver.IsSegment);

    private static bool Includes(string parent, string path) =>
        parent == "." || parent == path
        || path.Length > parent.Length && path[parent.Length] == '/' && path.StartsWith(parent, StringComparison.Ordinal);

    private DirectDirectory? TryOpenDirectDirectory(
        string path, IReadOnlyDictionary<string, string>? pinned = null, bool enumerate = false)
    {
        if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(registration.RootPath, requestEnumeration: enumerate && path == ".",
                out SafeFileHandle root, out FileHandleMetadata rootMetadata))
        {
            root.Dispose();

            return null;
        }

        SafeFileHandle current = root;

        bool transferred = false;

        try
        {
            if (opener.IdentifyOpenedDirectory(root) != registration.RootIdentity
                || !FileHandleIdentityInterop.TryGetHandleKernelPath(root, out string? currentPath))
            {
                return null;
            }

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            AppendIdentity(hash, rootMetadata.Identity);

            string fingerprint = Convert.ToHexString(hash.GetCurrentHash());

            if (pinned?.TryGetValue(".", out string? expected) == true && expected != fingerprint)
            {
                return null;
            }

            string relative = "";

            FileHandleIdentity identity = rootMetadata.Identity;

            foreach (string segment in path == "." ? [] : path.Split('/'))
            {
                relative = relative.Length == 0 ? segment : relative + "/" + segment;

                currentPath = Path.Combine(currentPath, segment);

                if (!FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(current, segment,
                        requestReadControl: false, requestEnumeration: enumerate && relative == path, out SafeFileHandle next, out FileHandleMetadata metadata))
                {
                    next.Dispose();

                    return null;
                }

                current.Dispose();

                current = next;

                if (metadata.Identity.VolumeId != rootMetadata.Identity.VolumeId
                    || !MatchesNamedHandle(current, currentPath, metadata.Identity, FileSystemObjectKind.Directory))
                {
                    return null;
                }

                identity = metadata.Identity;

                AppendIdentity(hash, identity);

                fingerprint = Convert.ToHexString(hash.GetCurrentHash());

                if (pinned?.TryGetValue(relative, out expected) == true && expected != fingerprint)
                {
                    return null;
                }
            }

            transferred = true;

            return new(current, currentPath, identity, fingerprint);
        }
        finally
        {
            if (!transferred)
            {
                current.Dispose();
            }
        }
    }

    private static bool MatchesNamedHandle(
        SafeFileHandle handle, string expectedPath, FileHandleIdentity identity, FileSystemObjectKind kind) =>
        FileHandleIdentityInterop.TryGetHandleKernelPath(handle, out string? actual)
        && string.Equals(expectedPath, actual, StringComparison.Ordinal)
        && FileHandleIdentityInterop.TryGetPathMetadataNoFollow(expectedPath, out FileHandleMetadata metadata)
        && metadata.Kind == kind && metadata.Identity == identity
        && (kind != FileSystemObjectKind.RegularFile || metadata.HardLinkCount == 1);

    internal static string FingerprintFile(string directoryFingerprint, FileHandleIdentity identity)
    {
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        digest.AppendData(Convert.FromHexString(directoryFingerprint));

        AppendIdentity(digest, identity);

        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static void AppendIdentity(IncrementalHash hash, FileHandleIdentity identity)
    {
        Span<byte> bytes = stackalloc byte[16];

        BinaryPrimitives.WriteUInt64BigEndian(bytes, identity.VolumeId);

        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], identity.FileId);

        hash.AppendData(bytes);
    }

    internal sealed class DirectReadScope(GuildWorkspaceAuthority owner, Dictionary<string, string> roots)
    {
        internal string WorkspaceRoot => owner.Registration.RootPath;

        internal byte[] BindCursorKey(ReadOnlySpan<byte> key, string toolName)
        {
            using MemoryStream bytes = new();

            using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);

            writer.Write("arcanum-guild-workspace-read-v1");

            writer.Write(toolName);

            writer.Write(owner.Registration.WorkspaceId.ToByteArray());

            writer.Write(owner.Registration.PolicyVersion);

            writer.Write(owner.Registration.Revision);

            writer.Write(owner.Registration.RootIdentity.Bytes);

            foreach ((string path, string identity) in roots.OrderBy(root => root.Key, StringComparer.Ordinal))
            {
                writer.Write(path);

                writer.Write(identity);
            }

            return HMACSHA256.HashData(key, bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
        }

        internal string[] SelectRoots(string requested)
        {
            if (roots.Keys.Any(root => Includes(root, requested)))
            {
                return [requested];
            }

            return [.. roots.Keys.Where(root => Includes(requested, root)
                    && !roots.Keys.Any(parent => parent != root && Includes(requested, parent) && Includes(parent, root)))
                .Order(StringComparer.Ordinal)];
        }

        internal DirectDirectory? OpenDirectory(string path, bool enumerate = false) =>
            roots.Keys.Any(root => Includes(root, path)) ? owner.TryOpenDirectDirectory(path, roots, enumerate) : null;

        internal DirectFile? OpenFile(string path, string? expectedFingerprint = null)
        {
            if (!IsDirectFilePath(path) || !roots.Keys.Any(root => Includes(root, path)))
            {
                return null;
            }

            int split = path.LastIndexOf('/');

            using DirectDirectory? parent = OpenDirectory(split < 0 ? "." : path[..split]);

            if (parent is null)
            {
                return null;
            }

            string leaf = path[(split + 1)..];

            string absolute = Path.Combine(parent.Path, leaf);

            if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(absolute, out FileHandleMetadata expected)
                || expected.Kind != FileSystemObjectKind.RegularFile || expected.HardLinkCount != 1
                || expected.Identity.VolumeId != parent.Identity.VolumeId
                || !MatchesNamedHandle(parent.Handle, parent.Path, parent.Identity, FileSystemObjectKind.Directory))
            {
                return null;
            }

            string fingerprint = FingerprintFile(parent.Fingerprint, expected.Identity);

            if (expectedFingerprint is not null && fingerprint != expectedFingerprint)
            {
                return null;
            }

            SecureFileOpenStatus status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(
                parent.Handle, leaf, out SafeFileHandle? handle);

            if (status != SecureFileOpenStatus.Success || handle is null)
            {
                handle?.Dispose();

                return null;
            }

            bool transferred = false;

            try
            {
                if (!SecureFileReader.TryValidateRegularFileHandle(handle, expected.Identity, out _)
                    || !MatchesNamedHandle(handle, absolute, expected.Identity, FileSystemObjectKind.RegularFile))
                {
                    return null;
                }

                long modified = File.GetLastWriteTimeUtc(handle).Ticks;

                FileStream stream = new(handle, FileAccess.Read, 4096, isAsync: OperatingSystem.IsWindows());

                transferred = true;

                return new(stream, absolute, expected.Identity, fingerprint, modified);
            }
            finally
            {
                if (!transferred)
                {
                    handle.Dispose();
                }
            }
        }

        internal async Task<Result> ValidateAsync(IReadOnlyList<DirectReadProof> proofs, CancellationToken cancellationToken)
        {
            try
            {
                return await ValidateProofsAsync(proofs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Unavailable;
            }
        }

        private async Task<Result> ValidateProofsAsync(IReadOnlyList<DirectReadProof> proofs, CancellationToken cancellationToken)
        {
            Result current = await owner.ValidateRegistrationAsync(cancellationToken).ConfigureAwait(false);

            if (current.IsFailure)
            {
                return current;
            }

            foreach ((string root, string expected) in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using DirectDirectory? actual = OpenDirectory(root);

                if (actual?.Fingerprint != expected)
                {
                    return Unavailable;
                }
            }

            foreach (DirectReadProof proof in proofs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Reopen from the pinned workspace and compare the complete directory chain and leaf.
                if (proof.Directory)
                {
                    using DirectDirectory? directory = OpenDirectory(proof.Path);

                    if (directory?.Fingerprint != proof.Fingerprint)
                    {
                        return Forbidden;
                    }
                }
                else
                {
                    using DirectFile? file = OpenFile(proof.Path, proof.Fingerprint);

                    if (file is null || !file.Matches()
                        || proof.Modified is long modified && file.Modified != modified
                        || proof.Content is { } content
                            && (!await content.MatchesAsync(file.Stream.SafeFileHandle, cancellationToken).ConfigureAwait(false) || !file.Matches()))
                    {
                        return Forbidden;
                    }
                }
            }

            return Result.Success();
        }
    }

    internal readonly record struct DirectReadProof(string Path, bool Directory, string Fingerprint,
        long? Modified = null, DirectReadContentProof? Content = null);

    internal sealed record DirectReadContentProof(long Offset, int Length, byte[] Digest)
    {
        internal async Task<bool> MatchesAsync(SafeFileHandle file, CancellationToken cancellationToken)
        {
            long fileLength = RandomAccess.GetLength(file);

            if (Offset > fileLength || Length > fileLength - Offset)
            {
                return false;
            }

            byte[] buffer = new byte[Math.Min(4096, Length)];

            using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            int remaining = Length;

            while (remaining > 0)
            {
                int read = await FileHandleIdentityInterop.ReadAtOffsetAsync(file, buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    Offset + Length - remaining, cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    return false;
                }

                digest.AppendData(buffer.AsSpan(0, read));

                remaining -= read;
            }

            return CryptographicOperations.FixedTimeEquals(Digest, digest.GetHashAndReset());
        }
    }

    internal sealed record DirectDirectory(SafeFileHandle Handle, string Path, FileHandleIdentity Identity, string Fingerprint)
        : IDisposable
    {
        internal bool Matches() => MatchesNamedHandle(Handle, Path, Identity, FileSystemObjectKind.Directory);

        public void Dispose() => Handle.Dispose();
    }

    internal sealed record DirectFile(FileStream Stream, string Path, FileHandleIdentity Identity, string Fingerprint, long Modified)
        : IDisposable
    {
        internal bool Matches() =>
            MatchesNamedHandle(Stream.SafeFileHandle, Path, Identity, FileSystemObjectKind.RegularFile)
            && File.GetLastWriteTimeUtc(Stream.SafeFileHandle).Ticks == Modified;

        public void Dispose() => Stream.Dispose();
    }
}
