using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.TheForge;

/// <summary>
/// Turns a supplied directory into the bounded set of opaque physical identities it could belong to.
/// </summary>
/// <remarks>
/// This is the type that makes Campaign resolution immune to path confusion. It never compares path
/// text. It resolves the directory and each of its ancestors with a no-follow metadata query and
/// derives a keyed identity from the volume and file identifiers the operating system reported for the
/// object it actually opened (§10.12).
///
/// <para>That single choice is what rejects the whole confusion family at once. A symlink or reparse
/// point resolves to the identity of the link, not the target. A rename keeps the identity, so a moved
/// root is still recognised while a <em>different</em> directory occupying the old path is not. A
/// delete-and-recreate produces a new inode and therefore a new identity, so a recreated directory does
/// not inherit the deleted one's authority. A copied marker file changes nothing, because the marker is
/// not what is being identified. And <c>/work/app</c> can never match <c>/work/app-legacy</c>, because
/// no prefix comparison happens anywhere.</para>
///
/// <para>Inode reuse is the one case identity alone cannot separate, which is why the identity is keyed
/// and why registration also records a revision the turn captures and later re-verifies. Reuse
/// produces a colliding identity only for an attacker who can also predict when the operating system
/// will recycle a specific inode inside a directory Arcanum already trusts.</para>
///
/// <para>Ancestors are walked from the supplied directory upward and bounded. The walk stops at the
/// volume root, and never crosses into a parent on a different volume: a bind mount or a mounted
/// filesystem is a boundary, not a path segment.</para>
/// </remarks>
/// <remarks>
/// Declared <see langword="partial"/> so the marker-lifecycle capabilities live in their own file
/// without becoming a second producer. The capability types are nested here because only this class may
/// construct them, and only this class holds the identity key their digests are derived from — a
/// sibling type that could mint a root capability would be a second answer to "who owns this
/// directory", and the whole point of physical identity is that there is exactly one.
/// </remarks>
internal sealed partial class PhysicalCampaignRootOpener(ICampaignRootIdentityKeyProvider keys)
{

    private static readonly byte[] LabelBytes =
        Encoding.UTF8.GetBytes(CampaignPathIdentityPolicy.Label);

    private readonly ICampaignRootIdentityKeyProvider _keys =
        keys ?? throw new ArgumentNullException(nameof(keys));

    /// <summary>
    /// The identity of the supplied directory and each bounded ancestor, deepest first.
    /// </summary>
    /// <remarks>
    /// Deepest first so the caller's <c>IN</c> query result can be reduced to the most specific match by
    /// position rather than by a second depth comparison. Returns an empty list — never a failure — when
    /// the path is absent, unreadable, not a directory, or the identity key is unavailable: none of
    /// those is an error in a request that simply supplied a directory Arcanum does not manage.
    /// </remarks>
    internal IReadOnlyList<CampaignRootCandidate> EnumerateAncestorIdentities(string? workingDirectory)
    {

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return [];
        }

        string full;

        try
        {
            full = Path.GetFullPath(workingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [];
        }

        Span<byte> key = stackalloc byte[32];

        try
        {

            if (!_keys.TryCopyRootIdentityKey(key))
            {
                return [];
            }

            List<CampaignRootCandidate> candidates = [];

            string? cursor = full;

            ulong? volume = null;

            int depth = 0;

            while (cursor is not null && candidates.Count < CampaignPathIdentityPolicy.MaxAncestorCandidates)
            {

                if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(cursor, out FileHandleMetadata metadata))
                {
                    break;
                }

                if (metadata.Kind is not FileSystemObjectKind.Directory)
                {
                    // A symlink resolves to its own object here rather than to its target, so a link
                    // pointing at a registered root is simply not a directory and contributes nothing.
                    break;
                }

                if (volume is { } expected && metadata.Identity.VolumeId != expected)
                {
                    // A mount point. The parent lives on a different filesystem, so it is not an
                    // ancestor of this root in any sense that should carry authority.
                    break;
                }

                volume ??= metadata.Identity.VolumeId;

                candidates.Add(new CampaignRootCandidate(
                    DeriveIdentity(key, metadata.Identity),
                    depth));

                depth++;

                string? parent = Path.GetDirectoryName(cursor);

                cursor = string.IsNullOrEmpty(parent) || string.Equals(parent, cursor, StringComparison.Ordinal)
                    ? null
                    : parent;

            }

            return candidates;

        }
        finally
        {

            CryptographicOperations.ZeroMemory(key);

        }

    }

    /// <summary>
    /// The identity of exactly one directory, with no ancestor walk.
    /// </summary>
    internal CovenantDigest? IdentifyExact(string? directory)
    {

        IReadOnlyList<CampaignRootCandidate> candidates = EnumerateAncestorIdentities(directory);

        return candidates.Count == 0
            ? null
            : candidates[0].PhysicalIdentityDigest;

    }

    /// <summary>
    /// <c>HMAC-SHA-256(rootKey, label || 0x00 || u32BE(policyVersion) || u64BE(volume) || u64BE(file))</c>.
    /// </summary>
    private static CovenantDigest DeriveIdentity(ReadOnlySpan<byte> key, FileHandleIdentity identity)
    {

        Span<byte> preimage = stackalloc byte[LabelBytes.Length + 1 + 4 + 8 + 8];

        LabelBytes.CopyTo(preimage);

        preimage[LabelBytes.Length] = 0;

        int offset = LabelBytes.Length + 1;

        BinaryPrimitives.WriteUInt32BigEndian(preimage[offset..], CampaignPathIdentityPolicy.Version);

        offset += 4;

        BinaryPrimitives.WriteUInt64BigEndian(preimage[offset..], identity.VolumeId);

        offset += 8;

        BinaryPrimitives.WriteUInt64BigEndian(preimage[offset..], identity.FileId);

        Span<byte> digest = stackalloc byte[32];

        _ = HMACSHA256.HashData(key, preimage, digest);

        return new CovenantDigest(digest.ToArray());

    }

}

/// <summary>
/// One candidate ancestor identity and how far above the supplied directory it sits.
/// </summary>
/// <remarks>
/// <see cref="Depth"/> is zero for the supplied directory itself and increases upward, so the smallest
/// depth among the matching rows is the most specific registered root.
/// </remarks>
internal readonly record struct CampaignRootCandidate(
    CovenantDigest PhysicalIdentityDigest,
    int Depth);
