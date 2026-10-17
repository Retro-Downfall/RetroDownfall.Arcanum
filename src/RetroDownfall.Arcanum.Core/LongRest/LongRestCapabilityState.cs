using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// The Covenant capability a Long Rest sweep stands on, as the one positive number its fence carries in
/// <see cref="LongRestSweepFence.CapabilityGeneration"/>.
/// </summary>
/// <remarks>
/// <para>It is a function of what the sweep's protected reads depend on: whether the Covenant feature is on, the canonical tier's
/// health, schema version and installed catalog, and the dataset generation. It is deliberately not
/// <see cref="CovenantAvailabilitySnapshot.Generation"/>. That counter moves on every availability publication, and publications are
/// routine: every Covenant maintenance pass republishes even when it removed nothing (every five minutes), and every Covenant write,
/// including the proposal a turn commits, republishes with a new canonical sequence. A fence that required the counter to be unchanged
/// would be invalidated by all of them, so a sweep longer than a pass would restart from nothing and a protected park would never
/// be readmitted in the boot that parked it. A publication that changes none of the facts below leaves the number, and so every
/// sweep and park, exactly as it was; one that changes any of them moves it, and a sweep or park that a check then observes the move in ends as stale. A change that is undone before any check (the canonical tier degraded and repaired to the same schema and catalog) returns the number to the value it had, which is safe because the term is never authority: the gate judges the tier's health at every use.</para>
///
/// <para>The number is a truncated SHA-256 of a fixed, versioned encoding in the discovery grammar (a domain string and NUL, signed
/// big-endian numbers, UTF-8 strings with a big-endian byte length, one-byte presence tags), always odd and below
/// <see cref="long.MaxValue"/>, so it is at least one as a fence requires. Only equality is ever asked of it. Sequences, the applied
/// search tuple, the accelerator and its synchronization, and the last health transition are not part of it: none of them is a fact
/// a sweep's reads stand on.</para>
/// </remarks>
public static class LongRestCapabilityState
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("arcanum-long-rest-capability-state-v1\0");

    public static long Of(CovenantAvailabilitySnapshot availability)
    {
        ArgumentNullException.ThrowIfNull(availability);

        using MemoryStream bytes = new();

        bytes.Write(Domain);

        bytes.WriteByte(availability.FeatureEnabled ? (byte)1 : (byte)0);

        bytes.WriteByte((byte)availability.Canonical);

        Presence(bytes, availability.CanonicalSchemaVersion is not null);

        if (availability.CanonicalSchemaVersion is { } version) Int32(bytes, version);

        Presence(bytes, availability.CanonicalInstalledFingerprint is not null);

        if (availability.CanonicalInstalledFingerprint is { } fingerprint)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(fingerprint);

            Int32(bytes, encoded.Length);

            bytes.Write(encoded);
        }

        Presence(bytes, availability.DatasetGeneration is not null);

        if (availability.DatasetGeneration is { } dataset) bytes.Write(dataset.ToByteArray(bigEndian: true));

        long value = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(bytes.ToArray()));

        return (value & long.MaxValue) | 1;
    }

    private static void Presence(MemoryStream bytes, bool present) => bytes.WriteByte(present ? (byte)1 : (byte)0);

    private static void Int32(MemoryStream bytes, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32BigEndian(buffer, value);

        bytes.Write(buffer);
    }
}
