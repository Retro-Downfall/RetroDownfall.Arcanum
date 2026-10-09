using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// Durable version-one grammar. Strings are strict UTF-8 with unsigned big-endian byte lengths;
/// integers use signed big-endian representation, nullable fields use a one-byte presence tag, and
/// times are UTC ticks. Inputs arrive in ordinal identity order with sorted exact dependency edges.
/// Consolidation and prior-output context are bound because they change the decision; current-head
/// status is excluded so the identity can still be probed for historical inputs.
/// </summary>
internal static class LongRestCanonicalHash
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Input(LongRestSnapshot[] snapshots)
    {
        using MemoryStream bytes = Start("Arcanum.LongRest.Input.v1\0");

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

        return Hash(bytes);
    }

    internal static string Receipt(LongRestTransformationKind kind, string inputHash, string? requestedSurvivorVersionId)
    {
        using MemoryStream bytes = Start("Arcanum.LongRest.Receipt.v1\0");

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
        using MemoryStream bytes = Start("Arcanum.LongRest.Output.v1\0");

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

        Int32(bytes, LongRestPolicy.PolicyVersion);

        return bytes;
    }

    private static void Target(Stream bytes, LongRestSnapshot snapshot)
    {
        String(bytes, snapshot.MemoryId);

        String(bytes, snapshot.ClaimId);

        String(bytes, snapshot.VersionId);

        Int32(bytes, snapshot.Revision);

        Int32(bytes, (int)snapshot.ContentHashFormat);

        Digest(bytes, snapshot.ContentHash);
    }

    private static void Digest(Stream bytes, string value)
    {
        Int32(bytes, 32);

        bytes.Write(Convert.FromHexString(value));
    }

    private static void String(Stream bytes, string value)
    {
        byte[] encoded = StrictUtf8.GetBytes(value);

        Span<byte> length = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)encoded.Length));

        bytes.Write(length);

        bytes.Write(encoded);
    }

    private static void NullableString(Stream bytes, string? value)
    {
        Boolean(bytes, value is not null);

        if (value is not null)
        {
            String(bytes, value);
        }
    }

    private static void Int32(Stream bytes, int value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32BigEndian(encoded, value);

        bytes.Write(encoded);
    }

    private static void Time(Stream bytes, DateTimeOffset value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(long)];

        BinaryPrimitives.WriteInt64BigEndian(encoded, value.UtcTicks);

        bytes.Write(encoded);
    }

    private static void NullableTime(Stream bytes, DateTimeOffset? value)
    {
        Boolean(bytes, value is not null);

        if (value is { } instant)
        {
            Time(bytes, instant);
        }
    }

    private static void Boolean(Stream bytes, bool value) => bytes.WriteByte(value ? (byte)1 : (byte)0);

    private static string Hash(MemoryStream bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
}
