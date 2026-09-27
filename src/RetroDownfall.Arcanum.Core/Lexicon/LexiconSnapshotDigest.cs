using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Lexicon;

/// <summary>
/// The durable format-2 identity of one canonical Lexicon type and ordered fact array.
/// </summary>
public static class LexiconSnapshotDigest
{
    public const byte FormatCode = 0x02;

    private static readonly byte[] Domain = "Arcanum.Lexicon.Snapshot.v2\0"u8.ToArray();

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Encodes the exact bytes hashed by both snapshot and sensitivity-label identity.</summary>
    public static byte[] Encode(LexiconCanonicalValue value) =>
        Encode(FormatCode, value);

    /// <summary>Computes the raw SHA-256 format-2 snapshot identity.</summary>
    public static byte[] Compute(LexiconCanonicalValue value) =>
        SHA256.HashData(Encode(value));

    /// <summary>Computes the uppercase hexadecimal SHA-256 format-2 snapshot identity.</summary>
    public static string ComputeHex(LexiconCanonicalValue value) =>
        Convert.ToHexString(Compute(value));

    /// <summary>
    /// Computes the Covenant-derived artifact identity from the same canonical snapshot bytes.
    /// </summary>
    public static CovenantDigest ComputeDerivedArtifactContentDigest(LexiconCanonicalValue value) =>
        DerivedArtifactContentDigest.ForBytes(Encode(value));

    internal static byte[] Encode(byte format, LexiconCanonicalValue value)
    {

        ArgumentNullException.ThrowIfNull(value);

        if (format != FormatCode)
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Only Lexicon snapshot format 2 is defined.");
        }

        ArgumentNullException.ThrowIfNull(value.Type);
        ArgumentNullException.ThrowIfNull(value.Facts);

        byte[] typeBytes = StrictUtf8.GetBytes(value.Type);
        byte[][] factBytes = new byte[value.Facts.Length][];
        int capacity;

        try
        {
            capacity = checked(Domain.Length + sizeof(byte) + sizeof(uint) + typeBytes.Length + sizeof(uint));

            for (int index = 0; index < value.Facts.Length; index++)
            {
                string fact = value.Facts[index]
                    ?? throw new ArgumentException("A canonical Lexicon fact cannot be null.", nameof(value));

                byte[] encoded = StrictUtf8.GetBytes(fact);

                factBytes[index] = encoded;
                capacity = checked(capacity + sizeof(uint) + encoded.Length);
            }
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "The canonical Lexicon snapshot exceeds the encoder's supported byte length.");
        }

        using MemoryStream destination = new(capacity);

        destination.Write(Domain);
        destination.WriteByte(format);
        WriteByteLength(destination, checked((ulong)typeBytes.LongLength));
        destination.Write(typeBytes);
        WriteFactCount(destination, checked((ulong)value.Facts.LongLength));

        foreach (byte[] fact in factBytes)
        {
            WriteByteLength(destination, checked((ulong)fact.LongLength));
            destination.Write(fact);
        }

        return destination.ToArray();

    }

    internal static void WriteFactCount(Stream destination, ulong count) =>
        WriteUnsigned32(destination, count, nameof(count));

    internal static void WriteByteLength(Stream destination, ulong byteLength) =>
        WriteUnsigned32(destination, byteLength, nameof(byteLength));

    private static void WriteUnsigned32(Stream destination, ulong value, string parameterName)
    {

        ArgumentNullException.ThrowIfNull(destination);

        if (value > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        Span<byte> bytes = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)value));
        destination.Write(bytes);

    }

}
