using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Core.Covenant;

public static class CovenantDisclosureStateAlgebra
{
    public static byte[] CreateEvidenceBloom(CovenantDigest receiptDigest)
    {
        CovenantValidation.RequireDigest(receiptDigest, nameof(receiptDigest));
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData("Arcanum.Covenant.DisclosureBloom.v1\0"u8);
        hash.AppendData(receiptDigest.Bytes);

        byte[] evidence = hash.GetHashAndReset();
        byte[] bloom = new byte[CovenantLimits.DisclosureEvidenceBloomBytes];

        for (int index = 0; index < 4; index++)
        {
            int bit = BinaryPrimitives.ReadUInt16BigEndian(evidence.AsSpan(index * sizeof(ushort), sizeof(ushort))) % 256;

            bloom[bit / 8] |= (byte)(1 << (bit % 8));
        }

        return bloom;
    }

    public static CovenantDisclosureState IncrementLocal(
        CovenantDisclosureState state,
        ulong additionalCount,
        long timestamp,
        ReadOnlySpan<byte> evidenceBloom)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (additionalCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(additionalCount));
        }

        if (timestamp <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        }

        return new CovenantDisclosureState(
            state.Destination,
            state.Revocability,
            state.CountKind,
            true,
            checked(state.Count + additionalCount),
            state.EverOccurred ? Math.Max(state.MaximumTimestamp, timestamp) : timestamp,
            OrBloom(state.EvidenceBloom.AsSpan(), evidenceBloom));
    }

    public static CovenantDisclosureState JoinRestore(
        CovenantDisclosureState left,
        CovenantDisclosureState right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Destination != right.Destination
            || left.Revocability != right.Revocability)
        {
            throw new ArgumentException("Disclosure restore joins require the same destination and revocability bucket.");
        }

        if (left.Equals(right))
        {
            return left;
        }

        return new CovenantDisclosureState(
            left.Destination,
            left.Revocability,
            CovenantDisclosureCountKind.LowerBound,
            left.EverOccurred || right.EverOccurred,
            Math.Max(left.Count, right.Count),
            left.EverOccurred
                ? right.EverOccurred ? Math.Max(left.MaximumTimestamp, right.MaximumTimestamp) : left.MaximumTimestamp
                : right.MaximumTimestamp,
            OrBloom(left.EvidenceBloom.AsSpan(), right.EvidenceBloom.AsSpan()));
    }

    private static byte[] OrBloom(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != CovenantLimits.DisclosureEvidenceBloomBytes)
        {
            throw new ArgumentException("The left disclosure Bloom has an invalid size.", nameof(left));
        }

        if (right.Length != CovenantLimits.DisclosureEvidenceBloomBytes)
        {
            throw new ArgumentException("The right disclosure Bloom has an invalid size.", nameof(right));
        }

        byte[] result = new byte[CovenantLimits.DisclosureEvidenceBloomBytes];

        for (int index = 0; index < result.Length; index++)
        {
            result[index] = (byte)(left[index] | right[index]);
        }

        return result;
    }
}
