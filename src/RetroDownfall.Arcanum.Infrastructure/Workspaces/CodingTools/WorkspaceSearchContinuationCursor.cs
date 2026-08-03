using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal enum WorkspaceSearchContinuationDecodeResult
{

    Success,

    Invalid,

    QueryMismatch,

}

internal sealed record WorkspaceSearchMatchCheckpoint(
    byte[] PathHash,
    byte[] LineContextHash,
    int Column,
    int MatchLength)
{

    internal bool Matches(
        ReadOnlySpan<byte> pathHash,
        ReadOnlySpan<byte> lineContextHash,
        int column,
        int matchLength) =>
        Column == column
        && MatchLength == matchLength
        && CryptographicOperations.FixedTimeEquals(PathHash, pathHash)
        && CryptographicOperations.FixedTimeEquals(LineContextHash, lineContextHash);

}

internal static class WorkspaceSearchContinuationCursor
{

    private const byte Version = 1;

    private const int HashLength = 32;

    private const int TokenLength = 1 + (HashLength * 3) + (sizeof(int) * 2);

    private const int EncodedTokenLength = 140;

    internal static byte[] CreateQueryFingerprint(
        WorkspaceSearchRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendString(hash, request.Pattern);

        AppendInt32(hash, (int)request.Mode);

        AppendInt32(hash, request.CaseSensitive ? 1 : 0);

        AppendString(hash, request.Root);

        AppendStrings(hash, request.Globs, cancellationToken);

        AppendStrings(hash, request.Extensions, cancellationToken);

        return hash.GetHashAndReset();

    }

    internal static byte[] CreatePathHash(string path) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(path));

    internal static byte[] CreateLineContextHash(
        ReadOnlySpan<byte> previousLineContextHash,
        ReadOnlySpan<char> line)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(previousLineContextHash);

        Encoder encoder = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true)
            .GetEncoder();

        Span<byte> encoded = stackalloc byte[4 * 1024];

        ReadOnlySpan<char> remaining = line;

        bool completed;

        do
        {

            encoder.Convert(
                remaining,
                encoded,
                flush: true,
                out int charactersUsed,
                out int bytesUsed,
                out completed);

            hash.AppendData(encoded[..bytesUsed]);

            remaining = remaining[charactersUsed..];

        } while (!completed);

        return hash.GetHashAndReset();

    }

    internal static string Encode(
        ReadOnlySpan<byte> queryFingerprint,
        ReadOnlySpan<byte> pathHash,
        ReadOnlySpan<byte> lineContextHash,
        int column,
        int matchLength)
    {

        if (queryFingerprint.Length != HashLength
            || pathHash.Length != HashLength
            || lineContextHash.Length != HashLength)
        {

            throw new ArgumentException("Workspace-search cursor hashes must be SHA-256 values.");

        }

        Span<byte> token = stackalloc byte[TokenLength];

        token[0] = Version;

        queryFingerprint.CopyTo(token[1..]);

        pathHash.CopyTo(token[(1 + HashLength)..]);

        lineContextHash.CopyTo(token[(1 + (HashLength * 2))..]);

        BinaryPrimitives.WriteInt32BigEndian(
            token[(1 + (HashLength * 3))..],
            column);

        BinaryPrimitives.WriteInt32BigEndian(
            token[(1 + (HashLength * 3) + sizeof(int))..],
            matchLength);

        return Convert.ToBase64String(token)
            .Replace('+', '-')
            .Replace('/', '_');

    }

    internal static WorkspaceSearchContinuationDecodeResult TryDecode(
        string? cursor,
        ReadOnlySpan<byte> expectedQueryFingerprint,
        out WorkspaceSearchMatchCheckpoint? checkpoint)
    {

        checkpoint = null;

        if (cursor is null)
        {

            return WorkspaceSearchContinuationDecodeResult.Success;

        }

        if (cursor.Length != EncodedTokenLength)
        {

            return WorkspaceSearchContinuationDecodeResult.Invalid;

        }

        Span<byte> token = stackalloc byte[TokenLength];

        string base64 = cursor
            .Replace('-', '+')
            .Replace('_', '/');

        if (!Convert.TryFromBase64String(base64, token, out int bytesWritten)
            || bytesWritten != TokenLength
            || token[0] != Version)
        {

            return WorkspaceSearchContinuationDecodeResult.Invalid;

        }

        ReadOnlySpan<byte> queryFingerprint = token.Slice(1, HashLength);

        if (expectedQueryFingerprint.Length != HashLength
            || !CryptographicOperations.FixedTimeEquals(
                queryFingerprint,
                expectedQueryFingerprint))
        {

            return WorkspaceSearchContinuationDecodeResult.QueryMismatch;

        }

        int column = BinaryPrimitives.ReadInt32BigEndian(
            token[(1 + (HashLength * 3))..]);

        int matchLength = BinaryPrimitives.ReadInt32BigEndian(
            token[(1 + (HashLength * 3) + sizeof(int))..]);

        if (column <= 0 || matchLength < 0)
        {

            return WorkspaceSearchContinuationDecodeResult.Invalid;

        }

        checkpoint = new WorkspaceSearchMatchCheckpoint(
            token.Slice(1 + HashLength, HashLength).ToArray(),
            token.Slice(1 + (HashLength * 2), HashLength).ToArray(),
            column,
            matchLength);

        return WorkspaceSearchContinuationDecodeResult.Success;

    }

    private static void AppendStrings(
        IncrementalHash hash,
        IReadOnlyList<string>? values,
        CancellationToken cancellationToken)
    {

        AppendInt32(hash, values?.Count ?? 0);

        if (values is null)
        {

            return;

        }

        foreach (string value in values)
        {

            cancellationToken.ThrowIfCancellationRequested();

            AppendString(hash, value);

            cancellationToken.ThrowIfCancellationRequested();

        }

    }

    private static void AppendString(IncrementalHash hash, string? value)
    {

        if (value is null)
        {

            AppendInt32(hash, -1);

            return;

        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);

        AppendInt32(hash, bytes.Length);

        hash.AppendData(bytes);

    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {

        Span<byte> bytes = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32BigEndian(bytes, value);

        hash.AppendData(bytes);

    }

}
