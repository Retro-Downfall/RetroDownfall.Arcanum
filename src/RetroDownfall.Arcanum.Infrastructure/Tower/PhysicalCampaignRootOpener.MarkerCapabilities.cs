using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Microsoft.Win32.SafeHandles;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Tower;

internal sealed partial class PhysicalCampaignRootOpener
{
    /// <summary>The fixed private directory a Campaign marker lives in.</summary>
    private const string MarkerDirectoryLeaf = ".arcanum";

    /// <summary>The fixed private marker leaf, never supplied by a caller.</summary>
    private const string MarkerFileLeaf = "campaign-root.marker";

    internal Action? AfterRootHandleOpenedBeforeMarkerDirectoryOpenForTests { get; set; }

    internal Action? BeforeMarkerChildOpenForTests { get; set; }

    internal Action? BeforeMarkerChildEffectForTests { get; set; }

    /// <summary>The longest display path this producer will consider.</summary>
    /// <remarks>Matches the durable <c>TargetDisplayPath</c> ceiling, so nothing openable is unstorable.</remarks>
    private const int MaximumDisplayPathLength = 4096;

    /// <summary>The longest single path segment a caller may name.</summary>
    private const int MaximumLeafLength = 255;

    /// <summary>
    /// Opens one proven Campaign root and its marker directory, and retains both handles.
    /// </summary>
    /// <remarks>
    /// This is the only place in the marker protocol where a path is used at all. Everything the caller
    /// does afterwards goes through the returned capability, which names at most a bounded leaf — so a
    /// later phase cannot be redirected by re-resolving a display path that meanwhile became a symlink,
    /// which is precisely the window a create-then-rename sequence would otherwise leave open (§10.12).
    ///
    /// <para>Failure is total and leaves nothing retained: a rejected root has no half-open handle a
    /// caller could still reach through, because the checks run before the capability exists.</para>
    /// </remarks>
    internal ValueTask<Result<MarkerRootCapability>> OpenForMarkerLifecycleAsync(
        Guid campaignId,
        long pathRevision,
        CovenantDigest expectedPhysicalIdentityDigest,
        string canonicalDisplayPath,
        CancellationToken cancellationToken) =>
        OpenForMarkerLifecycleAsync(
            campaignId,
            pathRevision,
            expectedPhysicalIdentityDigest,
            canonicalDisplayPath,
            requireExistingMarkerDirectory: false,
            cancellationToken);

    /// <summary>
    /// Opens one proven Campaign root while requiring its private marker directory to already exist.
    /// </summary>
    /// <remarks>
    /// Full-reset inventory is read-only until its pair checkpoint is durable. This arm therefore
    /// never creates <c>.arcanum</c>; absence is a refusal, while ordinary registration keeps using
    /// <see cref="OpenForMarkerLifecycleAsync"/> and its established create-capable behavior.
    /// </remarks>
    internal ValueTask<Result<MarkerRootCapability>> OpenExistingForMarkerLifecycleAsync(
        Guid campaignId,
        long pathRevision,
        CovenantDigest expectedPhysicalIdentityDigest,
        string canonicalDisplayPath,
        CancellationToken cancellationToken) =>
        OpenForMarkerLifecycleAsync(
            campaignId,
            pathRevision,
            expectedPhysicalIdentityDigest,
            canonicalDisplayPath,
            requireExistingMarkerDirectory: true,
            cancellationToken);

    private ValueTask<Result<MarkerRootCapability>> OpenForMarkerLifecycleAsync(
        Guid campaignId,
        long pathRevision,
        CovenantDigest expectedPhysicalIdentityDigest,
        string canonicalDisplayPath,
        bool requireExistingMarkerDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (campaignId == Guid.Empty
            || pathRevision <= 0
            || !expectedPhysicalIdentityDigest.IsValid
            || string.IsNullOrWhiteSpace(canonicalDisplayPath)
            || canonicalDisplayPath.Length > MaximumDisplayPathLength)
        {
            return ValueTask.FromResult<Result<MarkerRootCapability>>(InvalidRootRequest);
        }

        string canonical;

        try
        {
            canonical = Path.GetFullPath(canonicalDisplayPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ValueTask.FromResult<Result<MarkerRootCapability>>(InvalidRootRequest);
        }

        // A path that is not already canonical is refused rather than normalized. Silently accepting a
        // different spelling is how the same root acquires two registered identities.
        if (!string.Equals(canonical, canonicalDisplayPath, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<Result<MarkerRootCapability>>(InvalidRootRequest);
        }

        return ValueTask.FromResult(
            OpenRetainedPair(
                campaignId,
                pathRevision,
                expectedPhysicalIdentityDigest,
                canonical,
                requireExistingMarkerDirectory));
    }

    private static Error InvalidRootRequest =>
        new(ErrorCodes.Campaign.InvalidPath, "The Campaign root could not be opened and proven.");

    private static Error UnprovenIdentity =>
        new(
            ErrorCodes.Campaign.PathIdentityRequired,
            "The Campaign root did not match the physical identity it was opened for.");

    private Result<MarkerRootCapability> OpenRetainedPair(
        Guid campaignId,
        long pathRevision,
        CovenantDigest expectedPhysicalIdentityDigest,
        string canonicalRootPath,
        bool requireExistingMarkerDirectory)
    {
        SafeFileHandle? root = null;

        SafeFileHandle? markerDirectory = null;

        try
        {
            if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                    canonicalRootPath,
                    out root,
                    out FileHandleMetadata rootMetadata))
            {
                // A symlink, a reparse point, a file, or an absent directory all land here, because the
                // open itself refuses to follow the last component.
                return InvalidRootRequest;
            }

            CovenantDigest? observed = DeriveIdentityDigest(rootMetadata.Identity);

            if (observed is not { } rootIdentityDigest
                || rootIdentityDigest != expectedPhysicalIdentityDigest)
            {
                return UnprovenIdentity;
            }

            AfterRootHandleOpenedBeforeMarkerDirectoryOpenForTests?.Invoke();

            string markerDirectoryPath = Path.Combine(canonicalRootPath, MarkerDirectoryLeaf);

            if (requireExistingMarkerDirectory
                    ? !TryValidateExistingMarkerDirectory(markerDirectoryPath)
                    : !TryPrepareMarkerDirectory(markerDirectoryPath))
            {
                return InvalidRootRequest;
            }

            if (!FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(
                    root,
                    MarkerDirectoryLeaf,
                    requestReadControl: true,
                    out markerDirectory,
                    out FileHandleMetadata markerMetadata))
            {
                return InvalidRootRequest;
            }

            // Same volume as its own root. A mounted filesystem where the marker directory belongs is a
            // boundary someone else controls, not a subdirectory of this Campaign.
            if (markerMetadata.Identity.VolumeId != rootMetadata.Identity.VolumeId)
            {
                return InvalidRootRequest;
            }

            if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollowIgnoringTestSeam(
                    canonicalRootPath,
                    out FileHandleMetadata currentRoot)
                || !FileHandleIdentity.IdentitiesMatch(
                    rootMetadata.Identity,
                    currentRoot.Identity)
                || !FileHandleIdentityInterop.TryGetPathMetadataNoFollowIgnoringTestSeam(
                    markerDirectoryPath,
                    out FileHandleMetadata currentMarkerDirectory)
                || !FileHandleIdentity.IdentitiesMatch(
                    markerMetadata.Identity,
                    currentMarkerDirectory.Identity))
            {
                return InvalidRootRequest;
            }

            MarkerRootCapability capability = MarkerRootCapability.Adopt(
                this,
                campaignId,
                pathRevision,
                rootIdentityDigest,
                root,
                markerDirectory,
                rootMetadata.Identity,
                markerMetadata.Identity,
                markerDirectoryPath);

            root = null;

            markerDirectory = null;

            return capability;
        }
        finally
        {
            root?.Dispose();

            markerDirectory?.Dispose();
        }
    }

    /// <summary>
    /// Proves the marker directory is an owner-only real directory, creating it when absent.
    /// </summary>
    /// <remarks>
    /// A group- or other-writable marker directory is refused rather than tightened in place. Anyone who
    /// can write there can drop a file at the marker leaf between the absence check and the rename, and
    /// a producer that repaired the mode would be asserting the window never existed.
    /// </remarks>
    private static bool TryPrepareMarkerDirectory(string markerDirectoryPath)
    {
        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                markerDirectoryPath,
                out FileHandleMetadata existing))
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Windows: the directory inherits the Campaign root's ACL. It is neither tightened
                    // nor DACL-verified here; owner-only posture is a Unix mode check (§10.12).
                    _ = Directory.CreateDirectory(markerDirectoryPath);
                }
                else
                {
                    _ = Directory.CreateDirectory(
                        markerDirectoryPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                return true;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                return false;
            }
        }

        return ExistingMarkerDirectoryIsUsable(markerDirectoryPath, existing);
    }

    private static bool TryValidateExistingMarkerDirectory(string markerDirectoryPath) =>
        FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
            markerDirectoryPath,
            out FileHandleMetadata existing)
        && ExistingMarkerDirectoryIsUsable(markerDirectoryPath, existing);

    private static bool ExistingMarkerDirectoryIsUsable(
        string markerDirectoryPath,
        FileHandleMetadata existing)
    {
        if (existing.Kind is not FileSystemObjectKind.Directory)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(markerDirectoryPath);

            return (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0
                && CampaignMarkerNativeMethods.OwnsPath(markerDirectoryPath);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Derives the identity a claimed volume and file identifier pair would produce, opening nothing.
    /// </summary>
    /// <remarks>
    /// The one derivation that starts from a claim rather than from a handle, and it exists for one
    /// caller: post-restart marker reconciliation, which retains no root and has only the marker's own
    /// self-binding tuple to prove the directory it reopened with. A marker records the volume and file
    /// identifiers of the root it was written into, so a marker that was copied elsewhere derives an
    /// identity its new home cannot produce — the copy contradicts where it now lives (§10.12).
    ///
    /// <para>It returns an expectation to compare against and never a capability. A method that opened
    /// anything from a tuple would be a second way to mint root authority, this time out of bytes
    /// whoever can write into a directory gets to choose, which is exactly the substitution the whole
    /// identity protocol exists to refuse. <c>CampaignPathMarkerRootProofCallSiteTests</c> pins the
    /// call sites, because a digest derived from a claim is indistinguishable from one taken through a
    /// proven handle once it has been returned.</para>
    /// </remarks>
    internal CovenantDigest? DeriveClaimedRootIdentityDigest(ulong volumeId, ulong fileId) =>
        DeriveIdentityDigest(new FileHandleIdentity(volumeId, fileId));

    /// <summary>
    /// Derives one opaque identity digest, or reports that the installation key is unavailable.
    /// </summary>
    private CovenantDigest? DeriveIdentityDigest(FileHandleIdentity identity)
    {
        Span<byte> key = stackalloc byte[32];

        try
        {
            return _keys.TryCopyRootIdentityKey(key)
                ? DeriveIdentity(key, identity)
                : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool IsBoundedLeaf(string? leaf)
    {
        if (string.IsNullOrWhiteSpace(leaf)
            || leaf.Length > MaximumLeafLength
            || leaf is "." or ".."
            || leaf[0] is '.'
            || string.Equals(leaf, MarkerFileLeaf, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char character in leaf)
        {
            // An explicit allowlist rather than a list of forbidden characters. Separators, traversal,
            // NTFS alternate-stream colons, NUL, and every Unicode spelling that normalizes onto another
            // name are all excluded by not being on it.
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        ReadOnlySpan<char> stem = leaf.AsSpan();

        int dot = stem.IndexOf('.');

        if (dot >= 0)
        {
            stem = stem[..dot];
        }

        return !IsReservedDeviceName(stem);
    }

    private static bool IsReservedDeviceName(ReadOnlySpan<char> stem) =>
        stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
        || (stem.Length == 4
            && char.IsAsciiDigit(stem[3])
            && stem[3] is not '0'
            && (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase)
                || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)));

    private static Error MarkerLeafRejected =>
        new(ErrorCodes.Campaign.PathNotAllowed, "The supplied marker leaf is not one bounded segment.");

    private static Error MarkerRootChanged =>
        new(
            ErrorCodes.Campaign.PathIdentityRequired,
            "The retained Campaign marker directory is no longer the directory that was opened.");

    private static Error MarkerEvidenceRejected =>
        new(
            ErrorCodes.Campaign.InvalidPath,
            "The supplied marker capability did not match the expected identity and exact bytes.");

    private static Error MarkerIoFailed =>
        new(ErrorCodes.Campaign.DirectoryCreateFailed, "The Campaign marker operation could not complete.");

    /// <summary>
    /// One proven Campaign root plus its marker directory, held open for the whole marker protocol.
    /// </summary>
    /// <remarks>
    /// Every filesystem effect the protocol performs is reached through this object, and it accepts a
    /// bounded leaf at most — never a directory, a parent, or a target path. That is what makes the
    /// create/write/fsync/rename sequence safe to interrupt: a resumed phase reuses the same retained
    /// handles rather than re-resolving a path that could have become a symlink while the process was
    /// down (§10.12). On macOS and Linux the create, rename, and unlink themselves run relative to the
    /// retained marker-directory descriptor (<c>openat</c>, no-replace <c>renameat</c>,
    /// <c>unlinkat</c>), so a directory swapped in under the display path receives nothing. On Windows
    /// those three effects are still issued by path, each immediately after the retained directory's
    /// identity is re-proven at that path, so a swap inside that window is narrowed rather than closed;
    /// reads there are handle-relative as on Unix.
    ///
    /// <para>Child capabilities are checked for belonging by reference to this exact instance, not by
    /// comparing evidence. Byte and identity evidence that matches is not authority over a file — the
    /// same well-formed marker under a second Campaign's root would otherwise authorize a delete there
    /// too.</para>
    /// </remarks>
    internal sealed class MarkerRootCapability : IAsyncDisposable
    {
        private readonly PhysicalCampaignRootOpener _producer;

        private readonly FileHandleIdentity _markerDirectoryIdentity;

        private readonly string _markerDirectoryPath;

        private SafeFileHandle? _root;

        private SafeFileHandle? _markerDirectory;

        private MarkerRootCapability(
            PhysicalCampaignRootOpener producer,
            Guid campaignId,
            long pathRevision,
            CovenantDigest physicalIdentityDigest,
            SafeFileHandle root,
            SafeFileHandle markerDirectory,
            FileHandleIdentity rootIdentity,
            FileHandleIdentity markerDirectoryIdentity,
            string markerDirectoryPath)
        {
            _producer = producer;

            CampaignId = campaignId;

            PathRevision = pathRevision;

            PhysicalIdentityDigest = physicalIdentityDigest;

            _root = root;

            _markerDirectory = markerDirectory;

            RootIdentity = rootIdentity;

            _markerDirectoryIdentity = markerDirectoryIdentity;

            _markerDirectoryPath = markerDirectoryPath;
        }

        internal Guid CampaignId { get; }

        internal long PathRevision { get; }

        internal CovenantDigest PhysicalIdentityDigest { get; }

        private FileHandleIdentity RootIdentity { get; }

        /// <summary>
        /// The one call site of the private constructor, invoked only by the producer's proven open.
        /// </summary>
        /// <remarks>
        /// A type-owned factory rather than a reachable constructor: C# does not let the enclosing type
        /// call a nested private constructor, and widening the constructor instead would leave a second
        /// way to mint a root capability that skipped the identity proof entirely.
        /// </remarks>
        internal static MarkerRootCapability Adopt(
            PhysicalCampaignRootOpener producer,
            Guid campaignId,
            long pathRevision,
            CovenantDigest physicalIdentityDigest,
            SafeFileHandle root,
            SafeFileHandle markerDirectory,
            FileHandleIdentity rootIdentity,
            FileHandleIdentity markerDirectoryIdentity,
            string markerDirectoryPath) =>
            new(
                producer,
                campaignId,
                pathRevision,
                physicalIdentityDigest,
                root,
                markerDirectory,
                rootIdentity,
                markerDirectoryIdentity,
                markerDirectoryPath);

        /// <summary>
        /// Creates one temporary file that must not already exist.
        /// </summary>
        /// <remarks>
        /// Exclusive creation is the no-follow guarantee here: an exclusive create refuses a name that
        /// already resolves to anything at all, symlink included, so there is no separate check to lose
        /// a race against.
        /// </remarks>
        internal ValueTask<Result<MarkerTemporaryHandleCapability>> CreateTemporaryExclusiveNoFollowAsync(
            string temporaryLeaf,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _ = GetMarkerDirectory();

            if (!IsBoundedLeaf(temporaryLeaf))
            {
                return ValueTask.FromResult<Result<MarkerTemporaryHandleCapability>>(MarkerLeafRejected);
            }

            if (!MarkerDirectoryIsStillOurs())
            {
                return ValueTask.FromResult<Result<MarkerTemporaryHandleCapability>>(MarkerRootChanged);
            }

            _producer.BeforeMarkerChildOpenForTests?.Invoke();

            if (!OperatingSystem.IsWindows())
            {
                return ValueTask.FromResult(CreateTemporaryRelative(temporaryLeaf));
            }

            string path = Path.Combine(_markerDirectoryPath, temporaryLeaf);

            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,

                // Windows needs FILE_SHARE_DELETE to let the *owner* rename or unlink a file it still
                // holds open, and this capability does exactly that: it writes the temporary, flushes
                // it, and renames it onto the marker leaf without ever letting go of the handle,
                // because the handle is the proof. Without the flag every rename here fails with a
                // sharing violation and the marker can never be committed, which is what the whole
                // CampaignPath family did on Windows. It is not a weakening: read and write sharing
                // stay denied, and this code already assumes an entry can move under it — that is why
                // it proves identity by volume and file id rather than by path. POSIX has always
                // permitted the rename, so this makes Windows agree with the semantics the design was
                // written against rather than the other way round.
                //
                // Unix keeps FileShare.None, which .NET implements as an exclusive flock. Naming
                // Delete there instead would downgrade that to a shared lock, and the enum cannot say
                // "exclusive and deletable" — FileShare.None is zero, so None | Delete is just Delete.
                Share = OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None,
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            FileStream? stream = null;

            try
            {
                stream = new FileStream(path, options);

                Result<MarkerTemporaryHandleCapability> created = AdoptTemporary(
                    stream,
                    temporaryLeaf,
                    writable: true);

                if (created.IsSuccess)
                {
                    stream = null;
                }

                return ValueTask.FromResult(created);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                return ValueTask.FromResult<Result<MarkerTemporaryHandleCapability>>(MarkerIoFailed);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        /// <summary>
        /// The macOS and Linux create: <c>openat</c> relative to the retained marker directory.
        /// </summary>
        /// <remarks>
        /// <c>O_CREAT | O_EXCL | O_NOFOLLOW</c> against the retained descriptor, so neither a symlink at
        /// the leaf nor a directory swapped in under the display path after the identity re-check can
        /// receive the file. The exclusive <c>flock</c> a path-opened <c>FileShare.None</c> stream would
        /// take is taken here too.
        /// </remarks>
        private Result<MarkerTemporaryHandleCapability> CreateTemporaryRelative(string temporaryLeaf)
        {
            SafeFileHandle? handle = CampaignMarkerNativeMethods.TryCreateExclusiveRelative(
                GetMarkerDirectory(),
                temporaryLeaf);

            if (handle is null)
            {
                return MarkerIoFailed;
            }

            FileStream? stream = null;

            try
            {
                stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: false);

                handle = null;

                Result<MarkerTemporaryHandleCapability> created = AdoptTemporary(
                    stream,
                    temporaryLeaf,
                    writable: true);

                if (created.IsSuccess)
                {
                    stream = null;
                }

                return created;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                return MarkerIoFailed;
            }
            finally
            {
                stream?.Dispose();

                handle?.Dispose();
            }
        }

        /// <summary>
        /// Reopens exactly the temporary leaf a crashed run journaled.
        /// </summary>
        /// <remarks>
        /// Read-only on purpose. Recovery's job is to decide whether the file it finds is the one the
        /// journal describes; a writable reopen would let a resumed phase repair a mismatch into a match
        /// and then adopt it.
        /// </remarks>
        internal ValueTask<Result<MarkerTemporaryHandleCapability>> OpenTemporaryNoFollowAsync(
            string temporaryLeaf,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _ = GetMarkerDirectory();

            if (!IsBoundedLeaf(temporaryLeaf))
            {
                return ValueTask.FromResult<Result<MarkerTemporaryHandleCapability>>(MarkerLeafRejected);
            }

            if (!MarkerDirectoryIsStillOurs())
            {
                return ValueTask.FromResult<Result<MarkerTemporaryHandleCapability>>(MarkerRootChanged);
            }

            Result<FileStream> opened = OpenChildReadOnly(temporaryLeaf, out bool absent);

            if (!opened.IsSuccess)
            {
                return ValueTask.FromResult<Result<MarkerTemporaryHandleCapability>>(
                    absent ? MarkerIoFailed : opened.Error);
            }

            FileStream stream = opened.Value;

            Result<MarkerTemporaryHandleCapability> adopted = AdoptTemporary(
                stream,
                temporaryLeaf,
                writable: false);

            if (!adopted.IsSuccess)
            {
                stream.Dispose();
            }

            return ValueTask.FromResult(adopted);
        }

        /// <summary>
        /// Opens the marker, or proves nothing answers to its name.
        /// </summary>
        internal ValueTask<Result<PhysicalCampaignMarkerOpenResult>> OpenMarkerOrProveAbsentNoFollowAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _ = GetMarkerDirectory();

            if (!MarkerDirectoryIsStillOurs())
            {
                return ValueTask.FromResult<Result<PhysicalCampaignMarkerOpenResult>>(MarkerRootChanged);
            }

            Result<FileStream> opened = OpenChildReadOnly(MarkerFileLeaf, out bool absent);

            if (absent)
            {
                return ValueTask.FromResult<Result<PhysicalCampaignMarkerOpenResult>>(
                    new PhysicalCampaignMarkerOpenResult.Absent());
            }

            if (!opened.IsSuccess)
            {
                return ValueTask.FromResult<Result<PhysicalCampaignMarkerOpenResult>>(opened.Error);
            }

            FileStream stream = opened.Value;

            Result<MarkerHandleCapability> adopted = AdoptMarker(stream);

            if (!adopted.IsSuccess)
            {
                stream.Dispose();

                return ValueTask.FromResult<Result<PhysicalCampaignMarkerOpenResult>>(adopted.Error);
            }

            return ValueTask.FromResult<Result<PhysicalCampaignMarkerOpenResult>>(
                new PhysicalCampaignMarkerOpenResult.Opened(adopted.Value));
        }

        /// <summary>
        /// Moves a proven temporary onto the marker leaf, refusing to replace anything already there.
        /// </summary>
        internal ValueTask<Result> RenameTemporaryToMarkerNoReplaceAsync(
            MarkerTemporaryHandleCapability temporary,
            CovenantDigest expectedTemporaryPhysicalIdentityDigest,
            ReadOnlyMemory<byte> expectedExactCodecBytes,
            CancellationToken cancellationToken) =>
            RenameProvenChildAsync(
                temporary,
                expectedTemporaryPhysicalIdentityDigest,
                expectedExactCodecBytes,
                MarkerFileLeaf,
                cancellationToken);

        /// <summary>
        /// Moves a proven marker aside under a bounded quarantine leaf, replacing nothing.
        /// </summary>
        internal ValueTask<Result> RenameMarkerToQuarantineNoReplaceAsync(
            MarkerHandleCapability marker,
            string quarantineLeaf,
            CovenantDigest expectedMarkerPhysicalIdentityDigest,
            ReadOnlyMemory<byte> expectedExactCodecBytes,
            CancellationToken cancellationToken) =>
            IsBoundedLeaf(quarantineLeaf)
                ? RenameProvenChildAsync(
                    marker,
                    expectedMarkerPhysicalIdentityDigest,
                    expectedExactCodecBytes,
                    quarantineLeaf,
                    cancellationToken)
                : ValueTask.FromResult<Result>(MarkerLeafRejected);

        /// <summary>
        /// Removes a temporary only when the same handle still holds the same exact bytes.
        /// </summary>
        /// <remarks>
        /// The sole compensation and pre-rename abort. A mismatch leaves the file exactly where it is:
        /// this path runs when the process already knows something went wrong, and "delete whatever
        /// answers to that name" is how a botched rollback removes a file that belonged to someone else.
        /// </remarks>
        internal ValueTask<Result> CompareDeleteTemporaryAsync(
            MarkerTemporaryHandleCapability temporary,
            CovenantDigest expectedTemporaryPhysicalIdentityDigest,
            ReadOnlyMemory<byte> expectedExactCodecBytes,
            CancellationToken cancellationToken) =>
            CompareDeleteChildAsync(
                temporary,
                expectedTemporaryPhysicalIdentityDigest,
                expectedExactCodecBytes,
                cancellationToken);

        /// <summary>
        /// Removes the marker only when the same handle still holds the same exact bytes.
        /// </summary>
        internal ValueTask<Result> CompareDeleteMarkerAsync(
            MarkerHandleCapability marker,
            CovenantDigest expectedMarkerPhysicalIdentityDigest,
            ReadOnlyMemory<byte> expectedExactCodecBytes,
            CancellationToken cancellationToken) =>
            CompareDeleteChildAsync(
                marker,
                expectedMarkerPhysicalIdentityDigest,
                expectedExactCodecBytes,
                cancellationToken);

        /// <summary>
        /// Flushes the retained marker directory so a rename or unlink survives a power loss.
        /// </summary>
        /// <remarks>
        /// A separate operation rather than a step inside rename, so the caller can commit its durable
        /// phase only after the barrier actually returned. Folding it into the rename would let a phase
        /// record a durability guarantee the flush had not yet provided (§10.17).
        /// </remarks>
        internal ValueTask<Result> FlushMarkerDirectoryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SafeFileHandle directory = GetMarkerDirectory();

            return ValueTask.FromResult(
                CampaignMarkerNativeMethods.TryFlushDirectory(directory)
                    ? Result.Success()
                    : Result.Failure(MarkerIoFailed));
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            SafeFileHandle? markerDirectory = Interlocked.Exchange(ref _markerDirectory, null);

            SafeFileHandle? root = Interlocked.Exchange(ref _root, null);

            markerDirectory?.Dispose();

            root?.Dispose();

            await ValueTask.CompletedTask;
        }

        private SafeFileHandle GetMarkerDirectory() =>
            Volatile.Read(ref _markerDirectory)
            ?? throw new ObjectDisposedException(nameof(MarkerRootCapability));

        private bool MarkerDirectoryIsStillOurs() =>
            FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                _markerDirectoryPath,
                out FileHandleMetadata metadata)
            && metadata.Kind is FileSystemObjectKind.Directory
            && FileHandleIdentity.IdentitiesMatch(_markerDirectoryIdentity, metadata.Identity)
            && FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                Path.GetDirectoryName(_markerDirectoryPath) ?? _markerDirectoryPath,
                out FileHandleMetadata parent)
            && FileHandleIdentity.IdentitiesMatch(RootIdentity, parent.Identity);

        private Result<FileStream> OpenChildReadOnly(string leaf, out bool absent)
        {
            absent = false;

            _producer.BeforeMarkerChildOpenForTests?.Invoke();

            SecureFileOpenStatus status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(
                GetMarkerDirectory(),
                leaf,
                out SafeFileHandle? handle);

            if (status is SecureFileOpenStatus.NotFound)
            {
                absent = true;

                return MarkerIoFailed;
            }

            if (status is not SecureFileOpenStatus.Success || handle is null)
            {
                // Rejected covers the link case: O_NOFOLLOW refused the final component, so a symlink
                // where a marker belongs is a positive failure rather than a reported absence.
                return MarkerEvidenceRejected;
            }

            try
            {
                return new FileStream(
                    handle,
                    FileAccess.Read,
                    bufferSize: 0,
                    isAsync: OperatingSystem.IsWindows());
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                handle.Dispose();

                return MarkerIoFailed;
            }
        }

        private Result<MarkerTemporaryHandleCapability> AdoptTemporary(
            FileStream stream,
            string leaf,
            bool writable)
        {
            Result<CovenantDigest> proven = ProveChildHandle(stream, out FileHandleIdentity identity);

            return proven.IsSuccess
                ? MarkerTemporaryHandleCapability.Adopt(
                    this,
                    stream,
                    leaf,
                    identity,
                    proven.Value,
                    writable)
                : proven.Error;
        }

        private Result<MarkerHandleCapability> AdoptMarker(FileStream stream)
        {
            Result<CovenantDigest> proven = ProveChildHandle(stream, out FileHandleIdentity identity);

            return proven.IsSuccess
                ? MarkerHandleCapability.Adopt(this, stream, identity, proven.Value)
                : proven.Error;
        }

        private Result<CovenantDigest> ProveChildHandle(
            FileStream stream,
            out FileHandleIdentity identity)
        {
            identity = default;

            // Regular, unaliased, and on the same volume as the directory it was opened relative to. A
            // hard link into the marker directory would otherwise let an outside name survive every
            // compare-delete this capability performs.
            if (!SecureFileReader.TryValidateRegularFileHandle(
                    stream.SafeFileHandle,
                    expectedIdentity: null,
                    out FileHandleMetadata metadata)
                || metadata.Identity.VolumeId != _markerDirectoryIdentity.VolumeId)
            {
                return MarkerEvidenceRejected;
            }

            CovenantDigest? digest = _producer.DeriveIdentityDigest(metadata.Identity);

            if (digest is not { } proven)
            {
                return UnprovenIdentity;
            }

            identity = metadata.Identity;

            return proven;
        }

        private async ValueTask<Result> RenameProvenChildAsync(
            IMarkerChildCapability? child,
            CovenantDigest expectedPhysicalIdentityDigest,
            ReadOnlyMemory<byte> expectedExactCodecBytes,
            string destinationLeaf,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _ = GetMarkerDirectory();

            Result<string> proven = await ProveChildAsync(
                child,
                expectedPhysicalIdentityDigest,
                expectedExactCodecBytes,
                cancellationToken);

            if (!proven.IsSuccess)
            {
                return proven.Error;
            }

            if (!OperatingSystem.IsWindows())
            {
                return RenameRelative(child!, proven.Value, destinationLeaf);
            }

            string source = Path.Combine(_markerDirectoryPath, proven.Value);

            string destination = Path.Combine(_markerDirectoryPath, destinationLeaf);

            // Refuse a destination that already exists before attempting the move, and let the move
            // itself refuse again. One check alone would be a race; the move alone is silent about which
            // object it replaced on platforms whose rename overwrites.
            if (FileHandleIdentityInterop.TryGetPathMetadataNoFollow(destination, out _))
            {
                return MarkerEvidenceRejected;
            }

            _producer.BeforeMarkerChildEffectForTests?.Invoke();

            try
            {
                File.Move(source, destination);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                return MarkerIoFailed;
            }

            child!.ReleaseFor(this);

            return Result.Success();
        }

        private async ValueTask<Result> CompareDeleteChildAsync(
            IMarkerChildCapability? child,
            CovenantDigest expectedPhysicalIdentityDigest,
            ReadOnlyMemory<byte> expectedExactCodecBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _ = GetMarkerDirectory();

            Result<string> proven = await ProveChildAsync(
                child,
                expectedPhysicalIdentityDigest,
                expectedExactCodecBytes,
                cancellationToken);

            if (!proven.IsSuccess)
            {
                return proven.Error;
            }

            if (!OperatingSystem.IsWindows())
            {
                return DeleteRelative(child!, proven.Value, expectedPhysicalIdentityDigest);
            }

            string path = Path.Combine(_markerDirectoryPath, proven.Value);

            // The delegated-deletion window: no portable unlink-by-handle exists, so the name is checked
            // against the identity that was just proven on the handle and then unlinked. Closing the
            // window is not possible here; narrowing it and refusing on any disagreement is (§10.17).
            if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out FileHandleMetadata named)
                || named.Kind is not FileSystemObjectKind.RegularFile
                || _producer.DeriveIdentityDigest(named.Identity) != expectedPhysicalIdentityDigest)
            {
                return MarkerEvidenceRejected;
            }

            _producer.BeforeMarkerChildEffectForTests?.Invoke();

            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                return MarkerIoFailed;
            }

            child!.ReleaseFor(this);

            return Result.Success();
        }

        /// <summary>
        /// The macOS and Linux rename: a no-replace <c>renameat</c> within the retained directory.
        /// </summary>
        private Result RenameRelative(
            IMarkerChildCapability child,
            string sourceLeaf,
            string destinationLeaf)
        {
            SafeFileHandle directory = GetMarkerDirectory();

            // Refuse a destination that already exists before attempting the move, and let the
            // no-replace move refuse again; both look only inside the retained directory.
            SecureFileOpenStatus destination = FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(
                directory,
                destinationLeaf,
                out SafeFileHandle? existing);

            existing?.Dispose();

            if (destination is not SecureFileOpenStatus.NotFound)
            {
                return MarkerEvidenceRejected;
            }

            _producer.BeforeMarkerChildEffectForTests?.Invoke();

            CampaignMarkerRenameStatus renamed = CampaignMarkerNativeMethods.TryRenameNoReplaceRelative(
                directory,
                sourceLeaf,
                destinationLeaf);

            if (renamed is not CampaignMarkerRenameStatus.Renamed)
            {
                return renamed is CampaignMarkerRenameStatus.DestinationExists
                    ? MarkerEvidenceRejected
                    : MarkerIoFailed;
            }

            child.ReleaseFor(this);

            return Result.Success();
        }

        /// <summary>
        /// The macOS and Linux compare-delete: the name is re-proven and unlinked, both relative to the
        /// retained directory.
        /// </summary>
        /// <remarks>
        /// Unix has no unlink-by-handle, so the window between the identity comparison and
        /// <c>unlinkat</c> is narrowed rather than closed, exactly as the journal retirement states it
        /// (§10.17); what it no longer has is a path a swapped directory could redirect.
        /// </remarks>
        private Result DeleteRelative(
            IMarkerChildCapability child,
            string leaf,
            CovenantDigest expectedPhysicalIdentityDigest)
        {
            SafeFileHandle directory = GetMarkerDirectory();

            SecureFileOpenStatus status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(
                directory,
                leaf,
                out SafeFileHandle? named);

            using (named)
            {
                if (status is not SecureFileOpenStatus.Success
                    || named is null
                    || !FileHandleIdentityInterop.TryGetHandleMetadata(named, out FileHandleMetadata metadata)
                    || metadata.Kind is not FileSystemObjectKind.RegularFile
                    || _producer.DeriveIdentityDigest(metadata.Identity) != expectedPhysicalIdentityDigest)
                {
                    return MarkerEvidenceRejected;
                }
            }

            _producer.BeforeMarkerChildEffectForTests?.Invoke();

            if (!CampaignMarkerNativeMethods.TryUnlinkRelative(directory, leaf))
            {
                return MarkerIoFailed;
            }

            child.ReleaseFor(this);

            return Result.Success();
        }

        private async ValueTask<Result<string>> ProveChildAsync(
            IMarkerChildCapability? child,
            CovenantDigest expectedPhysicalIdentityDigest,
            ReadOnlyMemory<byte> expectedExactCodecBytes,
            CancellationToken cancellationToken)
        {
            if (child is null || !expectedPhysicalIdentityDigest.IsValid)
            {
                return MarkerEvidenceRejected;
            }

            if (!MarkerDirectoryIsStillOurs())
            {
                return MarkerRootChanged;
            }

            return await child.ProveForAsync(
                this,
                expectedPhysicalIdentityDigest,
                expectedExactCodecBytes,
                cancellationToken);
        }
    }

    /// <summary>
    /// The owner-only seam a child capability answers on.
    /// </summary>
    /// <remarks>
    /// Internal to this producer and implemented only by the two nested child capabilities. It carries a
    /// verdict and a leaf the caller already supplied — never a handle, a stream, or a path — so a root
    /// capability can verify and act on a child without ever holding the child's descriptor.
    /// </remarks>
    private interface IMarkerChildCapability
    {
        ValueTask<Result<string>> ProveForAsync(
            MarkerRootCapability owner,
            CovenantDigest expectedPhysicalIdentityDigest,
            ReadOnlyMemory<byte> expectedExactCodecBytes,
            CancellationToken cancellationToken);

        void ReleaseFor(MarkerRootCapability owner);
    }

    /// <summary>
    /// One temporary file inside a proven marker directory.
    /// </summary>
    /// <remarks>
    /// A distinct type from <see cref="MarkerHandleCapability"/> rather than a flag on one type, so a
    /// half-written temporary can never be handed to an operation that expects the committed marker. The
    /// compiler enforces that separation for free; a boolean would not.
    /// </remarks>
    internal sealed class MarkerTemporaryHandleCapability : IAsyncDisposable, IMarkerChildCapability
    {
        private readonly MarkerRootCapability _owner;

        private readonly string _leaf;

        private readonly FileHandleIdentity _identity;

        private readonly bool _writable;

        private FileStream? _stream;

        private MarkerTemporaryHandleCapability(
            MarkerRootCapability owner,
            FileStream stream,
            string leaf,
            FileHandleIdentity identity,
            CovenantDigest physicalIdentityDigest,
            bool writable)
        {
            _owner = owner;

            _stream = stream;

            _leaf = leaf;

            _identity = identity;

            _writable = writable;

            PhysicalIdentityDigest = physicalIdentityDigest;
        }

        internal CovenantDigest PhysicalIdentityDigest { get; }

        internal long Length => RandomAccess.GetLength(GetStream().SafeFileHandle);

        /// <summary>
        /// The one call site of the private constructor, invoked only by a proven root capability.
        /// </summary>
        internal static MarkerTemporaryHandleCapability Adopt(
            MarkerRootCapability owner,
            FileStream stream,
            string leaf,
            FileHandleIdentity identity,
            CovenantDigest physicalIdentityDigest,
            bool writable) =>
            new(owner, stream, leaf, identity, physicalIdentityDigest, writable);

        /// <summary>
        /// Reads the whole file into one zeroizable lease, or refuses because it is larger than the bound.
        /// </summary>
        internal ValueTask<Result<MarkerCodecBytesLease>> ReadAllBoundedAsync(
            int maximumBytes,
            CancellationToken cancellationToken) =>
            ReadBoundedAsync(GetStream(), maximumBytes, cancellationToken);

        /// <summary>
        /// Replaces the file contents with exactly the supplied bytes.
        /// </summary>
        internal ValueTask<Result> WriteAllAsync(
            ReadOnlyMemory<byte> exactCodecBytes,
            CancellationToken cancellationToken) =>
            _writable
                ? WriteAllToAsync(GetStream(), exactCodecBytes, cancellationToken)
                : ValueTask.FromResult<Result>(MarkerEvidenceRejected);

        /// <summary>
        /// Forces the written bytes to stable storage.
        /// </summary>
        internal ValueTask<Result> FlushToDiskAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileStream stream = GetStream();

            if (!_writable)
            {
                return ValueTask.FromResult<Result>(MarkerEvidenceRejected);
            }

            try
            {
                stream.Flush(flushToDisk: true);

                return ValueTask.FromResult(Result.Success());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return ValueTask.FromResult<Result>(MarkerIoFailed);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();

            await ValueTask.CompletedTask;
        }

        async ValueTask<Result<string>> IMarkerChildCapability.ProveForAsync(
            MarkerRootCapability owner,
            CovenantDigest expectedPhysicalIdentityDigest,
            ReadOnlyMemory<byte> expectedExactCodecBytes,
            CancellationToken cancellationToken) =>
            await ProveChildForAsync(
                ReferenceEquals(_owner, owner),
                Volatile.Read(ref _stream),
                _leaf,
                _identity,
                PhysicalIdentityDigest,
                expectedPhysicalIdentityDigest,
                expectedExactCodecBytes,
                cancellationToken);

        void IMarkerChildCapability.ReleaseFor(MarkerRootCapability owner)
        {
            if (ReferenceEquals(_owner, owner))
            {
                Interlocked.Exchange(ref _stream, null)?.Dispose();
            }
        }

        private FileStream GetStream() =>
            Volatile.Read(ref _stream)
            ?? throw new ObjectDisposedException(nameof(MarkerTemporaryHandleCapability));
    }

    /// <summary>
    /// The committed marker file inside a proven marker directory.
    /// </summary>
    /// <remarks>
    /// Read-only by construction. Nothing in the protocol edits a marker in place — a change is always a
    /// fresh temporary renamed over the old one — so a writable marker handle would only ever be the way
    /// a partial write became the live marker.
    /// </remarks>
    internal sealed class MarkerHandleCapability : IAsyncDisposable, IMarkerChildCapability
    {
        private readonly MarkerRootCapability _owner;

        private readonly FileHandleIdentity _identity;

        private FileStream? _stream;

        private MarkerHandleCapability(
            MarkerRootCapability owner,
            FileStream stream,
            FileHandleIdentity identity,
            CovenantDigest physicalIdentityDigest)
        {
            _owner = owner;

            _stream = stream;

            _identity = identity;

            PhysicalIdentityDigest = physicalIdentityDigest;
        }

        internal CovenantDigest PhysicalIdentityDigest { get; }

        internal long Length => RandomAccess.GetLength(GetStream().SafeFileHandle);

        /// <summary>
        /// The one call site of the private constructor, invoked only by a proven root capability.
        /// </summary>
        internal static MarkerHandleCapability Adopt(
            MarkerRootCapability owner,
            FileStream stream,
            FileHandleIdentity identity,
            CovenantDigest physicalIdentityDigest) =>
            new(owner, stream, identity, physicalIdentityDigest);

        /// <summary>
        /// Reads the whole marker into one zeroizable lease, or refuses an over-long file.
        /// </summary>
        internal ValueTask<Result<MarkerCodecBytesLease>> ReadAllBoundedAsync(
            int maximumBytes,
            CancellationToken cancellationToken) =>
            ReadBoundedAsync(GetStream(), maximumBytes, cancellationToken);

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();

            await ValueTask.CompletedTask;
        }

        async ValueTask<Result<string>> IMarkerChildCapability.ProveForAsync(
            MarkerRootCapability owner,
            CovenantDigest expectedPhysicalIdentityDigest,
            ReadOnlyMemory<byte> expectedExactCodecBytes,
            CancellationToken cancellationToken) =>
            await ProveChildForAsync(
                ReferenceEquals(_owner, owner),
                Volatile.Read(ref _stream),
                MarkerFileLeaf,
                _identity,
                PhysicalIdentityDigest,
                expectedPhysicalIdentityDigest,
                expectedExactCodecBytes,
                cancellationToken);

        void IMarkerChildCapability.ReleaseFor(MarkerRootCapability owner)
        {
            if (ReferenceEquals(_owner, owner))
            {
                Interlocked.Exchange(ref _stream, null)?.Dispose();
            }
        }

        private FileStream GetStream() =>
            Volatile.Read(ref _stream)
            ?? throw new ObjectDisposedException(nameof(MarkerHandleCapability));
    }

    /// <summary>
    /// The only mutable copy of a marker's bytes, cleared exactly once on release.
    /// </summary>
    /// <remarks>
    /// A lease rather than a returned array because the lifecycle disposes it in <c>finally</c>: parse
    /// failure, byte mismatch, cancellation, and a faulted rename all have to leave the same cleared
    /// buffer behind. Handing back a plain array would make the clearing the caller's problem on exactly
    /// the paths where the caller is already unwinding.
    /// </remarks>
    internal sealed class MarkerCodecBytesLease : IDisposable
    {
        private readonly int _length;

        private byte[]? _buffer;

        private MarkerCodecBytesLease(byte[] buffer, int length)
        {
            _buffer = buffer;

            _length = length;
        }

        /// <summary>
        /// The one call site of the private constructor, invoked only by a bounded read.
        /// </summary>
        internal static MarkerCodecBytesLease Adopt(byte[] buffer, int length) =>
            new(buffer, length);

        internal ReadOnlyMemory<byte> Bytes =>
            Volatile.Read(ref _buffer) is { } buffer
                ? buffer.AsMemory(0, _length)
                : ReadOnlyMemory<byte>.Empty;

        /// <inheritdoc />
        public void Dispose()
        {
            byte[]? buffer = Interlocked.Exchange(ref _buffer, null);

            if (buffer is not null)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private static async ValueTask<Result<MarkerCodecBytesLease>> ReadBoundedAsync(
        FileStream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (maximumBytes <= 0 || maximumBytes > CampaignPathMarkerPolicy.MaximumMarkerByteCount)
        {
            return MarkerLeafRejected;
        }

        long length;

        try
        {
            length = RandomAccess.GetLength(stream.SafeFileHandle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return MarkerIoFailed;
        }

        // Over-long is a refusal, not a truncation. A file read down to the bound could authenticate as
        // a marker whose trailing bytes nobody ever saw.
        if (length > maximumBytes)
        {
            return MarkerEvidenceRejected;
        }

        byte[] buffer = new byte[(int)length];

        int read = 0;

        try
        {
            while (read < buffer.Length)
            {
                int chunk = await RandomAccess.ReadAsync(
                    stream.SafeFileHandle,
                    buffer.AsMemory(read),
                    read,
                    cancellationToken);

                if (chunk == 0)
                {
                    break;
                }

                read += chunk;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CryptographicOperations.ZeroMemory(buffer);

            return MarkerIoFailed;
        }

        if (read != buffer.Length)
        {
            CryptographicOperations.ZeroMemory(buffer);

            return MarkerEvidenceRejected;
        }

        return MarkerCodecBytesLease.Adopt(buffer, read);
    }

    private static async ValueTask<Result> WriteAllToAsync(
        FileStream stream,
        ReadOnlyMemory<byte> exactCodecBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (exactCodecBytes.IsEmpty
            || exactCodecBytes.Length > CampaignPathMarkerPolicy.MaximumMarkerByteCount)
        {
            return MarkerEvidenceRejected;
        }

        try
        {
            RandomAccess.SetLength(stream.SafeFileHandle, 0);

            await RandomAccess.WriteAsync(
                stream.SafeFileHandle,
                exactCodecBytes,
                fileOffset: 0,
                cancellationToken);

            return Result.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return MarkerIoFailed;
        }
    }

    private static async ValueTask<Result<string>> ProveChildForAsync(
        bool belongsToOwner,
        FileStream? stream,
        string leaf,
        FileHandleIdentity identity,
        CovenantDigest physicalIdentityDigest,
        CovenantDigest expectedPhysicalIdentityDigest,
        ReadOnlyMemory<byte> expectedExactCodecBytes,
        CancellationToken cancellationToken)
    {
        // Belonging first. Matching evidence under a different retained root is still someone else's
        // file, and checking the evidence first would make that the deciding factor.
        if (!belongsToOwner
            || stream is null
            || !physicalIdentityDigest.IsValid
            || physicalIdentityDigest != expectedPhysicalIdentityDigest
            || expectedExactCodecBytes.IsEmpty)
        {
            return MarkerEvidenceRejected;
        }

        if (!SecureFileReader.TryValidateRegularFileHandle(
                stream.SafeFileHandle,
                identity,
                out _))
        {
            return MarkerEvidenceRejected;
        }

        Result<MarkerCodecBytesLease> read = await ReadBoundedAsync(
            stream,
            expectedExactCodecBytes.Length,
            cancellationToken);

        if (!read.IsSuccess)
        {
            return read.Error;
        }

        MarkerCodecBytesLease lease = read.Value;

        try
        {
            return lease.Bytes.Length == expectedExactCodecBytes.Length
                && CryptographicOperations.FixedTimeEquals(
                    lease.Bytes.Span,
                    expectedExactCodecBytes.Span)
                ? leaf
                : MarkerEvidenceRejected;
        }
        finally
        {
            lease.Dispose();
        }
    }
}

/// <summary>
/// What was found where a Campaign marker belongs.
/// </summary>
/// <remarks>
/// Closed and nonserializable, with exactly two arms. There is deliberately no third arm for "something
/// is there but it is not a marker": a symlink or a directory at the marker leaf is a failure, not an
/// absence, because reporting absence would invite the caller to create a marker straight through it.
/// </remarks>
internal abstract record PhysicalCampaignMarkerOpenResult
{
    private PhysicalCampaignMarkerOpenResult()
    {
    }

    /// <summary>No object answers to the marker leaf.</summary>
    internal sealed record Absent : PhysicalCampaignMarkerOpenResult;

    /// <summary>A real single-link regular file was opened and is retained.</summary>
    internal sealed record Opened(
        PhysicalCampaignRootOpener.MarkerHandleCapability Marker)
        : PhysicalCampaignMarkerOpenResult;
}

/// <summary>The outcome of a no-replace rename inside a retained marker directory.</summary>
internal enum CampaignMarkerRenameStatus : byte
{
    Renamed = 1,

    DestinationExists = 2,

    Failed = 3,
}

/// <summary>
/// The native calls the marker protocol needs and no shared helper already provides.
/// </summary>
/// <remarks>
/// Kept beside its only caller rather than added to the shared identity interop, because every entry
/// exists for the marker protocol specifically — its durability barrier, its ownership check, and its
/// retained-directory create, rename, and unlink — and a shared surface invites a caller who wants
/// "an fsync" to reach for one on a handle nobody proved.
/// </remarks>
internal static partial class CampaignMarkerNativeMethods
{
    /// <summary>
    /// Flushes a retained directory handle, or reports that the platform cannot prove it.
    /// </summary>
    /// <remarks>
    /// The shared <see cref="Storage.DurableDirectoryFlush"/> issues <c>F_FULLFSYNC</c> on macOS, where a
    /// plain <c>fsync</c> leaves the entry in the drive's cache. Windows exposes no directory-handle
    /// flush and journals directory metadata itself, so the barrier is satisfied there rather than
    /// demonstrated. That distinction is stated rather than papered over: a call that silently returned
    /// success on a platform where it does nothing would let the rename phase claim a durability
    /// guarantee it never obtained (§10.17).
    /// </remarks>
    internal static bool TryFlushDirectory(SafeFileHandle directory) =>
        Storage.DurableDirectoryFlush.TryFlush(directory);

    /// <summary>
    /// Reports whether the calling process owns the object at the supplied path.
    /// </summary>
    /// <remarks>
    /// Windows returns <see langword="true"/>: it has no owner-uid equivalent this check could read, and
    /// the marker directory there is guarded by the inherited owner ACL rather than by a mode bit.
    /// </remarks>
    internal static bool OwnsPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        return FileHandleIdentityInterop.TryGetUnixOwnerUserId(path, out uint owner)
            && owner == GetEffectiveUserId();
    }

    /// <summary>
    /// Creates one new owner-only regular file relative to a retained directory, or returns
    /// <see langword="null"/> when anything at all already answers to the leaf.
    /// </summary>
    internal static SafeFileHandle? TryCreateExclusiveRelative(SafeFileHandle directory, string leaf)
    {
        if (OperatingSystem.IsWindows() || !UsableDirectory(directory))
        {
            return null;
        }

        bool referenced = false;

        try
        {
            directory.DangerousAddRef(ref referenced);

            int flags = OperatingSystem.IsMacOS()
                ? MacOpenReadWrite | MacOpenCreate | MacOpenExclusive | MacOpenNoFollow | MacOpenCloseOnExec
                : LinuxOpenReadWrite | LinuxOpenCreate | LinuxOpenExclusive | LinuxOpenNoFollow | LinuxOpenCloseOnExec;

            int descriptor = OpenAtUnix(
                directory.DangerousGetHandle().ToInt32(),
                leaf,
                flags,
                OwnerReadWriteMode);

            if (descriptor < 0)
            {
                return null;
            }

            SafeFileHandle created = new(new IntPtr(descriptor), ownsHandle: true);

            // Belt and braces on the mode, as the path-based create applied it; and the exclusive
            // advisory lock a FileShare.None stream takes on Unix.
            if (Fchmod(descriptor, OwnerReadWriteMode) != 0
                || Flock(descriptor, LockExclusive | LockNonBlocking) != 0)
            {
                created.Dispose();

                return null;
            }

            return created;
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException
                or DllNotFoundException
                or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            if (referenced)
            {
                directory.DangerousRelease();
            }
        }
    }

    /// <summary>Renames one child to another leaf of the same retained directory, replacing nothing.</summary>
    internal static CampaignMarkerRenameStatus TryRenameNoReplaceRelative(
        SafeFileHandle directory,
        string sourceLeaf,
        string destinationLeaf)
    {
        if (OperatingSystem.IsWindows() || !UsableDirectory(directory))
        {
            return CampaignMarkerRenameStatus.Failed;
        }

        bool referenced = false;

        try
        {
            directory.DangerousAddRef(ref referenced);

            int descriptor = directory.DangerousGetHandle().ToInt32();

            int result = OperatingSystem.IsMacOS()
                ? RenameAtXMac(descriptor, sourceLeaf, descriptor, destinationLeaf, MacRenameExclusive)
                : RenameAt2(descriptor, sourceLeaf, descriptor, destinationLeaf, LinuxRenameNoReplace);

            if (result == 0)
            {
                return CampaignMarkerRenameStatus.Renamed;
            }

            return Marshal.GetLastPInvokeError() == ErrorExists
                ? CampaignMarkerRenameStatus.DestinationExists
                : CampaignMarkerRenameStatus.Failed;
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException
                or DllNotFoundException
                or ObjectDisposedException)
        {
            return CampaignMarkerRenameStatus.Failed;
        }
        finally
        {
            if (referenced)
            {
                directory.DangerousRelease();
            }
        }
    }

    /// <summary>Unlinks one regular-file leaf of a retained directory.</summary>
    internal static bool TryUnlinkRelative(SafeFileHandle directory, string leaf)
    {
        if (OperatingSystem.IsWindows() || !UsableDirectory(directory))
        {
            return false;
        }

        bool referenced = false;

        try
        {
            directory.DangerousAddRef(ref referenced);

            return UnlinkAt(directory.DangerousGetHandle().ToInt32(), leaf, flags: 0) == 0;
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException
                or DllNotFoundException
                or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (referenced)
            {
                directory.DangerousRelease();
            }
        }
    }

    private static bool UsableDirectory(SafeFileHandle directory) =>
        directory is { IsInvalid: false, IsClosed: false }
        && (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux());

    private const int OwnerReadWriteMode = 0x180;

    private const int MacOpenReadWrite = 0x00000002;

    private const int MacOpenNoFollow = 0x00000100;

    private const int MacOpenCreate = 0x00000200;

    private const int MacOpenExclusive = 0x00000800;

    private const int MacOpenCloseOnExec = 0x01000000;

    private const int LinuxOpenReadWrite = 0x00000002;

    private const int LinuxOpenCreate = 0x00000040;

    private const int LinuxOpenExclusive = 0x00000080;

    private const int LinuxOpenNoFollow = 0x00020000;

    private const int LinuxOpenCloseOnExec = 0x00080000;

    private const uint MacRenameExclusive = 0x00000004;

    private const int LinuxRenameNoReplace = 1;

    private const int ErrorExists = 17;

    private const int LockExclusive = 2;

    private const int LockNonBlocking = 4;

    /// <summary>
    /// <c>openat</c> is variadic in <c>mode</c>; Apple's arm64 ABI passes a variadic argument on the
    /// stack, so the mode is spilled past the eight argument registers there, exactly as the journal
    /// primitives do.
    /// </summary>
    private static int OpenAtUnix(int directory, string path, int flags, int mode) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? OpenAtAppleArm64(directory, path, flags, 0, 0, 0, 0, 0, mode)
            : OpenAtFixedArity(directory, path, flags, mode);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtFixedArity(int directory, string path, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtAppleArm64(
        int directory,
        string path,
        int flags,
        int registerFiller3,
        int registerFiller4,
        int registerFiller5,
        int registerFiller6,
        int registerFiller7,
        int mode);

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt2(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath,
        int flags);

    [LibraryImport("libc", EntryPoint = "renameatx_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAtXMac(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath,
        uint flags);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkAt(int directory, string path, int flags);

    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int Fchmod(int descriptor, int mode);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(int descriptor, int operation);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
