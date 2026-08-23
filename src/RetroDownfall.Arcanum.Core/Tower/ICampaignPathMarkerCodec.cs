using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Tower;

/// <summary>
/// The frozen shape of a Campaign root marker.
/// </summary>
/// <remarks>
/// Every field is fixed width and the encoding carries no clock, counter, or fresh nonce, so the same
/// content always produces the same bytes. Recovery compares the bytes it journaled before the crash
/// against the bytes it finds on disk afterwards; a codec that mixed in a timestamp would make that
/// comparison fail every time and turn ordinary cleanup into a manual blocker on a marker Arcanum
/// itself wrote (§10.12).
/// </remarks>
public static class CampaignPathMarkerPolicy
{

    /// <summary>The current marker encoding version.</summary>
    public const uint Version = 1;

    /// <summary>The domain-separation label that opens every marker.</summary>
    /// <remarks>
    /// Distinct from the root-identity label so a marker can never be replayed as an identity preimage,
    /// or the reverse.
    /// </remarks>
    public const string Label = "Arcanum.Campaign.PathMarker.v1";

    /// <summary>The ASCII byte length of <see cref="Label"/>.</summary>
    public const int LabelByteCount = 30;

    /// <summary>The random per-registration payload carried inside the marker.</summary>
    public const int MarkerSecretByteCount = 32;

    /// <summary>The trailing HMAC-SHA-256 tag.</summary>
    public const int AuthenticationTagByteCount = 32;

    /// <summary>
    /// The exact length of a well-formed marker, in bytes.
    /// </summary>
    /// <remarks>
    /// Exact rather than a maximum. A marker is a fixed record, so a length that is merely plausible is
    /// already a rejected marker — that is what stops a caller from appending trailing bytes that a
    /// prefix-tolerant parser would ignore while an exact-byte comparison elsewhere still matched.
    /// </remarks>
    public const int ExactMarkerByteCount =
        LabelByteCount
        + 1
        + sizeof(uint)
        + 16
        + sizeof(long)
        + sizeof(ulong)
        + sizeof(ulong)
        + sizeof(ushort)
        + MarkerSecretByteCount
        + AuthenticationTagByteCount;

    /// <summary>
    /// The ceiling on any bounded read of a marker or temporary file.
    /// </summary>
    /// <remarks>
    /// Larger than <see cref="ExactMarkerByteCount"/> so an over-long file is read far enough to be
    /// rejected as over-long rather than silently truncated to a length that could authenticate. It
    /// matches the durable payload cap in <c>campaign_path_marker_intents</c>, so nothing the journal
    /// can hold is larger than a read the recovery path is allowed to perform.
    /// </remarks>
    public const int MaximumMarkerByteCount = 4096;

}

/// <summary>
/// What a Campaign root marker asserts about the directory holding it.
/// </summary>
/// <remarks>
/// The physical tuple is the reason this is more than a name in a file. A marker is bound to the volume
/// and file identifiers of the root it was written into, so copying the file into a second directory
/// produces a marker whose own contents contradict where it now lives, and the copy claims nothing.
///
/// <para>Equality is spelled out rather than inherited because <see cref="ImmutableArray{T}"/> compares
/// by underlying-array reference. Left to the compiler, two structurally identical markers read from
/// disk in two different processes would compare unequal, and every recovery comparison in the marker
/// protocol would fail against a marker that was in fact correct.</para>
/// </remarks>
public sealed record CampaignPathMarkerContent(
    uint PolicyVersion,
    Guid CampaignId,
    long PathRevision,
    ulong RootVolumeId,
    ulong RootFileId,
    ImmutableArray<byte> MarkerSecret)
{

    /// <inheritdoc />
    public bool Equals(CampaignPathMarkerContent? other) =>
        other is not null
        && PolicyVersion == other.PolicyVersion
        && CampaignId == other.CampaignId
        && PathRevision == other.PathRevision
        && RootVolumeId == other.RootVolumeId
        && RootFileId == other.RootFileId
        && MarkerSecret.AsSpan().SequenceEqual(other.MarkerSecret.AsSpan());

    /// <inheritdoc />
    public override int GetHashCode()
    {

        HashCode hash = new();

        hash.Add(PolicyVersion);
        hash.Add(CampaignId);
        hash.Add(PathRevision);
        hash.Add(RootVolumeId);
        hash.Add(RootFileId);
        hash.AddBytes(MarkerSecret.AsSpan());

        return hash.ToHashCode();

    }

}

/// <summary>
/// The single encoder, parser, and exact-byte comparer for Campaign root markers.
/// </summary>
/// <remarks>
/// One implementation, shared by registration, cleanup, restore cleanup, and compare-delete. Two codecs
/// that agree on the day they are written drift as soon as either gains a field, and the first
/// divergence is a cleanup that refuses to recognise the marker its own registration produced — leaving
/// a live marker on a root no Campaign owns any more, which the next registration then reads as a
/// conflict it cannot resolve.
///
/// <para>Nothing here touches the filesystem. The codec is handed bytes and returns bytes, so a caller
/// cannot reach a file through it, and the capability layer that does own handles never has to trust a
/// path the codec produced.</para>
/// </remarks>
public interface ICampaignPathMarkerCodec
{

    /// <summary>The exact length of a well-formed marker.</summary>
    int ExactMarkerByteCount { get; }

    /// <summary>The ceiling on any bounded marker or temporary-file read.</summary>
    int MaximumMarkerByteCount { get; }

    /// <summary>
    /// Encodes one marker deterministically, or fails without producing a byte.
    /// </summary>
    /// <remarks>
    /// Malformed content is refused here rather than written and rejected on the next read, because a
    /// marker that reaches disk is a marker some later recovery has to classify.
    /// </remarks>
    Result<byte[]> Encode(CampaignPathMarkerContent content);

    /// <summary>
    /// Authenticates and decodes exactly one marker.
    /// </summary>
    /// <remarks>
    /// The tag is verified before any field is believed, so a caller never acts on a Campaign ID or
    /// physical tuple an attacker chose. Length is checked first and exactly: a short read cannot be
    /// padded into validity, and trailing bytes are a rejection rather than something to ignore.
    /// </remarks>
    Result<CampaignPathMarkerContent> Parse(ReadOnlySpan<byte> markerBytes);

    /// <summary>
    /// Reports whether two byte vectors are the same marker, byte for byte.
    /// </summary>
    /// <remarks>
    /// Fixed-time over the compared region and length-checked first. This is the comparison that
    /// authorizes a delete, so an early exit that revealed where two markers first differ would let a
    /// caller who can write into the root grind out a byte sequence the owner would then remove.
    /// </remarks>
    bool MatchesExactBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual);

}
