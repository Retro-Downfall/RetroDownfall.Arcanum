using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

internal enum AuditLogCursorDecodeResult
{

    Success,

    Invalid,

    QueryMismatch,

}

internal sealed record AuditLogCursorCheckpoint(
    DateTimeOffset SnapshotTo,
    int FileDateStamp,
    long BoundaryOffset,
    int IdentityPrefixLength,
    byte[] IdentityHash);

internal static class AuditLogCursorCodec
{

    private const byte Version = 1;

    private const int HashLength = 32;

    private const int TokenLength =
        1
        + HashLength
        + sizeof(long)
        + sizeof(int)
        + sizeof(long)
        + sizeof(int)
        + HashLength;

    private const int MaximumEncodedLength = ((TokenLength + 2) / 3) * 4;

    internal static byte[] CreateQueryFingerprint(
        string family,
        string fileFamilyIdentity,
        DateTimeOffset? from,
        DateTimeOffset? to,
        params string?[] filters)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendString(hash, family);

        AppendString(hash, fileFamilyIdentity);

        AppendDate(hash, from);

        AppendDate(hash, to);

        foreach (string? filter in filters)
        {

            AppendString(hash, filter);

        }

        return hash.GetHashAndReset();

    }

    internal static string Encode(
        ReadOnlySpan<byte> queryFingerprint,
        AuditLogCursorCheckpoint checkpoint)
    {

        if (queryFingerprint.Length != HashLength
            || checkpoint.IdentityHash.Length != HashLength)
        {

            throw new ArgumentException("Audit cursor hashes must be SHA-256 values.");

        }

        Span<byte> token = stackalloc byte[TokenLength];

        token[0] = Version;

        queryFingerprint.CopyTo(token.Slice(1, HashLength));

        int offset = 1 + HashLength;

        BinaryPrimitives.WriteInt64BigEndian(
            token.Slice(offset, sizeof(long)),
            checkpoint.SnapshotTo.ToUniversalTime().Ticks);

        offset += sizeof(long);

        BinaryPrimitives.WriteInt32BigEndian(
            token.Slice(offset, sizeof(int)),
            checkpoint.FileDateStamp);

        offset += sizeof(int);

        BinaryPrimitives.WriteInt64BigEndian(
            token.Slice(offset, sizeof(long)),
            checkpoint.BoundaryOffset);

        offset += sizeof(long);

        BinaryPrimitives.WriteInt32BigEndian(
            token.Slice(offset, sizeof(int)),
            checkpoint.IdentityPrefixLength);

        offset += sizeof(int);

        checkpoint.IdentityHash.CopyTo(token.Slice(offset, HashLength));

        return Convert.ToBase64String(token)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    }

    internal static AuditLogCursorDecodeResult TryDecode(
        string? cursor,
        ReadOnlySpan<byte> expectedQueryFingerprint,
        out AuditLogCursorCheckpoint? checkpoint)
    {

        checkpoint = null;

        if (cursor is null)
        {

            return AuditLogCursorDecodeResult.Success;

        }

        if (cursor.Length == 0
            || cursor.Length > MaximumEncodedLength
            || cursor.IndexOfAnyExcept(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) >= 0)
        {

            return AuditLogCursorDecodeResult.Invalid;

        }

        string base64 = cursor
            .Replace('-', '+')
            .Replace('_', '/');

        int padding = (4 - (base64.Length % 4)) % 4;

        if (padding > 0)
        {

            base64 = base64.PadRight(base64.Length + padding, '=');

        }

        Span<byte> token = stackalloc byte[TokenLength];

        if (!Convert.TryFromBase64String(base64, token, out int bytesWritten)
            || bytesWritten != TokenLength
            || token[0] != Version)
        {

            return AuditLogCursorDecodeResult.Invalid;

        }

        if (expectedQueryFingerprint.Length != HashLength
            || !CryptographicOperations.FixedTimeEquals(
                token.Slice(1, HashLength),
                expectedQueryFingerprint))
        {

            return AuditLogCursorDecodeResult.QueryMismatch;

        }

        int offset = 1 + HashLength;

        long snapshotTicks = BinaryPrimitives.ReadInt64BigEndian(
            token.Slice(offset, sizeof(long)));

        offset += sizeof(long);

        int fileDateStamp = BinaryPrimitives.ReadInt32BigEndian(
            token.Slice(offset, sizeof(int)));

        offset += sizeof(int);

        long boundaryOffset = BinaryPrimitives.ReadInt64BigEndian(
            token.Slice(offset, sizeof(long)));

        offset += sizeof(long);

        int identityPrefixLength = BinaryPrimitives.ReadInt32BigEndian(
            token.Slice(offset, sizeof(int)));

        offset += sizeof(int);

        if (snapshotTicks < DateTimeOffset.MinValue.Ticks
            || snapshotTicks > DateTimeOffset.MaxValue.Ticks
            || !IsValidDateStamp(fileDateStamp)
            || boundaryOffset < 0
            || identityPrefixLength is < 1 or > AuditLogPageReader.FileIdentityPrefixBytes)
        {

            return AuditLogCursorDecodeResult.Invalid;

        }

        checkpoint = new AuditLogCursorCheckpoint(
            new DateTimeOffset(snapshotTicks, TimeSpan.Zero),
            fileDateStamp,
            boundaryOffset,
            identityPrefixLength,
            token.Slice(offset, HashLength).ToArray());

        return AuditLogCursorDecodeResult.Success;

    }

    private static bool IsValidDateStamp(int dateStamp) =>
        DateTime.TryParseExact(
            dateStamp.ToString("D8", System.Globalization.CultureInfo.InvariantCulture),
            "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out _);

    private static void AppendDate(
        IncrementalHash hash,
        DateTimeOffset? value)
    {

        Span<byte> bytes = stackalloc byte[1 + sizeof(long)];

        bytes[0] = value.HasValue ? (byte)1 : (byte)0;

        BinaryPrimitives.WriteInt64BigEndian(
            bytes[1..],
            value?.ToUniversalTime().Ticks ?? 0L);

        hash.AppendData(bytes);

    }

    private static void AppendString(
        IncrementalHash hash,
        string? value)
    {

        if (value is null)
        {

            Span<byte> missing = stackalloc byte[sizeof(int)];

            BinaryPrimitives.WriteInt32BigEndian(missing, -1);

            hash.AppendData(missing);

            return;

        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);

        Span<byte> length = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);

        hash.AppendData(length);

        hash.AppendData(bytes);

    }

}
