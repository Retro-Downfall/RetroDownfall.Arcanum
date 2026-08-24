using System.Buffers.Binary;

using System.Text;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The wire form of a pagination cursor's encrypted body.
/// </summary>
/// <remarks>
/// The storage layer decides which facts a cursor binds; this decides how those facts travel. Keeping
/// them apart is what lets the dataset-drift rules be tested without an envelope and the encoding be
/// tested without a database.
///
/// <para>Fixed-width big-endian with an explicit length prefix on the one variable field, and a
/// leading format byte per endpoint. A cursor is attacker-supplied by construction — it comes back
/// from a client — so nothing here infers a shape from the bytes: a body that is not exactly the
/// declared length for its declared endpoint is refused before any field is read.</para>
/// </remarks>
public static class CovenantCursorBodyCodec
{

    private const byte ListFormat = 1;

    private const byte VersionFormat = 2;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>The bound on a normalized key inside a cursor, matching the key grammar's own.</summary>
    private const int MaxKeyBytes = 512;

    public static byte[] Encode(CovenantListCursorBody body)
    {

        ArgumentNullException.ThrowIfNull(body);

        byte[] key = StrictUtf8.GetBytes(body.Keyset.NormalizedKey);

        if (key.Length > MaxKeyBytes)
        {

            throw new ArgumentException("A cursor key exceeds the Covenant key bound.", nameof(body));

        }

        byte[] buffer = new byte[1 + 1 + 32 + 16 + 8 + 8 + 8 + 1 + 16 + 16 + 1 + 2 + key.Length];

        int offset = 0;

        buffer[offset++] = ListFormat;

        buffer[offset++] = (byte)body.Endpoint;

        body.FilterDigest.Span.CopyTo(buffer.AsSpan(offset, 32));

        offset += 32;

        offset += WriteGuid(buffer, offset, body.DatasetGeneration);

        offset += WriteInt64(buffer, offset, body.CanonicalSearchSequence);

        offset += WriteInt64(buffer, offset, body.CoreCampaignDeletionSequence);

        offset += WriteInt64(buffer, offset, body.EnvelopeKeyVersion);

        buffer[offset++] = body.Keyset.ScopeOrdinal;

        offset += WriteGuid(buffer, offset, body.Keyset.CampaignId);

        offset += WriteGuid(buffer, offset, body.Keyset.EntryId);

        buffer[offset++] = body.Keyset.LaneOrdinal;

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)key.Length);

        offset += 2;

        key.CopyTo(buffer.AsSpan(offset, key.Length));

        return buffer;

    }

    public static byte[] Encode(CovenantVersionCursorBody body)
    {

        ArgumentNullException.ThrowIfNull(body);

        byte[] buffer = new byte[1 + 32 + 16 + 8 + 8 + 8 + 8 + 16];

        int offset = 0;

        buffer[offset++] = VersionFormat;

        body.FilterDigest.Span.CopyTo(buffer.AsSpan(offset, 32));

        offset += 32;

        offset += WriteGuid(buffer, offset, body.DatasetGeneration);

        offset += WriteInt64(buffer, offset, body.CanonicalSearchSequence);

        offset += WriteInt64(buffer, offset, body.CoreCampaignDeletionSequence);

        offset += WriteInt64(buffer, offset, body.EnvelopeKeyVersion);

        offset += WriteInt64(buffer, offset, body.Keyset.LaneRevision);

        _ = WriteGuid(buffer, offset, body.Keyset.VersionId);

        return buffer;

    }

    public static Result<CovenantListCursorBody> TryDecodeList(ReadOnlySpan<byte> payload)
    {

        const int Fixed = 1 + 1 + 32 + 16 + 8 + 8 + 8 + 1 + 16 + 16 + 1 + 2;

        if (payload.Length < Fixed || payload[0] != ListFormat)
        {

            return Invalid<CovenantListCursorBody>();

        }

        int offset = 1;

        byte endpoint = payload[offset++];

        if (endpoint is not ((byte)CovenantCursorEndpoint.List
            or (byte)CovenantCursorEndpoint.FallbackQuery))
        {

            return Invalid<CovenantListCursorBody>();

        }

        CovenantDigest filter = new(payload.Slice(offset, 32).ToArray());

        offset += 32;

        Guid dataset = ReadGuid(payload, ref offset);

        long canonical = ReadInt64(payload, ref offset);

        long deletion = ReadInt64(payload, ref offset);

        long keyVersion = ReadInt64(payload, ref offset);

        byte scopeOrdinal = payload[offset++];

        Guid campaignId = ReadGuid(payload, ref offset);

        Guid entryId = ReadGuid(payload, ref offset);

        byte laneOrdinal = payload[offset++];

        int keyLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));

        offset += 2;

        // The declared length must account for the whole body exactly. A trailing byte is a body
        // somebody appended to, and a short one is a body somebody truncated.
        if (keyLength > MaxKeyBytes || payload.Length != Fixed + keyLength)
        {

            return Invalid<CovenantListCursorBody>();

        }

        string normalizedKey;

        try
        {

            normalizedKey = StrictUtf8.GetString(payload.Slice(offset, keyLength));

        }
        catch (DecoderFallbackException)
        {

            return Invalid<CovenantListCursorBody>();

        }

        return Result<CovenantListCursorBody>.Success(new CovenantListCursorBody(
            (CovenantCursorEndpoint)endpoint,
            filter,
            dataset,
            canonical,
            deletion,
            keyVersion,
            new CovenantListKeyset(scopeOrdinal, campaignId, normalizedKey, entryId, laneOrdinal)));

    }

    public static Result<CovenantVersionCursorBody> TryDecodeVersion(ReadOnlySpan<byte> payload)
    {

        const int Expected = 1 + 32 + 16 + 8 + 8 + 8 + 8 + 16;

        if (payload.Length != Expected || payload[0] != VersionFormat)
        {

            return Invalid<CovenantVersionCursorBody>();

        }

        int offset = 1;

        CovenantDigest filter = new(payload.Slice(offset, 32).ToArray());

        offset += 32;

        Guid dataset = ReadGuid(payload, ref offset);

        long canonical = ReadInt64(payload, ref offset);

        long deletion = ReadInt64(payload, ref offset);

        long keyVersion = ReadInt64(payload, ref offset);

        long laneRevision = ReadInt64(payload, ref offset);

        Guid versionId = ReadGuid(payload, ref offset);

        return Result<CovenantVersionCursorBody>.Success(new CovenantVersionCursorBody(
            filter,
            dataset,
            canonical,
            deletion,
            keyVersion,
            new CovenantVersionKeyset(laneRevision, versionId)));

    }

    /// <summary>
    /// The one refusal every malformed cursor receives.
    /// </summary>
    /// <remarks>
    /// Content-free and identical for every failure mode. These bytes arrived from a client, and a
    /// decoder that distinguished "wrong endpoint" from "bad length" would be describing material it
    /// has no reason to characterise — and telling whoever sent it which guess was closer.
    /// </remarks>
    private static Result<T> Invalid<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Covenant.InvalidCursor,
            "This Covenant cursor could not be read."));

    private static int WriteGuid(byte[] buffer, int offset, Guid value)
    {

        _ = value.TryWriteBytes(buffer.AsSpan(offset, 16), bigEndian: true, out _);

        return 16;

    }

    private static int WriteInt64(byte[] buffer, int offset, long value)
    {

        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), value);

        return 8;

    }

    private static Guid ReadGuid(ReadOnlySpan<byte> payload, ref int offset)
    {

        Guid value = new(payload.Slice(offset, 16), bigEndian: true);

        offset += 16;

        return value;

    }

    private static long ReadInt64(ReadOnlySpan<byte> payload, ref int offset)
    {

        long value = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(offset, 8));

        offset += 8;

        return value;

    }

}
