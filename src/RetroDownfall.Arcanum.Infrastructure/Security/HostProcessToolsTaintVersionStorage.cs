using System.Buffers.Binary;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The backward-compatible SQLite representation of a taint-time master-key version.
/// </summary>
internal static class HostProcessToolsTaintVersionStorage
{

    private const int EncodedLength = 8;

    internal static byte[] Encode(ulong value)
    {

        if (value == 0)
        {

            throw new ArgumentOutOfRangeException(nameof(value));

        }

        byte[] encoded = new byte[EncodedLength];

        BinaryPrimitives.WriteUInt64BigEndian(encoded, value);

        return encoded;

    }

    internal static bool TryDecode(object value, out ulong? decoded)
    {

        switch (value)
        {

            case DBNull:

                decoded = null;

                return true;

            case long legacy when legacy > 0:

                decoded = (ulong)legacy;

                return true;

            case byte[] { Length: EncodedLength } encoded:

                ulong version = BinaryPrimitives.ReadUInt64BigEndian(encoded);

                if (version > 0)
                {

                    decoded = version;

                    return true;

                }

                break;

        }

        decoded = null;

        return false;

    }

}
