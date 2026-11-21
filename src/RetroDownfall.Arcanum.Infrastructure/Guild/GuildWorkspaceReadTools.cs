using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using static RetroDownfall.Arcanum.Infrastructure.Guild.GuildWorkspaceAuthority;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildWorkspaceReadPage(string Content, string? Continuation);

internal sealed record GuildWorkspaceListPage(string[] Paths, string? Continuation);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuildWorkspaceReadPage))]
[JsonSerializable(typeof(GuildWorkspaceListPage))]
internal sealed partial class GuildWorkspaceReadJsonContext : JsonSerializerContext;

/// <summary>Qualifies one in-process result page against live Guild authority and physical source evidence.</summary>
internal static class GuildWorkspaceReadTools
{
    private const int ListPageSize = 256;

    private const int ReadCursorBytes = sizeof(long) * 2 + sizeof(int) + SHA256.HashSizeInBytes;

    private static Error InvalidArguments => new(ErrorCodes.Guild.InvalidRequest, "The workspace tool arguments are invalid.");

    private static Error InvalidCursor => new(ErrorCodes.Guild.InvalidRequest,
        "The continuation is invalid for this live workspace call. Restart without the continuation.");

    private static Error Forbidden => new(ErrorCodes.Guild.Forbidden, "The resource is outside the granted physical workspace scope.");

    private static Error Unavailable => new(ErrorCodes.Guild.WorkspaceIdentityUnavailable,
        "The granted physical workspace resource is unavailable or changed.");

    private static Error TooLarge => new(ErrorCodes.Guild.PayloadTooLarge,
        "The response allocation cannot fit one result and its continuation.");

    internal static async Task<Result<string>> ExecuteAsync(string toolName, JsonElement arguments,
        GuildWorkspaceAuthority physical, GuildGrant grant, Guid workspaceId, long responseAllocationBytes,
        WorkspaceSearchSettings searchSettings, ReadOnlyMemory<byte> cursorKey,
        Func<CancellationToken, Task<Result>> reauthorize, CancellationToken cancellationToken,
        Action<Func<CancellationToken, Task<Result>>>? retainProof = null)
    {
        string[] allowedArguments = toolName switch
        {
            "read_file" => ["relativePath", "startLine", "endLine", "continuation"],
            "list_directory" => ["relativePath", "recursive", "continuation"],
            "search_workspace" => ["pattern", "mode", "caseSensitive", "root", "globs", "extensions", "cursor"],
            _ => [],
        };

        if (allowedArguments.Length == 0 || !GuildTaskArguments.Closed(arguments, allowedArguments)
            || cursorKey.Length < SHA256.HashSizeInBytes
            || arguments.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            return InvalidArguments;
        }

        if (responseAllocationBytes <= 0)
        {
            return TooLarge;
        }

        Result live = await reauthorize(cancellationToken).ConfigureAwait(false);

        if (live.IsFailure)
        {
            return live.Error;
        }

        try
        {
            Result<DirectReadScope> opened = await physical.OpenDirectReadScopeAsync(grant, workspaceId, cancellationToken).ConfigureAwait(false);

            if (opened.IsFailure)
            {
                return opened.Error;
            }

            DirectReadScope scope = opened.Value;

            byte[] toolKey = scope.BindCursorKey(cursorKey.Span, toolName);

            Result<ResultPage> page = toolName switch
            {
                "read_file" => await ReadAsync(scope, arguments, responseAllocationBytes, toolKey, cancellationToken).ConfigureAwait(false),
                "list_directory" => List(scope, arguments, responseAllocationBytes, toolKey, cancellationToken),
                "search_workspace" => await SearchAsync(scope, arguments, responseAllocationBytes, toolKey, searchSettings, cancellationToken).ConfigureAwait(false),
                _ => InvalidArguments,
            };

            if (page.IsFailure)
            {
                return page.Error;
            }

            live = await reauthorize(cancellationToken).ConfigureAwait(false);

            if (live.IsFailure)
            {
                return live.Error;
            }

            DirectReadProof[] proofs = page.Value.Proofs;

            Task<Result> ValidatePhysicalAsync(CancellationToken token) => scope.ValidateAsync(proofs, token);

            Result persisted = await ValidatePhysicalAsync(cancellationToken).ConfigureAwait(false);

            if (persisted.IsFailure)
            {
                return persisted.Error;
            }

            retainProof?.Invoke(ValidatePhysicalAsync);

            return page.Value.Text;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or OverflowException)
        {
            return InvalidArguments;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Unavailable;
        }
    }

    private static async Task<Result<ResultPage>> ReadAsync(DirectReadScope scope, JsonElement arguments,
        long allocation, byte[] toolKey, CancellationToken cancellationToken)
    {
        ReadFileChunkParams? args = JsonSerializer.Deserialize(arguments, McpJsonSerializerContext.Default.ReadFileChunkParams);

        if (args?.RelativePath is not { } path || !IsDirectFilePath(path)
            || args.StartLine < 1 || args.EndLine < args.StartLine)
        {
            return InvalidArguments;
        }

        JsonElement continuation = arguments.TryGetProperty("continuation", out JsonElement token) ? token : default;

        if (continuation.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
        {
            return InvalidArguments;
        }

        string? cursor = continuation.ValueKind == JsonValueKind.String ? continuation.GetString() : null;

        CursorCodec codec = new(toolKey, Encoding.UTF8.GetBytes(FormattableString.Invariant($"{path}\0{args.StartLine}\0{args.EndLine}")));

        if (!codec.TryRead(cursor, ReadCursorBytes, out byte[]? resume))
        {
            return InvalidCursor;
        }

        string? expectedFingerprint = resume is null ? null : Convert.ToHexString(resume.AsSpan(sizeof(long) * 2 + sizeof(int)));

        using DirectFile? opened = scope.OpenFile(path, expectedFingerprint);

        if (opened is null)
        {
            return Forbidden;
        }

        long offset = resume is null ? 0 : BinaryPrimitives.ReadInt64BigEndian(resume);

        int line = resume is null ? 1 : BinaryPrimitives.ReadInt32BigEndian(resume.AsSpan(sizeof(long) * 2));

        SafeFileHandle handle = opened.Stream.SafeFileHandle;

        if (resume is not null && (offset <= 0 || offset >= RandomAccess.GetLength(handle)
            || line < args.StartLine || line > args.EndLine
            || BinaryPrimitives.ReadInt64BigEndian(resume.AsSpan(sizeof(long))) != opened.Modified))
        {
            return InvalidCursor;
        }

        long finalBudget = allocation - Encoding.UTF8.GetByteCount(SerializeRead("", null));

        if (finalBudget < 0)
        {
            return TooLarge;
        }

        long continuedBudget = allocation - Encoding.UTF8.GetByteCount(SerializeRead("", codec.Write(new byte[ReadCursorBytes])));

        StringBuilder content = new();

        Utf8RuneReader reader = new(handle, offset);

        if (resume is null && await reader.PeekAsync(cancellationToken).ConfigureAwait(false) is { Value: 0xFEFF } preamble)
        {
            reader.Consume(preamble);
        }

        long readStart = reader.Position;

        long nextOffset = readStart;

        long encodedBytes = 0;

        int keptChars = 0;

        int nextLine = line;

        bool more = false;

        char[] scalar = new char[2];

        while (await reader.PeekAsync(cancellationToken).ConfigureAwait(false) is Rune rune)
        {
            int runeLine = line;

            bool lineEnd = rune.Value is '\r' or '\n';

            if (lineEnd && line == args.EndLine)
            {
                break;
            }

            reader.Consume(rune);

            bool crlf = rune.Value == '\r' && await reader.PeekAsync(cancellationToken).ConfigureAwait(false) is { Value: '\n' };

            if (crlf)
            {
                reader.Consume(new Rune('\n'));
            }

            if (lineEnd)
            {
                if (await reader.PeekAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    // As in an ordinary line-range read, the final selected line's terminator
                    // is outside the returned range, even when the requested end is beyond EOF.
                    break;
                }

                line++;
            }

            if (runeLine < args.StartLine)
            {
                readStart = reader.Position;

                continue;
            }

            int cost = JsonEscapedByteCount(rune) + (crlf ? 2 : 0);

            if (cost > finalBudget - encodedBytes)
            {
                more = true;

                break;
            }

            content.Append(scalar, 0, rune.EncodeToUtf16(scalar));

            if (crlf)
            {
                content.Append('\n');
            }

            encodedBytes += cost;

            if (encodedBytes <= continuedBudget)
            {
                // The next token is always a Unicode scalar or a whole terminator. The cursor
                // restores the native byte position and logical line without a prefix reread.
                nextOffset = reader.Position;

                nextLine = line;

                keptChars = content.Length;
            }
        }

        if (!opened.Matches())
        {
            return Unavailable;
        }

        if (more && keptChars == 0)
        {
            return TooLarge;
        }

        string? next = null;

        if (more)
        {
            byte[] checkpoint = new byte[ReadCursorBytes];

            BinaryPrimitives.WriteInt64BigEndian(checkpoint, nextOffset);

            BinaryPrimitives.WriteInt64BigEndian(checkpoint.AsSpan(sizeof(long)), opened.Modified);

            BinaryPrimitives.WriteInt32BigEndian(checkpoint.AsSpan(sizeof(long) * 2), nextLine);

            Convert.FromHexString(opened.Fingerprint).CopyTo(checkpoint, sizeof(long) * 2 + sizeof(int));

            next = codec.Write(checkpoint);

            content.Length = keptChars;
        }

        string literal = content.ToString();

        byte[] bytes = Encoding.UTF8.GetBytes(literal);

        DirectReadContentProof contentProof = new(readStart, bytes.Length, SHA256.HashData(bytes));

        string text = SerializeRead(literal, next);

        return Encoding.UTF8.GetByteCount(text) <= allocation
            ? new ResultPage(text, [new(path, false, opened.Fingerprint, opened.Modified, contentProof)])
            : TooLarge;
    }

    private static int JsonEscapedByteCount(Rune rune) =>
        rune.Value is '\b' or '\t' or '\n' or '\f' or '\r' or '\\' ? 2
            : JavaScriptEncoder.Default.WillEncode(rune.Value) ? rune.Utf16SequenceLength * 6 : rune.Utf8SequenceLength;

    private static string SerializeRead(string content, string? continuation) => JsonSerializer.Serialize(
        new GuildWorkspaceReadPage(content, continuation), GuildWorkspaceReadJsonContext.Default.GuildWorkspaceReadPage);

    private sealed class Utf8RuneReader(SafeFileHandle handle, long offset)
    {
        private readonly byte[] _buffer = new byte[4096];

        private int _index;

        private int _count;

        private bool _atEnd;

        internal long Position { get; private set; } = offset;

        internal async ValueTask<Rune?> PeekAsync(CancellationToken cancellationToken)
        {
            int available = _count - _index;

            if (available < 4 && !_atEnd)
            {
                _buffer.AsSpan(_index, available).CopyTo(_buffer);

                while (available < 4)
                {
                    int read = await FileHandleIdentityInterop.ReadAtOffsetAsync(handle,
                        _buffer.AsMemory(available), Position + available, cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                    {
                        _atEnd = true;

                        break;
                    }

                    available += read;
                }

                _count = available;

                _index = 0;
            }

            if (_count == _index)
            {
                return null;
            }

            if (Rune.DecodeFromUtf8(_buffer.AsSpan(_index, _count - _index), out Rune rune, out _)
                != OperationStatus.Done)
            {
                throw new DecoderFallbackException();
            }

            return rune;
        }

        internal void Consume(Rune rune)
        {
            _index += rune.Utf8SequenceLength;

            Position += rune.Utf8SequenceLength;
        }
    }

    private static Result<ResultPage> List(DirectReadScope scope, JsonElement arguments, long allocation,
        byte[] toolKey, CancellationToken cancellationToken)
    {
        ListDirectoryParams? args = JsonSerializer.Deserialize(arguments, McpJsonSerializerContext.Default.ListDirectoryParams);

        if (args?.RelativePath is not { } path || !IsDirectDirectoryPath(path))
        {
            return InvalidArguments;
        }

        if (scope.SelectRoots(path).Length == 0)
        {
            return Forbidden;
        }

        CursorCodec codec = new(toolKey, Encoding.UTF8.GetBytes(path + (args.Recursive ? "\0recursive" : "\0flat")));

        if (!codec.TryRead(args.Continuation, SHA256.HashSizeInBytes, out byte[]? after))
        {
            return InvalidCursor;
        }

        long finalBudget = allocation - Encoding.UTF8.GetByteCount(SerializeList([], null));

        long continuedBudget = allocation - Encoding.UTF8.GetByteCount(SerializeList([], codec.Write(new byte[SHA256.HashSizeInBytes])));

        List<DirectReadProof> items = [];

        int continuedCount = 0;

        long encodedBytes = 0;

        GuildWorkspaceTree tree = new(scope);

        bool resumed = after is null;

        bool more = false;

        foreach (DirectReadProof proof in tree.Walk(path, args.Recursive, listing: true, _ => true, cancellationToken))
        {
            if (!resumed)
            {
                resumed = CryptographicOperations.FixedTimeEquals(after!, ListCheckpoint(proof));

                continue;
            }

            int cost = JsonEncodedText.Encode(proof.Path).EncodedUtf8Bytes.Length + 2 + (items.Count > 0 ? 1 : 0);

            if (items.Count == ListPageSize || cost > finalBudget - encodedBytes)
            {
                more = true;

                break;
            }

            items.Add(proof);

            encodedBytes += cost;

            if (encodedBytes <= continuedBudget)
            {
                continuedCount = items.Count;
            }
        }

        if (!resumed)
        {
            return InvalidCursor;
        }

        if (more)
        {
            if (continuedCount == 0)
            {
                return TooLarge;
            }

            items.RemoveRange(continuedCount, items.Count - continuedCount);
        }

        string result = SerializeList([.. items.Select(item => item.Path)], more ? codec.Write(ListCheckpoint(items[^1])) : null);

        return Encoding.UTF8.GetByteCount(result) <= allocation
            ? new ResultPage(result, [.. tree.RootProofs, .. items])
            : TooLarge;
    }

    private static byte[] ListCheckpoint(DirectReadProof proof) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(proof.Path + "\0" + proof.Fingerprint));

    private static string SerializeList(string[] paths, string? continuation) => JsonSerializer.Serialize(
        new GuildWorkspaceListPage(paths, continuation), GuildWorkspaceReadJsonContext.Default.GuildWorkspaceListPage);

    private static async Task<Result<ResultPage>> SearchAsync(DirectReadScope scope, JsonElement arguments, long allocation,
        byte[] toolKey, WorkspaceSearchSettings searchSettings, CancellationToken cancellationToken)
    {
        SearchWorkspaceParams? args = JsonSerializer.Deserialize(arguments, McpJsonSerializerContext.Default.SearchWorkspaceParams);

        WorkspaceSearchMode? mode = args?.Mode switch
        {
            "literal" => WorkspaceSearchMode.Literal,
            "regex" => WorkspaceSearchMode.Regex,
            _ => null,
        };

        string root = args?.Root ?? ".";

        if (args?.Pattern is not { } pattern || mode is null || !IsDirectDirectoryPath(root))
        {
            return InvalidArguments;
        }

        if (scope.SelectRoots(root).Length == 0)
        {
            return Forbidden;
        }

        WorkspaceSearchRequest request = new()
        {
            Pattern = pattern,
            Mode = mode.Value,
            CaseSensitive = args.CaseSensitive,
            Root = root,
            Globs = args.Globs ?? [],
            Extensions = args.Extensions ?? [],
        };

        CursorCodec codec = new(toolKey, WorkspaceSearchContinuationCursor.CreateQueryFingerprint(request, cancellationToken));

        if (!codec.TryRead(args.Cursor, WorkspaceSearchContinuationCursor.EncodedTokenLength, out byte[]? resume))
        {
            return InvalidCursor;
        }

        if (resume is not null)
        {
            request = request with { Cursor = Encoding.ASCII.GetString(resume) };
        }

        GuildWorkspaceTree tree = new(scope);

        WorkspaceSearchEngine engine = new(searchSettings);

        WorkspaceSearchToolResultEnvelope found = await engine.SearchAsync(scope.WorkspaceRoot, request, cancellationToken, tree)
            .ConfigureAwait(false);

        if (found.Status == "invalid_cursor")
        {
            return InvalidCursor;
        }

        string Serialize(WorkspaceSearchToolResultEnvelope page) =>
            JsonSerializer.Serialize(page with
            {
                NextCursor = page.NextCursor is null ? null : codec.Write(Encoding.ASCII.GetBytes(page.NextCursor)),
                PageStartCursor = null,
                MatchCursors = null,
            }, McpJsonSerializerContext.Default.WorkspaceSearchToolResultEnvelope);

        string best = Serialize(found);

        int count = found.Matches.Length;

        if (Encoding.UTF8.GetByteCount(best) > allocation)
        {
            int low = 1;

            int high = count - 1;

            count = 0;

            while (low <= high)
            {
                int middle = low + (high - low) / 2;

                string candidate = Serialize(found.RetainLeadingItems(middle));

                if (Encoding.UTF8.GetByteCount(candidate) <= allocation)
                {
                    count = middle;

                    best = candidate;

                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (count == 0)
            {
                return TooLarge;
            }
        }

        return new ResultPage(best, [.. tree.RootProofs,
            .. found.Matches.Take(count).Select(match => match.Path).Distinct(StringComparer.Ordinal).Select(tree.MatchProof)]);
    }

    private sealed record ResultPage(string Text, DirectReadProof[] Proofs);

    private sealed class CursorCodec(byte[] toolKey, byte[] requestFingerprint)
    {
        private readonly byte[] _key = HMACSHA256.HashData(toolKey, requestFingerprint);

        internal string Write(ReadOnlySpan<byte> payload)
        {
            Span<byte> token = stackalloc byte[payload.Length + SHA256.HashSizeInBytes];

            payload.CopyTo(token);

            HMACSHA256.HashData(_key, payload, token[payload.Length..]);

            return Encode(token);
        }

        private static string Encode(ReadOnlySpan<byte> token) => Convert.ToBase64String(token).Replace('+', '-').Replace('/', '_');

        internal bool TryRead(string? cursor, int payloadLength, out byte[]? payload)
        {
            payload = null;

            if (cursor is null)
            {
                return true;
            }

            int tokenLength = payloadLength + SHA256.HashSizeInBytes;

            if (cursor.Length != (tokenLength + 2) / 3 * 4)
            {
                return false;
            }

            Span<byte> token = stackalloc byte[tokenLength];

            if (!Convert.TryFromBase64String(cursor.Replace('-', '+').Replace('_', '/'), token, out int written)
                || written != token.Length || !string.Equals(cursor, Encode(token), StringComparison.Ordinal))
            {
                return false;
            }

            Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];

            HMACSHA256.HashData(_key, token[..payloadLength], expected);

            if (!CryptographicOperations.FixedTimeEquals(token[payloadLength..], expected))
            {
                return false;
            }

            payload = token[..payloadLength].ToArray();

            return true;
        }
    }
}
