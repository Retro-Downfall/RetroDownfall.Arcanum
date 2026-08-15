using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Core.Covenant;

public sealed class CovenantDisclosureState : IEquatable<CovenantDisclosureState>
{
    public CovenantDisclosureState(
        CovenantEgressDestination destination,
        CovenantDisclosureRevocability revocability,
        CovenantDisclosureCountKind countKind,
        bool everOccurred,
        ulong count,
        long maximumTimestamp,
        ReadOnlySpan<byte> evidenceBloom)
    {
        _ = CovenantPolicyV1Manifest.GetCode(destination);
        _ = CovenantPolicyV1Manifest.GetCode(revocability);
        _ = CovenantPolicyV1Manifest.GetCode(countKind);

        if (evidenceBloom.Length != CovenantLimits.DisclosureEvidenceBloomBytes)
        {
            throw new ArgumentException(
                $"Disclosure evidence must contain exactly {CovenantLimits.DisclosureEvidenceBloomBytes} bytes.",
                nameof(evidenceBloom));
        }

        CovenantDigests.ValidateDisclosureStateShape(countKind, everOccurred, count, maximumTimestamp, evidenceBloom);

        Destination = destination;
        Revocability = revocability;
        CountKind = countKind;
        EverOccurred = everOccurred;
        Count = count;
        MaximumTimestamp = maximumTimestamp;
        EvidenceBloom = [.. evidenceBloom];
        Digest = CovenantDigests.ExternalDisclosureState(ToDigestInput());
    }

    public CovenantEgressDestination Destination { get; }

    public CovenantDisclosureRevocability Revocability { get; }

    public CovenantDisclosureCountKind CountKind { get; }

    public bool EverOccurred { get; }

    public ulong Count { get; }

    public long MaximumTimestamp { get; }

    public ImmutableArray<byte> EvidenceBloom { get; }

    public CovenantDigest Digest { get; }

    public static CovenantDisclosureState Empty(
        CovenantEgressDestination destination,
        CovenantDisclosureRevocability revocability) =>
        new(destination, revocability, CovenantDisclosureCountKind.Exact, false, 0, 0, new byte[CovenantLimits.DisclosureEvidenceBloomBytes]);

    public bool Equals(CovenantDisclosureState? other) =>
        other is not null
        && Destination == other.Destination
        && Revocability == other.Revocability
        && CountKind == other.CountKind
        && EverOccurred == other.EverOccurred
        && Count == other.Count
        && MaximumTimestamp == other.MaximumTimestamp
        && EvidenceBloom.AsSpan().SequenceEqual(other.EvidenceBloom.AsSpan());

    public override bool Equals(object? obj) =>
        obj is CovenantDisclosureState other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();

        hash.Add(Destination);
        hash.Add(Revocability);
        hash.Add(CountKind);
        hash.Add(EverOccurred);
        hash.Add(Count);
        hash.Add(MaximumTimestamp);

        foreach (byte value in EvidenceBloom)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    internal ExternalDisclosureStateDigestInput ToDigestInput() =>
        new(Destination, Revocability, CountKind, EverOccurred, Count, MaximumTimestamp, EvidenceBloom);
}
