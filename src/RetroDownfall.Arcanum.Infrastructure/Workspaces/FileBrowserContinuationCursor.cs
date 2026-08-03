using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

internal enum FileBrowserContinuationDecodeResult
{

    Success,

    Invalid,

    QueryMismatch,

}

internal sealed record FileBrowserContinuationCheckpoint(
    string RelativePath,
    FileEntryType EntryType)
{

    internal bool Matches(FileEntry entry)
    {

        return EntryType == entry.Type
            && FileBrowserContinuationCursor.PathComparer.Equals(
                RelativePath,
                FileBrowserContinuationCursor.NormalizeRelativePath(
                    entry.RelativePath));

    }

}

internal static class FileBrowserContinuationCursor
{

    private const byte Version = 1;

    private const int HashLength = 32;

    private const int TokenHeaderLength = 1 + HashLength + 1;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    internal static byte[] CreateQueryFingerprint(
        string workspaceRoot,
        string resolvedDirectory,
        bool recursive,
        string? searchPattern)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendString(hash, Path.GetFullPath(workspaceRoot));

        AppendString(hash, Path.GetFullPath(resolvedDirectory));

        hash.AppendData([recursive ? (byte)1 : (byte)0]);

        AppendString(hash, searchPattern);

        return hash.GetHashAndReset();

    }

    internal static string Encode(
        ReadOnlySpan<byte> queryFingerprint,
        FileEntry entry)
    {

        if (queryFingerprint.Length != HashLength)
        {

            throw new ArgumentException("File-browser cursor query hashes must be SHA-256 values.");

        }

        byte[] relativePath = Encoding.UTF8.GetBytes(
            NormalizeRelativePath(entry.RelativePath));

        byte[] token = new byte[TokenHeaderLength + relativePath.Length];

        token[0] = Version;

        queryFingerprint.CopyTo(token.AsSpan(1));

        token[1 + HashLength] = checked((byte)entry.Type);

        relativePath.CopyTo(token, TokenHeaderLength);

        return Convert.ToBase64String(token)
            .Replace('+', '-')
            .Replace('/', '_');

    }

    internal static FileBrowserContinuationDecodeResult TryDecode(
        string? cursor,
        ReadOnlySpan<byte> expectedQueryFingerprint,
        out FileBrowserContinuationCheckpoint? checkpoint)
    {

        checkpoint = null;

        if (cursor is null)
        {

            return FileBrowserContinuationDecodeResult.Success;

        }

        if (cursor.Length == 0)
        {

            return FileBrowserContinuationDecodeResult.Invalid;

        }

        string base64 = cursor
            .Replace('-', '+')
            .Replace('_', '/');

        byte[] token;

        try
        {

            token = Convert.FromBase64String(base64);

        }
        catch (FormatException)
        {

            return FileBrowserContinuationDecodeResult.Invalid;

        }

        if (token.Length <= TokenHeaderLength
            || token[0] != Version)
        {

            return FileBrowserContinuationDecodeResult.Invalid;

        }

        if (expectedQueryFingerprint.Length != HashLength
            || !CryptographicOperations.FixedTimeEquals(
                token.AsSpan(1, HashLength),
                expectedQueryFingerprint))
        {

            return FileBrowserContinuationDecodeResult.QueryMismatch;

        }

        FileEntryType entryType = (FileEntryType)token[1 + HashLength];

        if (!Enum.IsDefined(entryType))
        {

            return FileBrowserContinuationDecodeResult.Invalid;

        }

        string relativePath;

        try
        {

            relativePath = StrictUtf8.GetString(token.AsSpan(TokenHeaderLength));

        }
        catch (DecoderFallbackException)
        {

            return FileBrowserContinuationDecodeResult.Invalid;

        }

        if (relativePath.Length == 0
            || relativePath.Contains('\\'))
        {

            return FileBrowserContinuationDecodeResult.Invalid;

        }

        checkpoint = new FileBrowserContinuationCheckpoint(
            relativePath,
            entryType);

        return FileBrowserContinuationDecodeResult.Success;

    }

    internal static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');

    private static void AppendString(IncrementalHash hash, string? value)
    {

        if (value is null)
        {

            hash.AppendData([0]);

            return;

        }

        hash.AppendData([1]);

        hash.AppendData(Encoding.UTF8.GetBytes(value));

        hash.AppendData([0]);

    }

}
