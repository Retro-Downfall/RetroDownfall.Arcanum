using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Where one Arcanum-owned workspace file lives, expressed so that no caller can supply it.
/// </summary>
/// <remarks>
/// Every field is copied from the producer's own durable row before the first syscall. The location
/// is a canonical root identity plus a bounded, normalized relative walk — never a path string —
/// because a path is exactly the value a confused or hostile request would substitute to aim a
/// deleter at a file Arcanum never created (§10.17).
///
/// <para><paramref name="PathRevision"/> travels with the root identity because a Campaign's physical
/// root can legitimately be re-registered. A location recorded against revision 3 says nothing about
/// where the workspace is at revision 4, so a revision change stops the walk rather than following
/// the same relative segments into a different directory.</para>
/// </remarks>
public sealed record ManagedFileDurableLocationEvidence
{

    /// <summary>The deepest relative walk a managed file may live at below its Campaign root.</summary>
    public const int MaxParentSegments = 64;

    /// <summary>The longest single path component this evidence will carry.</summary>
    public const int MaxComponentLength = 255;

    private readonly ImmutableArray<string> _normalizedParentSegments;

    public ManagedFileDurableLocationEvidence(
        CovenantDigest campaignRootIdentityDigest,
        long pathRevision,
        ImmutableArray<string> normalizedParentSegments,
        CovenantDigest parentPhysicalIdentityDigest,
        string targetLeaf)
    {

        CampaignRootIdentityDigest = CovenantValidation.RequireDigest(
            campaignRootIdentityDigest,
            nameof(campaignRootIdentityDigest));

        PathRevision = CovenantValidation.RequirePositive(pathRevision, nameof(pathRevision));

        ParentPhysicalIdentityDigest = CovenantValidation.RequireDigest(
            parentPhysicalIdentityDigest,
            nameof(parentPhysicalIdentityDigest));

        if (normalizedParentSegments.IsDefault || normalizedParentSegments.Length > MaxParentSegments)
        {

            throw new ArgumentOutOfRangeException(nameof(normalizedParentSegments));

        }

        foreach (string segment in normalizedParentSegments)
        {

            RequireComponent(segment, nameof(normalizedParentSegments));

        }

        _normalizedParentSegments = normalizedParentSegments;

        TargetLeaf = RequireComponent(targetLeaf, nameof(targetLeaf));

    }

    /// <summary>The opaque keyed identity of the Campaign's physical root directory.</summary>
    public CovenantDigest CampaignRootIdentityDigest { get; }

    /// <summary>The Campaign path revision this walk was recorded against.</summary>
    public long PathRevision { get; }

    /// <summary>The bounded, normalized relative walk from the root to the file's parent.</summary>
    public ImmutableArray<string> NormalizedParentSegments => _normalizedParentSegments;

    /// <summary>The physical identity of the parent, captured from the same retained handle.</summary>
    public CovenantDigest ParentPhysicalIdentityDigest { get; }

    /// <summary>The file's own name inside that parent.</summary>
    public string TargetLeaf { get; }

    /// <summary>
    /// Structural equality over the walk, not reference equality over its backing array.
    /// </summary>
    /// <remarks>
    /// The compiler-generated comparison would use the default comparer for
    /// <see cref="ImmutableArray{T}"/>, which compares the underlying array by reference. Two
    /// locations decoded from the same durable row would then be unequal, and a recovery pass that
    /// compared a copied location against the producer's would refuse work it should have adopted.
    /// </remarks>
    public bool Equals(ManagedFileDurableLocationEvidence? other) =>
        other is not null
        && CampaignRootIdentityDigest == other.CampaignRootIdentityDigest
        && PathRevision == other.PathRevision
        && ParentPhysicalIdentityDigest == other.ParentPhysicalIdentityDigest
        && string.Equals(TargetLeaf, other.TargetLeaf, StringComparison.Ordinal)
        && _normalizedParentSegments.AsSpan().SequenceEqual(other._normalizedParentSegments.AsSpan());

    public override int GetHashCode()
    {

        HashCode hash = new();

        hash.Add(CampaignRootIdentityDigest);

        hash.Add(PathRevision);

        hash.Add(ParentPhysicalIdentityDigest);

        hash.Add(TargetLeaf, StringComparer.Ordinal);

        foreach (string segment in _normalizedParentSegments)
        {

            hash.Add(segment, StringComparer.Ordinal);

        }

        return hash.ToHashCode();

    }

    /// <summary>
    /// Rejects any component that is not a single, ordinary, relative name.
    /// </summary>
    /// <remarks>
    /// Separators, traversal, drive-relative prefixes, control characters, and trailing dots or spaces
    /// are all refused rather than sanitized. Sanitizing produces a different path than the producer
    /// recorded, and a deleter that quietly corrects its own location has stopped proving anything.
    /// </remarks>
    internal static string RequireComponent(string value, string parameterName)
    {

        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);

        if (value.Length > MaxComponentLength)
        {

            throw new ArgumentOutOfRangeException(parameterName);

        }

        if (value is "." or ".."
            || value.EndsWith('.')
            || value.EndsWith(' ')
            || value.StartsWith(' '))
        {

            throw new ArgumentException(
                "A managed-file path component must be an ordinary relative name.",
                parameterName);

        }

        foreach (char character in value)
        {

            if (character is '/' or '\\' or ':' || char.IsControl(character))
            {

                throw new ArgumentException(
                    "A managed-file path component cannot contain a separator, a drive marker, or a control character.",
                    parameterName);

            }

        }

        return value;

    }

}

/// <summary>
/// The producer-side location, which additionally names the temporary leaf a write stages through.
/// </summary>
/// <remarks>
/// A separate type rather than a nullable field on the durable location, so the temporary leaf is
/// structurally unable to reach an erasure request. A deleter handed a temporary name would unlink a
/// file that is either mid-write or already renamed away, and in both cases it would be deleting
/// something other than the artifact it was asked to erase.
/// </remarks>
public sealed record ManagedFileWriteDurableLocationEvidence
{

    public ManagedFileWriteDurableLocationEvidence(
        ManagedFileDurableLocationEvidence target,
        string temporaryLeaf)
    {

        Target = target ?? throw new ArgumentNullException(nameof(target));

        TemporaryLeaf = ManagedFileDurableLocationEvidence.RequireComponent(
            temporaryLeaf,
            nameof(temporaryLeaf));

        if (string.Equals(temporaryLeaf, target.TargetLeaf, StringComparison.Ordinal))
        {

            throw new ArgumentException(
                "A managed write's temporary leaf must differ from its target leaf.",
                nameof(temporaryLeaf));

        }

    }

    /// <summary>The final location the write adopts.</summary>
    public ManagedFileDurableLocationEvidence Target { get; }

    /// <summary>The distinct staging name under the same parent.</summary>
    public string TemporaryLeaf { get; }

}

/// <summary>
/// What the producer proved about the file it adopted.
/// </summary>
/// <remarks>
/// Three independent facts, all compared before a delete. Physical identity alone would accept a
/// same-inode file whose contents were replaced; a content hash alone would accept a same-content
/// file that somebody else created at the same path. Requiring both, plus the exact byte length,
/// means a deletion happens only when the file on disk is the file the database claims to own.
/// </remarks>
public sealed record ManagedFileOwnershipEvidence
{

    public ManagedFileOwnershipEvidence(
        CovenantDigest physicalIdentityDigest,
        CovenantDigest contentHash,
        long contentLength)
    {

        PhysicalIdentityDigest = CovenantValidation.RequireDigest(
            physicalIdentityDigest,
            nameof(physicalIdentityDigest));

        ContentHash = CovenantValidation.RequireDigest(contentHash, nameof(contentHash));

        ContentLength = contentLength >= 0
            ? contentLength
            : throw new ArgumentOutOfRangeException(nameof(contentLength));

    }

    /// <summary>The opaque keyed identity of the adopted file itself.</summary>
    public CovenantDigest PhysicalIdentityDigest { get; }

    /// <summary>The full content hash of the adopted bytes.</summary>
    public CovenantDigest ContentHash { get; }

    /// <summary>The exact adopted byte length.</summary>
    public long ContentLength { get; }

}
