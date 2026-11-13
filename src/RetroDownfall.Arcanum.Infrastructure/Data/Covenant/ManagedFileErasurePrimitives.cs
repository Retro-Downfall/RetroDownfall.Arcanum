using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Win32.SafeHandles;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// What a no-follow open found at the recorded location.
/// </summary>
/// <remarks>
/// Three outcomes, and the distinction between the last two is the whole safety property.
/// <see cref="Absent"/> means the exact recorded walk still resolves and the leaf is genuinely gone;
/// <see cref="Mismatch"/> means something on the way there is not what the producer recorded, and the
/// deleter therefore has no idea whether the file it was asked to erase still exists somewhere. Only
/// the first may ever be recorded as proof of deletion.
/// </remarks>
internal enum ManagedFileOpenKind : byte
{

    Opened = 1,

    Absent = 2,

    Mismatch = 3,

}

/// <summary>
/// Whether an opened handle is the file the database claims to own.
/// </summary>
internal enum ManagedFileVerification : byte
{

    Match = 1,

    Mismatch = 2,

}

/// <summary>
/// Whether a verified compare-and-delete removed the file or refused to.
/// </summary>
internal enum ManagedFileCompareDeleteResult : byte
{

    Deleted = 1,

    Mismatch = 2,

}

/// <summary>
/// The domain-separated identity of one physical filesystem object.
/// </summary>
/// <remarks>
/// Volume and file identity — dev/ino on Unix, volume serial plus file index on Windows — hashed
/// under a fixed label so the value stored in a durable row discloses neither. Deliberately local to
/// Infrastructure rather than added to the Core digest corpus: the corpus carries pinned
/// cross-release vectors, and a filesystem identity is a host-local fact that has no business in a
/// contract other builds compare against.
/// </remarks>
internal static class ManagedFilePhysicalIdentity
{

    private static readonly byte[] Label =
        Encoding.UTF8.GetBytes("arcanum.covenant.managed-file.physical-identity.v1\0");

    internal static CovenantDigest Digest(FileHandleIdentity identity)
    {

        Span<byte> buffer = stackalloc byte[Label.Length + 16];

        Label.CopyTo(buffer);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer[Label.Length..], identity.VolumeId);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer[(Label.Length + 8)..], identity.FileId);

        Span<byte> hash = stackalloc byte[CovenantLimits.DigestBytes];

        _ = SHA256.HashData(buffer, hash);

        return new CovenantDigest(hash.ToArray());

    }

}

/// <summary>
/// One opened managed file, together with the parent directory handle its deletion must flush.
/// </summary>
/// <remarks>
/// The parent handle is retained rather than reopened. Reopening after the unlink would resolve the
/// name again, and the whole point of holding it is that the directory whose metadata gets flushed
/// is provably the directory the file was removed from.
///
/// <para>Disposal is idempotent and every accessor is rejected afterwards, because the kernel
/// disposes in a <c>finally</c> on every terminal and failure path and a second disposal must not
/// close a descriptor another operation now owns.</para>
/// </remarks>
internal sealed class ManagedFileOpenHandle : IDisposable
{

    private readonly SafeFileHandle _parentDirectory;

    private FileStream? _content;

    private int _disposed;

    internal ManagedFileOpenHandle(
        string fullPath,
        FileStream content,
        FileHandleIdentity identity,
        long length,
        SafeFileHandle parentDirectory)
    {

        FullPath = fullPath;

        _content = content;

        Identity = identity;

        Length = length;

        _parentDirectory = parentDirectory;

    }

    /// <summary>The resolved absolute path the handle was opened from.</summary>
    internal string FullPath { get; }

    /// <summary>The physical identity observed from the opened handle itself.</summary>
    internal FileHandleIdentity Identity { get; }

    /// <summary>The byte length observed from the opened handle itself.</summary>
    internal long Length { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>The opened content stream. Rejected once the handle has been disposed.</summary>
    internal FileStream Content =>
        _content is { } stream && !IsDisposed
            ? stream
            : throw new ObjectDisposedException(nameof(ManagedFileOpenHandle));

    /// <summary>
    /// Flushes the parent directory's metadata so the unlink survives a crash.
    /// </summary>
    /// <remarks>
    /// Unix flushes the retained directory descriptor. Windows has no directory-handle flush and
    /// journals directory metadata itself, so the call is satisfied there rather than failed — a
    /// deleter that reported "could not prove durability" on every Windows host would make manual
    /// recovery the normal outcome.
    /// </remarks>
    internal Result FlushParentToDisk()
    {

        if (IsDisposed)
        {

            throw new ObjectDisposedException(nameof(ManagedFileOpenHandle));

        }

        if (OperatingSystem.IsWindows())
        {

            return Result.Success();

        }

        try
        {

            RandomAccess.FlushToDisk(_parentDirectory);

            return Result.Success();

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {

            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.ManualArtifactErasureRequired,
                    "The parent directory of an erased managed file could not be flushed to disk."));

        }

    }

    public void Dispose()
    {

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {

            return;

        }

        _content?.Dispose();

        _content = null;

        _parentDirectory.Dispose();

    }

}

/// <summary>
/// The outcome of one no-follow open, carrying a handle only when something was opened.
/// </summary>
internal sealed record ManagedFileOpenOutcome(ManagedFileOpenKind Kind, ManagedFileOpenHandle? Handle)
{

    internal static ManagedFileOpenOutcome Absent { get; } = new(ManagedFileOpenKind.Absent, null);

    internal static ManagedFileOpenOutcome Mismatch { get; } = new(ManagedFileOpenKind.Mismatch, null);

    internal static ManagedFileOpenOutcome Opened(ManagedFileOpenHandle handle) =>
        new(ManagedFileOpenKind.Opened, handle);

}

/// <summary>
/// The Campaign root a durable location was recorded against, as the database currently records it.
/// </summary>
internal sealed record ManagedFileResolvedRoot(string RootPath, long PathRevision);

/// <summary>
/// The only way a managed file is opened for erasure.
/// </summary>
/// <remarks>
/// One implementation, and no path parameter. The opener is handed the root the database currently
/// records plus the producer's own durable location, revalidates the root's physical identity,
/// traverses only the recorded normalized segments without following a link at any step, and compares
/// the parent's identity from the same retained handle before the leaf is opened at all.
/// </remarks>
internal interface IManagedFileCapabilityOpener
{

    ValueTask<Result<ManagedFileOpenOutcome>> OpenNoFollowAsync(
        ManagedFileResolvedRoot root,
        ManagedFileDurableLocationEvidence location,
        CancellationToken cancellationToken);

}

/// <summary>
/// The only way an opened managed file is proved to be Arcanum's and then removed.
/// </summary>
internal interface IManagedFileOwnershipVerifier
{

    ValueTask<Result<ManagedFileVerification>> VerifyAsync(
        ManagedFileOpenHandle handle,
        ManagedFileOwnershipEvidence expected,
        CancellationToken cancellationToken);

    ValueTask<Result<ManagedFileCompareDeleteResult>> CompareDeleteAsync(
        ManagedFileOpenHandle handle,
        ManagedFileOwnershipEvidence expected,
        CancellationToken cancellationToken);

    /// <summary>
    /// Compare-deletes a child this installation created but never adopted, by physical identity alone.
    /// </summary>
    /// <remarks>
    /// Content is deliberately not compared here, and that is the whole difference from
    /// <see cref="CompareDeleteAsync"/>. An unadopted child is whatever the crash left behind — empty
    /// at <c>TempCreated</c>, partial at <c>TempWritten</c>, complete but unverified afterwards — so
    /// its expected content hash describes a file that may never have finished being written. The
    /// created-child physical identity is the only thing the producer recorded that proves the child
    /// on disk is the one this operation made, so it is the only thing compared.
    ///
    /// <para>The unlink, the name-reuse recheck, and the parent flush are the same sequence
    /// <see cref="CompareDeleteAsync"/> performs — there is one delete algorithm, reached two ways.</para>
    /// </remarks>
    ValueTask<Result<ManagedFileCompareDeleteResult>> CompareDeleteCreatedChildAsync(
        ManagedFileOpenHandle handle,
        CovenantDigest expectedCreatedChildPhysicalIdentityDigest,
        CancellationToken cancellationToken);

}

/// <summary>
/// The production opener, over the shared no-follow filesystem primitives.
/// </summary>
internal sealed class ManagedFileCapabilityOpener(PhysicalCampaignRootOpener roots) : IManagedFileCapabilityOpener
{

    public ValueTask<Result<ManagedFileOpenOutcome>> OpenNoFollowAsync(
        ManagedFileResolvedRoot root,
        ManagedFileDurableLocationEvidence location,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(root);

        ArgumentNullException.ThrowIfNull(location);

        cancellationToken.ThrowIfCancellationRequested();

        // The revision is a database fact the caller already revalidated in its own transaction. It is
        // compared again here because the opener is the last thing that runs before an irreversible
        // effect, and the cheapest possible check at that boundary is worth having twice.
        if (root.PathRevision != location.PathRevision)
        {

            return ValueTask.FromResult(Result<ManagedFileOpenOutcome>.Success(ManagedFileOpenOutcome.Mismatch));

        }

        SafeFileHandle? parent = null;

        try
        {

            if (!TryOpenDirectory(root.RootPath, out SafeFileHandle rootHandle, out CovenantDigest rootIdentity))
            {

                return ValueTask.FromResult(Result<ManagedFileOpenOutcome>.Success(ManagedFileOpenOutcome.Mismatch));

            }

            parent = rootHandle;

            if (roots.IdentifyOpenedDirectory(rootHandle) != location.CampaignRootIdentityDigest)
            {

                return ValueTask.FromResult(Result<ManagedFileOpenOutcome>.Success(ManagedFileOpenOutcome.Mismatch));

            }

            string parentPath = root.RootPath;

            CovenantDigest parentIdentity = rootIdentity;

            foreach (string segment in location.NormalizedParentSegments)
            {

                string childPath = Path.Combine(parentPath, segment);

                if (!TryOpenDirectory(childPath, out SafeFileHandle childHandle, out CovenantDigest childIdentity))
                {

                    return ValueTask.FromResult(
                        Result<ManagedFileOpenOutcome>.Success(ManagedFileOpenOutcome.Mismatch));

                }

                parent.Dispose();

                parent = childHandle;

                parentPath = childPath;

                parentIdentity = childIdentity;

            }

            // The parent's identity is compared from the same retained handle the leaf is opened
            // beneath. Comparing a freshly resolved path instead would prove a property of whatever
            // that name points at now, which is precisely the swap being guarded against.
            if (parentIdentity != location.ParentPhysicalIdentityDigest)
            {

                return ValueTask.FromResult(Result<ManagedFileOpenOutcome>.Success(ManagedFileOpenOutcome.Mismatch));

            }

            string leafPath = Path.Combine(parentPath, location.TargetLeaf);

            SecureFileOpenStatus status = SecureFileReader.TryOpenRegularFile(
                leafPath,
                expectedIdentity: null,
                out FileStream? stream,
                out FileHandleMetadata metadata);

            switch (status)
            {

                case SecureFileOpenStatus.NotFound:

                    return ValueTask.FromResult(
                        Result<ManagedFileOpenOutcome>.Success(ManagedFileOpenOutcome.Absent));

                case SecureFileOpenStatus.Rejected:

                    return ValueTask.FromResult(
                        Result<ManagedFileOpenOutcome>.Success(ManagedFileOpenOutcome.Mismatch));

                case SecureFileOpenStatus.Success:

                    ManagedFileOpenHandle handle = new(
                        leafPath,
                        stream!,
                        metadata.Identity,
                        stream!.Length,
                        parent);

                    parent = null;

                    return ValueTask.FromResult(Result<ManagedFileOpenOutcome>.Success(
                        ManagedFileOpenOutcome.Opened(handle)));

                default:

                    return ValueTask.FromResult(Result<ManagedFileOpenOutcome>.Failure(
                        new Error(
                            ErrorCodes.Covenant.ManualArtifactErasureRequired,
                            "A managed workspace file could not be opened for erasure.")));

            }

        }
        finally
        {

            parent?.Dispose();

        }

    }

    private static bool TryOpenDirectory(
        string path,
        out SafeFileHandle handle,
        out CovenantDigest identity)
    {

        if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                path,
                out handle,
                out FileHandleMetadata metadata))
        {

            identity = default;

            return false;

        }

        identity = ManagedFilePhysicalIdentity.Digest(metadata.Identity);

        return true;

    }

}

/// <summary>
/// The production ownership verifier and compare-deleter.
/// </summary>
/// <remarks>
/// Everything it compares is read from the already-open handle: identity and length from the handle's
/// own metadata, and the content hash from the handle's own stream. A verifier that reopened by path
/// would be describing whatever that name resolves to at that instant, which is the failure the
/// producer's evidence exists to detect.
/// </remarks>
internal sealed class ManagedFileOwnershipVerifier : IManagedFileOwnershipVerifier
{

    public async ValueTask<Result<ManagedFileVerification>> VerifyAsync(
        ManagedFileOpenHandle handle,
        ManagedFileOwnershipEvidence expected,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(handle);

        ArgumentNullException.ThrowIfNull(expected);

        if (handle.Length != expected.ContentLength
            || ManagedFilePhysicalIdentity.Digest(handle.Identity) != expected.PhysicalIdentityDigest)
        {

            return Result<ManagedFileVerification>.Success(ManagedFileVerification.Mismatch);

        }

        try
        {

            handle.Content.Position = 0;

            byte[] hash = await SHA256.HashDataAsync(handle.Content, cancellationToken).ConfigureAwait(false);

            return Result<ManagedFileVerification>.Success(
                new CovenantDigest(hash) == expected.ContentHash
                    ? ManagedFileVerification.Match
                    : ManagedFileVerification.Mismatch);

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            return Result<ManagedFileVerification>.Failure(
                new Error(
                    ErrorCodes.Covenant.ManualArtifactErasureRequired,
                    "A managed workspace file could not be read for ownership verification."));

        }

    }

    public async ValueTask<Result<ManagedFileCompareDeleteResult>> CompareDeleteAsync(
        ManagedFileOpenHandle handle,
        ManagedFileOwnershipEvidence expected,
        CancellationToken cancellationToken)
    {

        Result<ManagedFileVerification> verified = await VerifyAsync(handle, expected, cancellationToken)
            .ConfigureAwait(false);

        if (verified.IsFailure)
        {

            return Result<ManagedFileCompareDeleteResult>.Failure(verified.Error);

        }

        return verified.Value == ManagedFileVerification.Mismatch
            ? Result<ManagedFileCompareDeleteResult>.Success(ManagedFileCompareDeleteResult.Mismatch)
            : DeleteVerified(handle);

    }

    public ValueTask<Result<ManagedFileCompareDeleteResult>> CompareDeleteCreatedChildAsync(
        ManagedFileOpenHandle handle,
        CovenantDigest expectedCreatedChildPhysicalIdentityDigest,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(handle);

        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            ManagedFilePhysicalIdentity.Digest(handle.Identity)
                != expectedCreatedChildPhysicalIdentityDigest
                ? Result<ManagedFileCompareDeleteResult>.Success(
                    ManagedFileCompareDeleteResult.Mismatch)
                : DeleteVerified(handle));

    }

    /// <summary>
    /// The one unlink, name-reuse recheck, and parent flush both compare-deleters run.
    /// </summary>
    private static Result<ManagedFileCompareDeleteResult> DeleteVerified(
        ManagedFileOpenHandle handle)
    {

        try
        {

            File.Delete(handle.FullPath);

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            return Result<ManagedFileCompareDeleteResult>.Failure(
                new Error(
                    ErrorCodes.Covenant.ManualArtifactErasureRequired,
                    "A verified managed workspace file could not be unlinked."));

        }

        // Deletion is by name because no portable unlink-by-handle exists, which leaves a window
        // between the verified handle and the unlink. It is closed conservatively on the other side:
        // anything still answering to that name means either the delete did not take effect or
        // somebody else now owns the name, and neither is a removal this operation may claim.
        if (FileHandleIdentityInterop.TryGetPathIdentity(handle.FullPath, out _))
        {

            return Result<ManagedFileCompareDeleteResult>.Success(ManagedFileCompareDeleteResult.Mismatch);

        }

        Result flushed = handle.FlushParentToDisk();

        return flushed.IsFailure
            ? Result<ManagedFileCompareDeleteResult>.Failure(flushed.Error)
            : Result<ManagedFileCompareDeleteResult>.Success(ManagedFileCompareDeleteResult.Deleted);

    }

}
