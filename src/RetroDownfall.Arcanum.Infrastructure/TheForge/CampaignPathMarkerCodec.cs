using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Infrastructure.TheForge;

/// <summary>
/// The only Campaign marker encoder and parser in the process.
/// </summary>
/// <remarks>
/// Registration, cleanup, restore cleanup, and compare-delete are all handed this one instance. The
/// alternative — each caller encoding its own idea of a marker — fails the moment two of them disagree
/// about a field, because the disagreement does not surface as a build error. It surfaces as a cleanup
/// that declines to delete a marker it cannot parse, on a root whose Campaign is already gone (§10.12).
///
/// <para>The tag is HMAC-SHA-256 under the installation root-identity key, so a marker is worth nothing
/// on another installation and a marker written by someone without the key authenticates nowhere. Key
/// loss fails closed in both directions: nothing encodes and nothing parses, which leaves every marker
/// untouched rather than letting an unauthenticated fallback delete one.</para>
/// </remarks>
internal sealed class CampaignPathMarkerCodec(ICampaignRootIdentityKeyProvider keys)
    : ICampaignPathMarkerCodec
{

    private static readonly byte[] LabelBytes =
        Encoding.ASCII.GetBytes(CampaignPathMarkerPolicy.Label);

    private readonly ICampaignRootIdentityKeyProvider _keys =
        keys ?? throw new ArgumentNullException(nameof(keys));

    /// <inheritdoc />
    public int ExactMarkerByteCount => CampaignPathMarkerPolicy.ExactMarkerByteCount;

    /// <inheritdoc />
    public int MaximumMarkerByteCount => CampaignPathMarkerPolicy.MaximumMarkerByteCount;

    /// <inheritdoc />
    public Result<byte[]> Encode(CampaignPathMarkerContent content)
    {

        if (content is null)
        {
            return InvalidMarker;
        }

        if (content.PolicyVersion != CampaignPathMarkerPolicy.Version
            || content.CampaignId == Guid.Empty
            || content.PathRevision <= 0
            || content.MarkerSecret.IsDefaultOrEmpty
            || content.MarkerSecret.Length != CampaignPathMarkerPolicy.MarkerSecretByteCount)
        {
            return InvalidMarker;
        }

        byte[] marker = new byte[CampaignPathMarkerPolicy.ExactMarkerByteCount];

        Span<byte> key = stackalloc byte[32];

        try
        {

            if (!_keys.TryCopyRootIdentityKey(key))
            {
                return KeyUnavailable;
            }

            WriteBody(marker, content);

            _ = HMACSHA256.HashData(
                key,
                marker.AsSpan(0, BodyByteCount),
                marker.AsSpan(BodyByteCount));

            return marker;

        }
        finally
        {

            CryptographicOperations.ZeroMemory(key);

        }

    }

    /// <inheritdoc />
    public Result<CampaignPathMarkerContent> Parse(ReadOnlySpan<byte> markerBytes)
    {

        // Length first, and exactly. A short marker cannot be padded into validity and a long one is
        // refused rather than parsed from its prefix — a prefix-tolerant parser would accept bytes that
        // the exact comparison guarding every delete would then reject, and the two would disagree
        // about the same file.
        if (markerBytes.Length != CampaignPathMarkerPolicy.ExactMarkerByteCount)
        {
            return InvalidMarker;
        }

        Span<byte> key = stackalloc byte[32];

        Span<byte> expectedTag = stackalloc byte[CampaignPathMarkerPolicy.AuthenticationTagByteCount];

        try
        {

            if (!_keys.TryCopyRootIdentityKey(key))
            {
                return KeyUnavailable;
            }

            _ = HMACSHA256.HashData(key, markerBytes[..BodyByteCount], expectedTag);

            if (!CryptographicOperations.FixedTimeEquals(
                    expectedTag,
                    markerBytes[BodyByteCount..]))
            {
                return InvalidMarker;
            }

        }
        finally
        {

            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(expectedTag);

        }

        // Only now are the fields worth reading: nothing below has been chosen by anyone without the
        // installation key.
        if (!markerBytes[..LabelBytes.Length].SequenceEqual(LabelBytes)
            || markerBytes[LabelBytes.Length] != 0)
        {
            return InvalidMarker;
        }

        int offset = LabelBytes.Length + 1;

        uint version = BinaryPrimitives.ReadUInt32BigEndian(markerBytes[offset..]);

        offset += sizeof(uint);

        Guid campaignId = new(markerBytes.Slice(offset, 16), bigEndian: true);

        offset += 16;

        long pathRevision = BinaryPrimitives.ReadInt64BigEndian(markerBytes[offset..]);

        offset += sizeof(long);

        ulong volumeId = BinaryPrimitives.ReadUInt64BigEndian(markerBytes[offset..]);

        offset += sizeof(ulong);

        ulong fileId = BinaryPrimitives.ReadUInt64BigEndian(markerBytes[offset..]);

        offset += sizeof(ulong);

        ushort secretLength = BinaryPrimitives.ReadUInt16BigEndian(markerBytes[offset..]);

        offset += sizeof(ushort);

        if (version != CampaignPathMarkerPolicy.Version
            || campaignId == Guid.Empty
            || pathRevision <= 0
            || secretLength != CampaignPathMarkerPolicy.MarkerSecretByteCount)
        {
            return InvalidMarker;
        }

        return new CampaignPathMarkerContent(
            version,
            campaignId,
            pathRevision,
            volumeId,
            fileId,
            [.. markerBytes.Slice(offset, secretLength)]);

    }

    /// <inheritdoc />
    public bool MatchesExactBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        expected.Length == actual.Length
        && CryptographicOperations.FixedTimeEquals(expected, actual);

    private static int BodyByteCount =>
        CampaignPathMarkerPolicy.ExactMarkerByteCount
        - CampaignPathMarkerPolicy.AuthenticationTagByteCount;

    private static Error InvalidMarker =>
        new(ErrorCodes.Campaign.InvalidPath, "The Campaign root marker is not well formed.");

    private static Error KeyUnavailable =>
        new(
            ErrorCodes.Campaign.PathIdentityRequired,
            "The Campaign root identity key is unavailable, so no marker can be authenticated.");

    private static void WriteBody(Span<byte> marker, CampaignPathMarkerContent content)
    {

        LabelBytes.CopyTo(marker);

        int offset = LabelBytes.Length;

        marker[offset] = 0;

        offset++;

        BinaryPrimitives.WriteUInt32BigEndian(marker[offset..], content.PolicyVersion);

        offset += sizeof(uint);

        _ = content.CampaignId.TryWriteBytes(marker.Slice(offset, 16), bigEndian: true, out _);

        offset += 16;

        BinaryPrimitives.WriteInt64BigEndian(marker[offset..], content.PathRevision);

        offset += sizeof(long);

        BinaryPrimitives.WriteUInt64BigEndian(marker[offset..], content.RootVolumeId);

        offset += sizeof(ulong);

        BinaryPrimitives.WriteUInt64BigEndian(marker[offset..], content.RootFileId);

        offset += sizeof(ulong);

        BinaryPrimitives.WriteUInt16BigEndian(
            marker[offset..],
            CampaignPathMarkerPolicy.MarkerSecretByteCount);

        offset += sizeof(ushort);

        content.MarkerSecret.AsSpan().CopyTo(marker[offset..]);

    }

}
