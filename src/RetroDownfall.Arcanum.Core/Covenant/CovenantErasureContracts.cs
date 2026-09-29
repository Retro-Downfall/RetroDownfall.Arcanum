using System.Buffers.Binary;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>The exact lane head an operator saw, which an erase requires still to be current.</summary>
public sealed record CovenantEraseHeadExpectation(Guid VersionId, long LaneRevision);

/// <summary>
/// A prepared Covenant entry erasure: the exact entry and both lane heads, as the operator's show
/// reported them. A Global entry has no Proposed lane, so it never carries a Proposed expectation.
/// </summary>
public sealed record CovenantErasePrepareRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    Guid EntryId,
    CovenantEraseHeadExpectation? Confirmed,
    CovenantEraseHeadExpectation? Proposed,
    Guid MutationId);

/// <summary>
/// The applied Covenant entry erasure: the prepared fields again, beside the token that authorizes them.
/// </summary>
public sealed record CovenantEraseRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    string Key,
    Guid EntryId,
    CovenantEraseHeadExpectation? Confirmed,
    CovenantEraseHeadExpectation? Proposed,
    Guid MutationId,
    string PreflightToken)
{
    /// <summary>The prepared request these fields describe, so apply re-derives its digest from them.</summary>
    public CovenantErasePrepareRequest ToPrepareRequest() =>
        new(Scope, CampaignId, Key, EntryId, Confirmed, Proposed, MutationId);
}

/// <summary>
/// The exact facts one prepared Covenant entry erasure was measured against, on the wire.
/// </summary>
/// <remarks>
/// The encrypted body of an <see cref="CovenantEnvelopePurpose.OperatorPreflight"/> envelope, beside
/// <see cref="CovenantOperatorPreflightBody"/> in the same purpose. The two never read as each other:
/// this body has its own format byte and its own fixed length, so an erasure token can never authorize
/// a set, correct, or curate, nor any of their tokens an erase.
///
/// <para>Fixed-width big-endian throughout, with an explicit presence byte for each optional head and
/// for <see cref="ReclaimsKey"/>, for the same reason as the operator body: a self-describing format
/// would let one build read a field another silently ignores.</para>
/// </remarks>
public sealed record CovenantErasurePreflightBody(
    CovenantDigest RequestDigest,
    ulong OperatorAuthorityEpoch,
    Guid DatasetGeneration,
    ulong KeyEpoch,
    ulong KeyReclamationEpoch,
    bool ReclaimsKey,
    Guid EntryId,
    Guid? ConfirmedVersionId,
    Guid? ProposedVersionId,
    CovenantDigest EffectDigest,
    long IssuedAt,
    long ExpiresAt)
{
    /// <summary>The one encoded length this format ever produces, after its format byte.</summary>
    public const int EncodedBytes = 32 + 8 + 16 + 8 + 8 + 1 + 16 + 1 + 16 + 1 + 16 + 32 + 8 + 8;

    private const byte FormatVersion = 1;

    public byte[] Encode()
    {
        byte[] buffer = new byte[EncodedBytes + 1];

        buffer[0] = FormatVersion;

        int offset = 1;

        RequestDigest.Span.CopyTo(buffer.AsSpan(offset, 32));

        offset += 32;

        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset, 8), OperatorAuthorityEpoch);

        offset += 8;

        _ = DatasetGeneration.TryWriteBytes(buffer.AsSpan(offset, 16), bigEndian: true, out _);

        offset += 16;

        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset, 8), KeyEpoch);

        offset += 8;

        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset, 8), KeyReclamationEpoch);

        offset += 8;

        buffer[offset++] = ReclaimsKey ? (byte)1 : (byte)0;

        _ = EntryId.TryWriteBytes(buffer.AsSpan(offset, 16), bigEndian: true, out _);

        offset += 16;

        offset = WriteOptional(buffer, offset, ConfirmedVersionId);

        offset = WriteOptional(buffer, offset, ProposedVersionId);

        EffectDigest.Span.CopyTo(buffer.AsSpan(offset, 32));

        offset += 32;

        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), IssuedAt);

        offset += 8;

        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), ExpiresAt);

        return buffer;
    }

    /// <summary>
    /// Reads one body, or reports that these bytes are not one.
    /// </summary>
    /// <remarks>
    /// Every failure is the same content-free refusal, for the reason the operator body gives: the
    /// bytes survived authenticated decryption, so there is nothing to gain by describing them.
    /// </remarks>
    public static Result<CovenantErasurePreflightBody> TryDecode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != EncodedBytes + 1 || payload[0] != FormatVersion)
        {
            return Refused();
        }

        int offset = 1;

        CovenantDigest requestDigest = new(payload.Slice(offset, 32).ToArray());

        offset += 32;

        ulong authorityEpoch = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(offset, 8));

        offset += 8;

        Guid datasetGeneration = new(payload.Slice(offset, 16), bigEndian: true);

        offset += 16;

        ulong keyEpoch = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(offset, 8));

        offset += 8;

        ulong keyReclamationEpoch = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(offset, 8));

        offset += 8;

        byte reclaimsKey = payload[offset++];

        Guid entryId = new(payload.Slice(offset, 16), bigEndian: true);

        offset += 16;

        byte hasConfirmed = payload[offset++];

        Guid confirmed = new(payload.Slice(offset, 16), bigEndian: true);

        offset += 16;

        byte hasProposed = payload[offset++];

        Guid proposed = new(payload.Slice(offset, 16), bigEndian: true);

        offset += 16;

        CovenantDigest effectDigest = new(payload.Slice(offset, 32).ToArray());

        offset += 32;

        long issuedAt = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(offset, 8));

        offset += 8;

        long expiresAt = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(offset, 8));

        if (reclaimsKey > 1 || hasConfirmed > 1 || hasProposed > 1)
        {
            return Refused();
        }

        return Result<CovenantErasurePreflightBody>.Success(new CovenantErasurePreflightBody(
            requestDigest,
            authorityEpoch,
            datasetGeneration,
            keyEpoch,
            keyReclamationEpoch,
            reclaimsKey == 1,
            entryId,
            hasConfirmed == 1 ? confirmed : null,
            hasProposed == 1 ? proposed : null,
            effectDigest,
            issuedAt,
            expiresAt));
    }

    private static int WriteOptional(byte[] buffer, int offset, Guid? value)
    {
        buffer[offset++] = value is null ? (byte)0 : (byte)1;

        if (value is { } present)
        {
            _ = present.TryWriteBytes(buffer.AsSpan(offset, 16), bigEndian: true, out _);
        }

        return offset + 16;
    }

    private static Result<CovenantErasurePreflightBody> Refused() =>
        Result<CovenantErasurePreflightBody>.Failure(new Error(
            ErrorCodes.MemoryErasure.InvalidPreflight,
            "This erasure preflight token could not be read."));
}
