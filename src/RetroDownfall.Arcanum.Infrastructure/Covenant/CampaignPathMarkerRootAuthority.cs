using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.TheForge;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The single way to obtain a <see cref="CampaignPathMarkerRootAuthority"/>.
/// </summary>
/// <remarks>
/// One method, one implementation, and that implementation is private to the authority it produces. The
/// alternative — a public constructor, a <c>Create</c> helper, or a DI registration — would mean any
/// Infrastructure type could mint an object that grants filesystem effects over a Campaign root, and
/// the identity proof in front of it would be advice rather than a gate (§10.12).
/// </remarks>
internal interface ICampaignPathMarkerRootAuthorityFactory
{

    /// <summary>
    /// Opens one Campaign root for marker work, proving its identity echo before granting anything.
    /// </summary>
    ValueTask<Result<CampaignPathMarkerRootAuthority>> OpenAsync(
        PhysicalCampaignRootOpener opener,
        Guid campaignId,
        long pathRevision,
        CovenantDigest expectedPhysicalIdentityDigest,
        string canonicalDisplayPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens one Campaign root while requiring its marker directory to exist already.
    /// </summary>
    ValueTask<Result<CampaignPathMarkerRootAuthority>> OpenExistingAsync(
        PhysicalCampaignRootOpener opener,
        Guid campaignId,
        long pathRevision,
        CovenantDigest expectedPhysicalIdentityDigest,
        string canonicalDisplayPath,
        CancellationToken cancellationToken);

}

/// <summary>
/// A live Campaign root, forwarding only typed marker operations through retained handles.
/// </summary>
/// <remarks>
/// Nonserializable, factory-constructed, and deliberately thin: it holds one retained
/// <see cref="PhysicalCampaignRootOpener.MarkerRootCapability"/> and forwards. There is no raw-handle
/// accessor, no path-reopen method, no second interface to downcast through, and no way to construct one
/// without passing the sealed producer's own identity check. A caller that reached the underlying stream
/// could write the marker directly, and every phase ordering the crash-recovery protocol depends on
/// would become advisory (§10.12).
///
/// <para>The identity echo is rechecked here even though the producer already checked it. The producer
/// answers "is this the directory you named"; this factory answers "is this the directory the caller
/// asked me to grant authority over", and a capability that failed the second question is disposed
/// rather than returned — exactly once, on the failure path as well as the success path.</para>
/// </remarks>
internal sealed class CampaignPathMarkerRootAuthority : IAsyncDisposable
{

    private PhysicalCampaignRootOpener.MarkerRootCapability? _retainedCapability;

    private CampaignPathMarkerRootAuthority(
        Guid campaignId,
        long pathRevision,
        CovenantDigest physicalIdentityDigest,
        PhysicalCampaignRootOpener.MarkerRootCapability retainedCapability)
    {

        CampaignId = campaignId;

        PathRevision = pathRevision;

        PhysicalIdentityDigest = physicalIdentityDigest;

        _retainedCapability = retainedCapability;

    }

    public Guid CampaignId { get; }

    public long PathRevision { get; }

    public CovenantDigest PhysicalIdentityDigest { get; }

    /// <summary>The only factory, private to this type so nothing else can implement or replace it.</summary>
    internal static ICampaignPathMarkerRootAuthorityFactory Instance { get; } = new Factory();

    /// <inheritdoc cref="PhysicalCampaignRootOpener.MarkerRootCapability.CreateTemporaryExclusiveNoFollowAsync" />
    internal ValueTask<Result<PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability>>
        CreateTemporaryExclusiveNoFollowAsync(
            string temporaryLeaf,
            CancellationToken cancellationToken) =>
        GetRetainedCapability().CreateTemporaryExclusiveNoFollowAsync(
            temporaryLeaf,
            cancellationToken);

    /// <inheritdoc cref="PhysicalCampaignRootOpener.MarkerRootCapability.OpenTemporaryNoFollowAsync" />
    internal ValueTask<Result<PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability>>
        OpenTemporaryNoFollowAsync(
            string temporaryLeaf,
            CancellationToken cancellationToken) =>
        GetRetainedCapability().OpenTemporaryNoFollowAsync(
            temporaryLeaf,
            cancellationToken);

    /// <inheritdoc cref="PhysicalCampaignRootOpener.MarkerRootCapability.RenameTemporaryToMarkerNoReplaceAsync" />
    internal ValueTask<Result> RenameTemporaryToMarkerNoReplaceAsync(
        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary,
        CovenantDigest expectedTemporaryPhysicalIdentityDigest,
        ReadOnlyMemory<byte> expectedExactCodecBytes,
        CancellationToken cancellationToken) =>
        GetRetainedCapability().RenameTemporaryToMarkerNoReplaceAsync(
            temporary,
            expectedTemporaryPhysicalIdentityDigest,
            expectedExactCodecBytes,
            cancellationToken);

    /// <inheritdoc cref="PhysicalCampaignRootOpener.MarkerRootCapability.CompareDeleteTemporaryAsync" />
    internal ValueTask<Result> CompareDeleteTemporaryAsync(
        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary,
        CovenantDigest expectedTemporaryPhysicalIdentityDigest,
        ReadOnlyMemory<byte> expectedExactCodecBytes,
        CancellationToken cancellationToken) =>
        GetRetainedCapability().CompareDeleteTemporaryAsync(
            temporary,
            expectedTemporaryPhysicalIdentityDigest,
            expectedExactCodecBytes,
            cancellationToken);

    /// <inheritdoc cref="PhysicalCampaignRootOpener.MarkerRootCapability.OpenMarkerOrProveAbsentNoFollowAsync" />
    internal ValueTask<Result<PhysicalCampaignMarkerOpenResult>>
        OpenMarkerOrProveAbsentNoFollowAsync(
            CancellationToken cancellationToken) =>
        GetRetainedCapability().OpenMarkerOrProveAbsentNoFollowAsync(
            cancellationToken);

    /// <inheritdoc cref="PhysicalCampaignRootOpener.MarkerRootCapability.CompareDeleteMarkerAsync" />
    internal ValueTask<Result> CompareDeleteMarkerAsync(
        PhysicalCampaignRootOpener.MarkerHandleCapability marker,
        CovenantDigest expectedMarkerPhysicalIdentityDigest,
        ReadOnlyMemory<byte> expectedExactCodecBytes,
        CancellationToken cancellationToken) =>
        GetRetainedCapability().CompareDeleteMarkerAsync(
            marker,
            expectedMarkerPhysicalIdentityDigest,
            expectedExactCodecBytes,
            cancellationToken);

    /// <inheritdoc cref="PhysicalCampaignRootOpener.MarkerRootCapability.RenameMarkerToQuarantineNoReplaceAsync" />
    internal ValueTask<Result> RenameMarkerToQuarantineNoReplaceAsync(
        PhysicalCampaignRootOpener.MarkerHandleCapability marker,
        string quarantineLeaf,
        CovenantDigest expectedMarkerPhysicalIdentityDigest,
        ReadOnlyMemory<byte> expectedExactCodecBytes,
        CancellationToken cancellationToken) =>
        GetRetainedCapability().RenameMarkerToQuarantineNoReplaceAsync(
            marker,
            quarantineLeaf,
            expectedMarkerPhysicalIdentityDigest,
            expectedExactCodecBytes,
            cancellationToken);

    /// <inheritdoc cref="PhysicalCampaignRootOpener.MarkerRootCapability.FlushMarkerDirectoryAsync" />
    internal ValueTask<Result> FlushMarkerDirectoryAsync(
        CancellationToken cancellationToken) =>
        GetRetainedCapability().FlushMarkerDirectoryAsync(cancellationToken);

    /// <summary>
    /// Detaches and releases the retained capability set exactly once.
    /// </summary>
    /// <remarks>
    /// The exchange is what makes it exactly once. A second disposal that reran the release would close
    /// a descriptor number the process may have already handed to something else, and the failure would
    /// surface far from here as an unrelated file being read or truncated.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {

        PhysicalCampaignRootOpener.MarkerRootCapability? capability =
            Interlocked.Exchange(ref _retainedCapability, null);

        if (capability is not null)
        {
            await capability.DisposeAsync();
        }

    }

    private PhysicalCampaignRootOpener.MarkerRootCapability GetRetainedCapability() =>
        Volatile.Read(ref _retainedCapability)
        ?? throw new ObjectDisposedException(nameof(CampaignPathMarkerRootAuthority));

    private sealed class Factory : ICampaignPathMarkerRootAuthorityFactory
    {

        public async ValueTask<Result<CampaignPathMarkerRootAuthority>> OpenAsync(
            PhysicalCampaignRootOpener opener,
            Guid campaignId,
            long pathRevision,
            CovenantDigest expectedPhysicalIdentityDigest,
            string canonicalDisplayPath,
            CancellationToken cancellationToken) =>
            await OpenAsync(
                opener,
                campaignId,
                pathRevision,
                expectedPhysicalIdentityDigest,
                canonicalDisplayPath,
                requireExistingMarkerDirectory: false,
                cancellationToken).ConfigureAwait(false);

        public async ValueTask<Result<CampaignPathMarkerRootAuthority>> OpenExistingAsync(
            PhysicalCampaignRootOpener opener,
            Guid campaignId,
            long pathRevision,
            CovenantDigest expectedPhysicalIdentityDigest,
            string canonicalDisplayPath,
            CancellationToken cancellationToken) =>
            await OpenAsync(
                opener,
                campaignId,
                pathRevision,
                expectedPhysicalIdentityDigest,
                canonicalDisplayPath,
                requireExistingMarkerDirectory: true,
                cancellationToken).ConfigureAwait(false);

        private static async ValueTask<Result<CampaignPathMarkerRootAuthority>> OpenAsync(
            PhysicalCampaignRootOpener opener,
            Guid campaignId,
            long pathRevision,
            CovenantDigest expectedPhysicalIdentityDigest,
            string canonicalDisplayPath,
            bool requireExistingMarkerDirectory,
            CancellationToken cancellationToken)
        {

            if (opener is null
                || campaignId == Guid.Empty
                || pathRevision <= 0
                || !expectedPhysicalIdentityDigest.IsValid
                || string.IsNullOrWhiteSpace(canonicalDisplayPath))
            {
                return new Error(
                    ErrorCodes.Campaign.InvalidPath,
                    "Invalid Campaign root authority request.");
            }

            Result<PhysicalCampaignRootOpener.MarkerRootCapability> opened =
                await (requireExistingMarkerDirectory
                    ? opener.OpenExistingForMarkerLifecycleAsync(
                        campaignId,
                        pathRevision,
                        expectedPhysicalIdentityDigest,
                        canonicalDisplayPath,
                        cancellationToken)
                    : opener.OpenForMarkerLifecycleAsync(
                        campaignId,
                        pathRevision,
                        expectedPhysicalIdentityDigest,
                        canonicalDisplayPath,
                        cancellationToken));

            if (!opened.IsSuccess)
            {
                return opened.Error;
            }

            PhysicalCampaignRootOpener.MarkerRootCapability capability = opened.Value;

            if (capability.CampaignId != campaignId
                || capability.PathRevision != pathRevision
                || capability.PhysicalIdentityDigest != expectedPhysicalIdentityDigest)
            {

                await capability.DisposeAsync();

                return new Error(
                    ErrorCodes.Campaign.InvalidPath,
                    "Campaign root authority identity mismatch.");

            }

            return new CampaignPathMarkerRootAuthority(
                campaignId,
                pathRevision,
                expectedPhysicalIdentityDigest,
                capability);

        }

    }

}
