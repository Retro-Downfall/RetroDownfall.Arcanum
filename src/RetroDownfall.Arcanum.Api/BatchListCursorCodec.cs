using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api;

internal static class BatchListCursorCodec
{

    private const byte Version = 1;

    private const int ScopeHashBytes = 32;

    private const int TimestampBytes = sizeof(long);

    private const int GuidBytes = 16;

    private const int CursorBytes = 1 + ScopeHashBytes + TimestampBytes + GuidBytes;

    internal static string Encode(

        string? status,

        BatchListPosition position)

    {

        Span<byte> cursor = stackalloc byte[CursorBytes];

        cursor[0] = Version;

        WriteScopeHash(status, cursor.Slice(1, ScopeHashBytes));

        BinaryPrimitives.WriteInt64BigEndian(

            cursor.Slice(1 + ScopeHashBytes, TimestampBytes),

            position.CreatedAt.ToUniversalTime().Ticks);

        if (!position.Id.TryWriteBytes(

                cursor.Slice(1 + ScopeHashBytes + TimestampBytes, GuidBytes),

                bigEndian: true,

                out int bytesWritten)

            || bytesWritten != GuidBytes)

        {

            throw new InvalidOperationException("The batch cursor identity could not be encoded.");

        }

        return Convert.ToBase64String(cursor)

            .TrimEnd('=')

            .Replace('+', '-')

            .Replace('/', '_');

    }

    internal static bool TryDecode(

        string? cursor,

        string? status,

        out BatchListPosition? position)

    {

        position = null;

        if (string.IsNullOrWhiteSpace(cursor))

        {

            return false;

        }

        byte[] bytes;

        try

        {

            string normalized = cursor.Replace('-', '+').Replace('_', '/');

            normalized = normalized.PadRight(

                normalized.Length + ((4 - (normalized.Length % 4)) % 4),

                '=');

            bytes = Convert.FromBase64String(normalized);

        }

        catch (FormatException)

        {

            return false;

        }

        if (bytes.Length != CursorBytes || bytes[0] != Version)

        {

            return false;

        }

        Span<byte> expectedScopeHash = stackalloc byte[ScopeHashBytes];

        WriteScopeHash(status, expectedScopeHash);

        if (!CryptographicOperations.FixedTimeEquals(

                bytes.AsSpan(1, ScopeHashBytes),

                expectedScopeHash))

        {

            return false;

        }

        long ticks = BinaryPrimitives.ReadInt64BigEndian(

            bytes.AsSpan(1 + ScopeHashBytes, TimestampBytes));

        if (ticks < DateTimeOffset.MinValue.Ticks

            || ticks > DateTimeOffset.MaxValue.Ticks)

        {

            return false;

        }

        Guid id = new(

            bytes.AsSpan(1 + ScopeHashBytes + TimestampBytes, GuidBytes),

            bigEndian: true);

        position = new BatchListPosition(

            new DateTimeOffset(ticks, TimeSpan.Zero),

            id);

        return true;

    }

    private static void WriteScopeHash(

        string? status,

        Span<byte> destination)

    {

        byte[] scope = Encoding.UTF8.GetBytes(NormalizeStatus(status));

        if (!SHA256.TryHashData(scope, destination, out int bytesWritten)

            || bytesWritten != ScopeHashBytes)

        {

            throw new InvalidOperationException("The batch cursor query scope could not be encoded.");

        }

    }

    private static string NormalizeStatus(string? status) =>

        string.IsNullOrWhiteSpace(status)

            ? string.Empty

            : status.Trim();

}
