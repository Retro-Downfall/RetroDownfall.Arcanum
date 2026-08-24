using System.Buffers.Binary;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The exact facts one prepared operator mutation was measured against, on the wire.
/// </summary>
/// <remarks>
/// The encrypted body of an <see cref="CovenantEnvelopePurpose.OperatorPreflight"/> envelope. Its
/// fields are the same ones <see cref="PreflightBodyDigestInput"/> digests, and the round trip exists
/// so that commit can recompute that digest from what the token actually carried rather than from
/// what the commit request claims. A token whose body says one thing and whose digest says another is
/// refused; a body that decodes cleanly but describes state that has since moved is a stale snapshot.
///
/// <para>Fixed-width big-endian throughout, with an explicit presence byte for each optional. A
/// self-describing format would let a body grow a field one build understands and another silently
/// ignores, and the ignored field is exactly the one an attacker would choose.</para>
/// </remarks>
public sealed record CovenantOperatorPreflightBody(
    CovenantDigest RequestDigest,
    ulong OperatorAuthorityEpoch,
    Guid DatasetGeneration,
    ulong ExpectedTargetRevision,
    ulong NormalizedKeyDependencyEpoch,
    ulong KeyReclamationEpoch,
    ulong? CampaignRegistryEpoch,
    CovenantDigest? CompiledArtifactDigest,
    CovenantDigest DependentHeadVectorDigest,
    CovenantDigest EffectDigest,
    long IssuedAt,
    long ExpiresAt)
{

    /// <summary>The one encoded length this format ever produces.</summary>
    public const int EncodedBytes = 32 + 8 + 16 + 8 + 8 + 8 + 1 + 8 + 1 + 32 + 32 + 32 + 8 + 8;

    private const byte FormatVersion = 1;

    public PreflightBodyDigestInput ToDigestInput() =>
        new(
            RequestDigest,
            OperatorAuthorityEpoch,
            DatasetGeneration,
            ExpectedTargetRevision,
            NormalizedKeyDependencyEpoch,
            KeyReclamationEpoch,
            CampaignRegistryEpoch,
            CompiledArtifactDigest,
            DependentHeadVectorDigest,
            EffectDigest,
            IssuedAt,
            ExpiresAt);

    public CovenantDigest Digest() => CovenantDigests.PreflightBody(ToDigestInput());

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

        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset, 8), ExpectedTargetRevision);

        offset += 8;

        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset, 8), NormalizedKeyDependencyEpoch);

        offset += 8;

        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset, 8), KeyReclamationEpoch);

        offset += 8;

        buffer[offset++] = CampaignRegistryEpoch is null ? (byte)0 : (byte)1;

        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset, 8), CampaignRegistryEpoch ?? 0UL);

        offset += 8;

        buffer[offset++] = CompiledArtifactDigest is null ? (byte)0 : (byte)1;

        if (CompiledArtifactDigest is { } artifact)
        {

            artifact.Span.CopyTo(buffer.AsSpan(offset, 32));

        }

        offset += 32;

        DependentHeadVectorDigest.Span.CopyTo(buffer.AsSpan(offset, 32));

        offset += 32;

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
    /// Every failure is the same content-free refusal. The bytes reached this method by surviving
    /// authenticated decryption, so a caller that could distinguish "wrong length" from "unknown
    /// version" would be reporting on material it has no reason to describe.
    /// </remarks>
    public static Result<CovenantOperatorPreflightBody> TryDecode(ReadOnlySpan<byte> payload)
    {

        if (payload.Length != EncodedBytes + 1 || payload[0] != FormatVersion)
        {

            return Result<CovenantOperatorPreflightBody>.Failure(new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Covenant preflight token could not be read."));

        }

        int offset = 1;

        CovenantDigest requestDigest = new(payload.Slice(offset, 32).ToArray());

        offset += 32;

        ulong authorityEpoch = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(offset, 8));

        offset += 8;

        Guid datasetGeneration = new(payload.Slice(offset, 16), bigEndian: true);

        offset += 16;

        ulong expectedRevision = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(offset, 8));

        offset += 8;

        ulong keyDependencyEpoch = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(offset, 8));

        offset += 8;

        ulong keyReclamationEpoch = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(offset, 8));

        offset += 8;

        byte hasRegistry = payload[offset++];

        ulong registryEpoch = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(offset, 8));

        offset += 8;

        byte hasArtifact = payload[offset++];

        CovenantDigest artifactDigest = new(payload.Slice(offset, 32).ToArray());

        offset += 32;

        CovenantDigest dependentHeads = new(payload.Slice(offset, 32).ToArray());

        offset += 32;

        CovenantDigest effect = new(payload.Slice(offset, 32).ToArray());

        offset += 32;

        long issuedAt = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(offset, 8));

        offset += 8;

        long expiresAt = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(offset, 8));

        if (hasRegistry > 1 || hasArtifact > 1)
        {

            return Result<CovenantOperatorPreflightBody>.Failure(new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Covenant preflight token could not be read."));

        }

        return Result<CovenantOperatorPreflightBody>.Success(new CovenantOperatorPreflightBody(
            requestDigest,
            authorityEpoch,
            datasetGeneration,
            expectedRevision,
            keyDependencyEpoch,
            keyReclamationEpoch,
            hasRegistry == 1 ? registryEpoch : null,
            hasArtifact == 1 ? artifactDigest : null,
            dependentHeads,
            effect,
            issuedAt,
            expiresAt));

    }

}
