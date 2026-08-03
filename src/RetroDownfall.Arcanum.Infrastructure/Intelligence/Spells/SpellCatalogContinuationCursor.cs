using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

internal enum SpellCatalogCursorDecodeResult
{

    Success,

    Invalid,

    QueryMismatch,

}

internal sealed record SpellCatalogCheckpoint(
    string Name,
    SpellSource Source,
    byte[] IdentityHash);

internal static class SpellCatalogContinuationCursor
{

    private const byte Version = 1;

    private const int HashLength = 32;

    private const int FixedTokenLength = 1 + HashLength + HashLength + 1 + sizeof(int);

    internal const int MaximumNameBytes = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static byte[] CreateQueryFingerprint(
        string? workingDirectory,
        SpellCatalogQuery query)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);

        AppendString(hash, NormalizePath(workingDirectory));

        AppendString(hash, NormalizeFilter(query.Query));

        AppendString(hash, NormalizeFilter(query.Tag));

        AppendString(hash, NormalizeFilter(query.Tool));

        AppendInt32(hash, query.Source is null ? -1 : (int)query.Source.Value);

        return hash.GetHashAndReset();

    }

    internal static byte[] CreateIdentityHash(
        SpellSource source,
        string name,
        string path)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);

        AppendInt32(hash, (int)source);

        AppendString(hash, name);

        AppendString(hash, NormalizePath(path));

        return hash.GetHashAndReset();

    }

    internal static bool TryEncode(
        ReadOnlySpan<byte> queryFingerprint,
        string name,
        SpellSource source,
        ReadOnlySpan<byte> identityHash,
        out string? cursor,
        out int nameByteCount)
    {

        cursor = null;

        nameByteCount = 0;

        if (queryFingerprint.Length != HashLength
            || identityHash.Length != HashLength)
        {

            throw new ArgumentException(
                "Spell-catalog cursor hashes must be SHA-256 values.");

        }

        nameByteCount = StrictUtf8.GetByteCount(name);

        if (nameByteCount > MaximumNameBytes)
        {

            return false;

        }

        byte[] nameBytes = StrictUtf8.GetBytes(name);

        byte[] token = new byte[FixedTokenLength + nameBytes.Length];

        token[0] = Version;

        queryFingerprint.CopyTo(token.AsSpan(1, HashLength));

        identityHash.CopyTo(token.AsSpan(1 + HashLength, HashLength));

        token[1 + (HashLength * 2)] = checked((byte)source);

        BinaryPrimitives.WriteInt32BigEndian(
            token.AsSpan(1 + (HashLength * 2) + 1, sizeof(int)),
            nameBytes.Length);

        nameBytes.CopyTo(token.AsSpan(FixedTokenLength));

        cursor = Convert.ToBase64String(token)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return true;

    }

    internal static SpellCatalogCursorDecodeResult TryDecode(
        string? cursor,
        ReadOnlySpan<byte> expectedQueryFingerprint,
        out SpellCatalogCheckpoint? checkpoint)
    {

        checkpoint = null;

        if (cursor is null)
        {

            return SpellCatalogCursorDecodeResult.Success;

        }

        if (cursor.Length == 0
            || cursor.Length > ((FixedTokenLength + MaximumNameBytes + 2) / 3) * 4
            || cursor.IndexOfAnyExcept(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) >= 0)
        {

            return SpellCatalogCursorDecodeResult.Invalid;

        }

        string base64 = cursor
            .Replace('-', '+')
            .Replace('_', '/');

        int padding = (4 - (base64.Length % 4)) % 4;

        if (padding > 0)
        {

            base64 = base64.PadRight(base64.Length + padding, '=');

        }

        byte[] token;

        try
        {

            token = Convert.FromBase64String(base64);

        }
        catch (FormatException)
        {

            return SpellCatalogCursorDecodeResult.Invalid;

        }

        if (token.Length < FixedTokenLength
            || token[0] != Version)
        {

            return SpellCatalogCursorDecodeResult.Invalid;

        }

        ReadOnlySpan<byte> queryFingerprint = token.AsSpan(1, HashLength);

        if (expectedQueryFingerprint.Length != HashLength
            || !CryptographicOperations.FixedTimeEquals(
                queryFingerprint,
                expectedQueryFingerprint))
        {

            return SpellCatalogCursorDecodeResult.QueryMismatch;

        }

        int sourceValue = token[1 + (HashLength * 2)];

        int nameLength = BinaryPrimitives.ReadInt32BigEndian(
            token.AsSpan(1 + (HashLength * 2) + 1, sizeof(int)));

        if (sourceValue is not ((int)SpellSource.Builtin)
                and not ((int)SpellSource.Workspace)
            || nameLength <= 0
            || nameLength > MaximumNameBytes
            || token.Length != FixedTokenLength + nameLength)
        {

            return SpellCatalogCursorDecodeResult.Invalid;

        }

        string name;

        try
        {

            name = StrictUtf8.GetString(token.AsSpan(FixedTokenLength));

        }
        catch (DecoderFallbackException)
        {

            return SpellCatalogCursorDecodeResult.Invalid;

        }

        if (string.IsNullOrWhiteSpace(name))
        {

            return SpellCatalogCursorDecodeResult.Invalid;

        }

        checkpoint = new SpellCatalogCheckpoint(
            name,
            (SpellSource)sourceValue,
            token.AsSpan(1 + HashLength, HashLength).ToArray());

        return SpellCatalogCursorDecodeResult.Success;

    }

    private static string? NormalizeFilter(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static string? NormalizePath(string? value)
    {

        if (string.IsNullOrWhiteSpace(value))
        {

            return null;

        }

        try
        {

            return Path.GetFullPath(value.Trim());

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {

            return value.Trim();

        }

    }

    private static void AppendString(
        IncrementalHash hash,
        string? value)
    {

        if (value is null)
        {

            AppendInt32(hash, -1);

            return;

        }

        byte[] bytes = StrictUtf8.GetBytes(value);

        AppendInt32(hash, bytes.Length);

        hash.AppendData(bytes);

    }

    private static void AppendInt32(
        IncrementalHash hash,
        int value)
    {

        Span<byte> bytes = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32BigEndian(bytes, value);

        hash.AppendData(bytes);

    }

}
