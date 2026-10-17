using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// Durable protected version-two grammar; the exact retained label identity follows the original snapshots. Strings are strict UTF-8 with unsigned big-endian byte lengths;
/// integers use signed big-endian representation, nullable fields use a one-byte presence tag, and
/// times are UTC ticks. Inputs arrive in ordinal identity order with sorted exact dependency edges.
/// Consolidation and prior-output context are bound because they change the decision; current-head
/// status is excluded so the identity can still be probed for historical inputs.
/// </summary>
internal static class ProtectedLongRestCanonicalHash
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Input(LongRestSnapshot[] snapshots, LongRestProtectedEvidence evidence,
        IReadOnlyList<(string MemoryId, Guid LabelId, CovenantDigest LabelDigest)> labels)
    {
        using MemoryStream bytes = Start("Arcanum.LongRest.Input.v2\0");

        Int32(bytes, snapshots.Length);

        foreach (LongRestSnapshot snapshot in snapshots)
        {
            Target(bytes, snapshot);

            Int32(bytes, (int)snapshot.Origin);

            NullableString(bytes, snapshot.SourceSessionId?.ToString("D"));

            Int32(bytes, (int)snapshot.ScopeKind);

            NullableString(bytes, snapshot.CampaignId?.ToString("D"));

            Int32(bytes, (int)snapshot.Sensitivity);

            Time(bytes, snapshot.ValidFromUtc);

            NullableTime(bytes, snapshot.ValidToUtc);

            Time(bytes, snapshot.RecordedAtUtc);

            NullableTime(bytes, snapshot.RetiredAtUtc);

            NullableTime(bytes, snapshot.PinnedAtUtc);

            Boolean(bytes, snapshot.HasEmbedding);

            Boolean(bytes, snapshot.HasSensitivityLabel);

            Boolean(bytes, snapshot.IsConsolidated);

            Boolean(bytes, snapshot.IsTransformationOutput);

            Int32(bytes, snapshot.Dependencies.Length);

            foreach (LongRestDependency edge in snapshot.Dependencies)
            {
                String(bytes, edge.VersionId);

                Int32(bytes, (int)edge.Relation);

                Int32(bytes, edge.Ordinal);
            }

            Int32(bytes, snapshot.IncomingDependencies.Length);

            foreach (LongRestIncomingDependency edge in snapshot.IncomingDependencies)
            {
                String(bytes, edge.DependentVersionId);

                Int32(bytes, (int)edge.Relation);

                Boolean(bytes, edge.IsCurrent);
            }
        }

        Digest(bytes, Convert.ToHexString(evidence.PartitionDigest.Bytes));

        Int32(bytes, (int)evidence.Provenance.Mode);

        Int32(bytes, evidence.Provenance.ExactGenerationIds.Length);

        foreach (Guid generation in evidence.Provenance.ExactGenerationIds)
        {
            String(bytes, generation.ToString("D"));
        }

        Int32(bytes, evidence.Provenance.BloomBits.Length);

        bytes.Write(evidence.Provenance.BloomBits.AsSpan());

        Int32(bytes, labels.Count);

        foreach ((string memory, Guid label, CovenantDigest digest) in labels.OrderBy(item => item.MemoryId, StringComparer.Ordinal))
        {
            String(bytes, memory);

            String(bytes, label.ToString("D"));

            Digest(bytes, Convert.ToHexString(digest.Bytes));
        }

        return Hash(bytes);
    }

    internal static string Receipt(LongRestTransformationKind kind, string inputHash, string? requestedSurvivorVersionId)
    {
        using MemoryStream bytes = Start("Arcanum.LongRest.Receipt.v2\0");

        Int32(bytes, (int)kind);

        Digest(bytes, inputHash);

        NullableString(bytes, requestedSurvivorVersionId);

        return Hash(bytes);
    }

    internal static string Output(
        LongRestTransformationKind kind,
        LongRestOutcome outcome,
        LongRestReason reason,
        LongRestSnapshot? survivor,
        LongRestSnapshot[] superseded)
    {
        using MemoryStream bytes = Start("Arcanum.LongRest.Output.v2\0");

        Int32(bytes, (int)kind);

        Int32(bytes, (int)outcome);

        Int32(bytes, (int)reason);

        Boolean(bytes, survivor is not null);

        if (survivor is not null)
        {
            Target(bytes, survivor);
        }

        Int32(bytes, superseded.Length);

        foreach (LongRestSnapshot snapshot in superseded)
        {
            Target(bytes, snapshot);
        }

        return Hash(bytes);
    }

    private static MemoryStream Start(string domain)
    {
        MemoryStream bytes = new();

        bytes.Write(StrictUtf8.GetBytes(domain));

        Int32(bytes, ProtectedLongRestPolicy.PolicyVersion);

        return bytes;
    }

    private static void Target(MemoryStream bytes, LongRestSnapshot snapshot)
    {
        String(bytes, snapshot.MemoryId);

        String(bytes, snapshot.ClaimId);

        String(bytes, snapshot.VersionId);

        Int32(bytes, snapshot.Revision);

        Int32(bytes, (int)snapshot.ContentHashFormat);

        Digest(bytes, snapshot.ContentHash);
    }

    private static void Digest(MemoryStream bytes, string value)
    {
        Int32(bytes, 32);

        bytes.Write(Convert.FromHexString(value));
    }

    private static void String(MemoryStream bytes, string value)
    {
        byte[] encoded = StrictUtf8.GetBytes(value);

        Span<byte> length = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)encoded.Length));

        bytes.Write(length);

        bytes.Write(encoded);
    }

    private static void NullableString(MemoryStream bytes, string? value)
    {
        Boolean(bytes, value is not null);

        if (value is not null)
        {
            String(bytes, value);
        }
    }

    private static void Int32(MemoryStream bytes, int value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32BigEndian(encoded, value);

        bytes.Write(encoded);
    }

    private static void Time(MemoryStream bytes, DateTimeOffset value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(long)];

        BinaryPrimitives.WriteInt64BigEndian(encoded, value.UtcTicks);

        bytes.Write(encoded);
    }

    private static void NullableTime(MemoryStream bytes, DateTimeOffset? value)
    {
        Boolean(bytes, value is not null);

        if (value is { } instant)
        {
            Time(bytes, instant);
        }
    }

    private static void Boolean(MemoryStream bytes, bool value) => bytes.WriteByte(value ? (byte)1 : (byte)0);

    private static string Hash(MemoryStream bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
}
