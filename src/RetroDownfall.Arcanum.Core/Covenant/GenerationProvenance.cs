using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GenerationProvenanceMode>))]
public enum GenerationProvenanceMode : byte
{
    Exact = 1,
    BloomOverflow = 2
}

public sealed class GenerationProvenance : IEquatable<GenerationProvenance>
{
    private static readonly byte[] BloomDomain = "Arcanum.Covenant.GenerationBloom.v1\0"u8.ToArray();

    [JsonConstructor]
    public GenerationProvenance(
        GenerationProvenanceMode mode,
        ImmutableArray<Guid> exactGenerationIds,
        ImmutableArray<byte> bloomBits)
    {
        if (mode == GenerationProvenanceMode.Exact)
        {
            ValidateExact(exactGenerationIds);

            if (bloomBits.IsDefault || !bloomBits.IsEmpty)
            {
                throw new ArgumentException("Exact provenance cannot carry Bloom bits.", nameof(bloomBits));
            }
        }
        else if (mode == GenerationProvenanceMode.BloomOverflow)
        {
            if (exactGenerationIds.IsDefault || !exactGenerationIds.IsEmpty)
            {
                throw new ArgumentException("Bloom provenance cannot carry exact generation identities.", nameof(exactGenerationIds));
            }

            if (bloomBits.IsDefault
                || bloomBits.Length != CovenantLimits.GenerationBloomBytes
                || IsAllZero(bloomBits.AsSpan()))
            {
                throw new ArgumentException($"Bloom provenance must contain exactly {CovenantLimits.GenerationBloomBytes} bytes with at least one set bit.", nameof(bloomBits));
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Mode = mode;
        ExactGenerationIds = ImmutableCollectionsMarshal.AsImmutableArray(exactGenerationIds.AsSpan().ToArray());
        BloomBits = ImmutableCollectionsMarshal.AsImmutableArray(bloomBits.AsSpan().ToArray());
    }

    public GenerationProvenanceMode Mode { get; }

    public ImmutableArray<Guid> ExactGenerationIds { get; }

    public ImmutableArray<byte> BloomBits { get; }

    public bool IsEmpty =>
        Mode == GenerationProvenanceMode.Exact && ExactGenerationIds.IsEmpty;

    public static GenerationProvenance CreateExact(IEnumerable<Guid> generationIds)
    {
        ArgumentNullException.ThrowIfNull(generationIds);

        Guid[] distinct = new Guid[CovenantLimits.MaxExactGenerationIds];
        int count = 0;

        foreach (Guid generationId in generationIds)
        {
            CovenantValidation.RequireNonEmpty(generationId, nameof(generationIds));

            if (Contains(distinct, count, generationId))
            {
                continue;
            }

            if (count == distinct.Length)
            {
                throw new ArgumentException($"Exact provenance supports at most {CovenantLimits.MaxExactGenerationIds} generations.", nameof(generationIds));
            }

            distinct[count++] = generationId;
        }

        Array.Sort(distinct, 0, count, RawGuidComparer.Instance);

        return new GenerationProvenance(
            GenerationProvenanceMode.Exact,
            [.. distinct.AsSpan(0, count)],
            []);
    }

    public static GenerationProvenance Create(IEnumerable<Guid> generationIds)
    {
        ArgumentNullException.ThrowIfNull(generationIds);

        Guid[] distinct = new Guid[CovenantLimits.MaxExactGenerationIds];
        byte[]? bloom = null;
        int count = 0;

        foreach (Guid generationId in generationIds)
        {
            CovenantValidation.RequireNonEmpty(generationId, nameof(generationIds));

            if (bloom is not null)
            {
                SetBloomBits(bloom, generationId);
                continue;
            }

            if (Contains(distinct, count, generationId))
            {
                continue;
            }

            if (count < distinct.Length)
            {
                distinct[count++] = generationId;
                continue;
            }

            bloom = new byte[CovenantLimits.GenerationBloomBytes];

            for (int index = 0; index < count; index++)
            {
                SetBloomBits(bloom, distinct[index]);
            }

            SetBloomBits(bloom, generationId);
        }

        if (bloom is not null)
        {
            return CreateBloom(bloom);
        }

        Array.Sort(distinct, 0, count, RawGuidComparer.Instance);

        return new GenerationProvenance(
            GenerationProvenanceMode.Exact,
            [.. distinct.AsSpan(0, count)],
            []);
    }

    public static GenerationProvenance CreateBloom(ReadOnlySpan<byte> bloomBits)
    {
        if (bloomBits.Length != CovenantLimits.GenerationBloomBytes)
        {
            throw new ArgumentException($"Bloom provenance must contain exactly {CovenantLimits.GenerationBloomBytes} bytes.", nameof(bloomBits));
        }

        return new GenerationProvenance(
            GenerationProvenanceMode.BloomOverflow,
            [],
            [.. bloomBits]);
    }

    public GenerationProvenance Add(Guid generationId)
    {
        CovenantValidation.RequireNonEmpty(generationId, nameof(generationId));

        if (Mode == GenerationProvenanceMode.Exact)
        {
            if (Contains(ExactGenerationIds.AsSpan(), generationId))
            {
                return this;
            }

            if (ExactGenerationIds.Length < CovenantLimits.MaxExactGenerationIds)
            {
                Guid[] expanded = new Guid[ExactGenerationIds.Length + 1];

                ExactGenerationIds.CopyTo(expanded);
                expanded[^1] = generationId;

                return CreateExact(expanded);
            }

            byte[] overflow = CreateBloomBits(ExactGenerationIds.AsSpan());

            SetBloomBits(overflow, generationId);

            return CreateBloom(overflow);
        }

        byte[] bloom = BloomBits.ToArray();

        SetBloomBits(bloom, generationId);

        return bloom.AsSpan().SequenceEqual(BloomBits.AsSpan())
            ? this
            : CreateBloom(bloom);
    }

    public bool ContainsExact(Guid generationId)
    {
        CovenantValidation.RequireNonEmpty(generationId, nameof(generationId));

        return Mode == GenerationProvenanceMode.Exact
            && Contains(ExactGenerationIds.AsSpan(), generationId);
    }

    public GenerationProvenance Merge(GenerationProvenance other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Mode == GenerationProvenanceMode.Exact && other.Mode == GenerationProvenanceMode.Exact)
        {
            Guid[] combined = new Guid[ExactGenerationIds.Length + other.ExactGenerationIds.Length];

            ExactGenerationIds.CopyTo(combined);
            other.ExactGenerationIds.CopyTo(combined, ExactGenerationIds.Length);

            return Create(combined);
        }

        byte[] bloom = Mode == GenerationProvenanceMode.BloomOverflow
            ? BloomBits.ToArray()
            : CreateBloomBits(ExactGenerationIds.AsSpan());

        if (other.Mode == GenerationProvenanceMode.BloomOverflow)
        {
            for (int index = 0; index < bloom.Length; index++)
            {
                bloom[index] |= other.BloomBits[index];
            }
        }
        else
        {
            foreach (Guid generationId in other.ExactGenerationIds)
            {
                SetBloomBits(bloom, generationId);
            }
        }

        return CreateBloom(bloom);
    }

    public SensitivityDigestInput ToDigestInput(ContentSensitivity level)
    {
        ContentSensitivityAlgebra.Validate(level, this, nameof(level));

        return new SensitivityDigestInput(level, Mode, ExactGenerationIds, BloomBits);
    }

    public bool Equals(GenerationProvenance? other) =>
        other is not null
        && Mode == other.Mode
        && ExactGenerationIds.AsSpan().SequenceEqual(other.ExactGenerationIds.AsSpan())
        && BloomBits.AsSpan().SequenceEqual(other.BloomBits.AsSpan());

    public override bool Equals(object? obj) =>
        obj is GenerationProvenance other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();

        hash.Add(Mode);

        foreach (Guid generationId in ExactGenerationIds)
        {
            hash.Add(generationId);
        }

        foreach (byte value in BloomBits)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    private static void ValidateExact(ImmutableArray<Guid> generationIds)
    {
        if (generationIds.IsDefault)
        {
            throw new ArgumentException("Exact provenance must use an initialized generation vector.", nameof(generationIds));
        }

        if (generationIds.Length > CovenantLimits.MaxExactGenerationIds)
        {
            throw new ArgumentException($"Exact provenance supports at most {CovenantLimits.MaxExactGenerationIds} generations.", nameof(generationIds));
        }

        for (int index = 0; index < generationIds.Length; index++)
        {
            CovenantValidation.RequireNonEmpty(generationIds[index], nameof(generationIds));

            if (index > 0 && RawGuidComparer.Instance.Compare(generationIds[index - 1], generationIds[index]) >= 0)
            {
                throw new ArgumentException("Exact provenance generations must be distinct and sorted.", nameof(generationIds));
            }
        }
    }

    private static bool Contains(Guid[] values, int count, Guid candidate)
    {
        for (int index = 0; index < count; index++)
        {
            if (values[index] == candidate)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(ReadOnlySpan<Guid> values, Guid candidate)
    {
        foreach (Guid value in values)
        {
            if (value == candidate)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] CreateBloomBits(ReadOnlySpan<Guid> values)
    {
        byte[] bloom = new byte[CovenantLimits.GenerationBloomBytes];

        foreach (Guid value in values)
        {
            SetBloomBits(bloom, value);
        }

        return bloom;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> values)
    {
        foreach (byte value in values)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void SetBloomBits(Span<byte> bloom, Guid generationId)
    {
        Span<byte> preimage = stackalloc byte[BloomDomain.Length + 16];
        Span<byte> hash = stackalloc byte[CovenantLimits.DigestBytes];

        BloomDomain.CopyTo(preimage);

        if (!generationId.TryWriteBytes(preimage[BloomDomain.Length..], bigEndian: true, out int bytesWritten)
            || bytesWritten != 16)
        {
            throw new InvalidOperationException("The generation ID could not be encoded in network byte order.");
        }

        int hashBytes = SHA256.HashData(preimage, hash);

        if (hashBytes != CovenantLimits.DigestBytes)
        {
            throw new InvalidOperationException("The generation Bloom hash did not produce 32 bytes.");
        }

        for (int offset = 0; offset < sizeof(ushort) * 4; offset += sizeof(ushort))
        {
            int position = BinaryPrimitives.ReadUInt16BigEndian(hash[offset..]) % (CovenantLimits.GenerationBloomBytes * 8);

            bloom[position / 8] |= checked((byte)(1 << (position % 8)));
        }
    }

    private sealed class RawGuidComparer : IComparer<Guid>
    {
        public static RawGuidComparer Instance { get; } = new();

        public int Compare(Guid left, Guid right)
        {
            Span<byte> leftBytes = stackalloc byte[16];
            Span<byte> rightBytes = stackalloc byte[16];

            left.TryWriteBytes(leftBytes, bigEndian: true, out _);
            right.TryWriteBytes(rightBytes, bigEndian: true, out _);

            return leftBytes.SequenceCompareTo(rightBytes);
        }
    }
}
