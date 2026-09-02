using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{

    /// <summary>
    /// Invoked once per entry for which the <c>list_directory</c> walk performs a full
    /// <see cref="WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck"/> component walk. Each of
    /// those is several filesystem syscalls per path component, and the continuation replays the walk
    /// from the top of the tree on every page, so this is the only observable measure of how much of
    /// the replay is being paid for again. Instance-scoped so a test never observes another session's
    /// walk. Never set outside tests.
    /// </summary>
    internal Action<string>? ListDirectoryEntryValidationObserverForTests { get; set; }

    // The production default (4,096) headroom OutOfScopeDescentTracker reserves for the fingerprint
    // and the page's eventual lastPath -- see the type's own remarks. A non-nullable settable with the
    // production value already assigned, not a nullable override requiring a null-coalesce at every
    // read site: tests lower it to force TryRecord's budget refusal deterministically, then restore it
    // in a finally block. Instance-scoped, matching every other test seam on this class -- a fresh
    // ArcanumInternalToolServer per test session means no cross-test leakage even without that finally.
    internal int ReservedOverheadBytesForTests { get; set; } = 4_096;

    private async Task<McpToolsCallResultWire> ExecuteReadFileChunkAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        McpToolsCallResultWire? gate = TryRequireWorkspaceRoot();

        if (gate is not null)
        {
            return gate;
        }

        ReadFileChunkParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ReadFileChunkParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "read_file_chunk argument deserialization failed.");

            return ToolError("Invalid arguments for read_file_chunk.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.RelativePath))
        {
            return ToolError("read_file_chunk requires 'relativePath', 'startLine', and 'endLine'.");
        }

        if (args.StartLine < 1 || args.EndLine < args.StartLine)
        {
            return ToolError(
                $"read_file_chunk requires 1 <= startLine <= endLine; got startLine={args.StartLine}, endLine={args.EndLine}.");
        }

        if (args.StartLine > McpSecurityLimits.ReadFileChunkMaxStartLine)
        {
            return ToolError(
                $"read_file_chunk: startLine exceeds the maximum ({McpSecurityLimits.ReadFileChunkMaxStartLine}).");
        }

        int requestedLines = args.EndLine - args.StartLine + 1;

        if (requestedLines > McpSecurityLimits.ReadFileChunkMaxLinesPerRequest)
        {
            return ToolError(
                $"read_file_chunk: requested range ({requestedLines} lines) exceeds the maximum ({McpSecurityLimits.ReadFileChunkMaxLinesPerRequest} lines per request).");
        }

        if (!TryResolveSandboxedPath(args.RelativePath, out string? absolutePath, out McpToolsCallResultWire? resolveErr))
        {
            return resolveErr!;
        }

        if (!TryRevalidateBeforeIo(absolutePath, out McpToolsCallResultWire? revalidateErr))
        {
            return revalidateErr!;
        }

        int maxReadBytes = checked((int)
            ArcanumSettingClamps.MaxFileReadSizeBytes(
                _maxFileReadSizeBytes));

        (string? slice, McpToolsCallResultWire? readError) =
            await TryReadLineRangeThroughValidatedHandleAsync(
                    absolutePath,
                    args.StartLine,
                    args.EndLine,
                    maxReadBytes,
                    cancellationToken)
                .ConfigureAwait(false);

        if (slice is null)
        {
            return PrefixToolError("read_file_chunk", readError!);
        }

        return CapToolTextResult(slice, "read_file_chunk");
    }

    private static readonly UTF8Encoding StrictUtf8NoBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Streams <paramref name="absolutePath"/> through the same validated handle
    /// <see cref="SandboxedFileIo.TryOpenForRead"/> opens for every other read path, stopping once
    /// <paramref name="endLine"/>'s content has been captured (or end of file, whichever comes first)
    /// instead of decoding the file's full length before slicing. <paramref name="maxRangeBytes"/>
    /// budgets what is actually read rather than the file's total size, so a narrow range near the top
    /// of a file larger than the cap still succeeds; a distant <paramref name="startLine"/> still costs
    /// reading everything before it, because a plain-text file carries no line index.
    /// </summary>
    private async Task<(string? Content, McpToolsCallResultWire? Error)> TryReadLineRangeThroughValidatedHandleAsync(
        string absolutePath,
        int startLine,
        int endLine,
        int maxRangeBytes,
        CancellationToken cancellationToken)
    {

        if (!SandboxedFileIo.TryOpenForRead(_workspaceRoot!, absolutePath, out FileStream? stream, out McpToolsCallResultWire? openError))
        {
            return (null, openError);
        }

        using (stream)
        {

            char[] charBuffer = ArrayPool<char>.Shared.Rent(8192);

            try
            {

                // Strips a leading UTF-8 BOM the same way the whole-file path does
                // (SecureFileReader.DecodeUtf8), so a file saved with one numbers its lines identically
                // through both read paths. detectEncodingFromByteOrderMarks is left off below because
                // it would also honor a UTF-16/UTF-32 BOM, which the whole-file path never did.
                byte[] bomProbe = new byte[3];

                int bomRead = await stream.ReadAsync(bomProbe, cancellationToken).ConfigureAwait(false);

                if (bomRead != 3 || bomProbe[0] != 0xEF || bomProbe[1] != 0xBB || bomProbe[2] != 0xBF)
                {
                    stream.Seek(-bomRead, SeekOrigin.Current);
                }

                using StreamReader reader = new(
                    stream,
                    StrictUtf8NoBom,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 8192,
                    leaveOpen: true);

                StringBuilder content = new();

                long rangeBytes = 0;

                int lineNumber = 1;

                bool pendingCr = false;

                // Only advances lineNumber/pendingCr to decide when enough of the file has been read
                // for SliceLineRange to compute the answer; SliceLineRange itself still owns every
                // terminator decision (CRLF, lone CR, LF) once the accumulated text reaches it below.
                bool ScanChunkPastEndLine(int count)
                {

                    for (int i = 0; i < count; i++)
                    {

                        char c = charBuffer[i];

                        if (pendingCr)
                        {

                            pendingCr = false;

                            if (c == '\n')
                            {
                                continue;
                            }

                        }

                        if (c == '\r')
                        {

                            pendingCr = true;

                            lineNumber++;

                        }
                        else if (c == '\n')
                        {

                            lineNumber++;

                        }
                        else
                        {

                            continue;

                        }

                        if (lineNumber > endLine)
                        {
                            return true;
                        }

                    }

                    return false;

                }

                while (true)
                {

                    int read;

                    try
                    {

                        read = await reader.ReadAsync(charBuffer, cancellationToken).ConfigureAwait(false);

                    }
                    catch (DecoderFallbackException)
                    {

                        return (null, ToolError("The file is not valid UTF-8 text."));

                    }

                    if (read == 0)
                    {
                        break;
                    }

                    content.Append(charBuffer, 0, read);

                    rangeBytes += Encoding.UTF8.GetByteCount(charBuffer, 0, read);

                    if (rangeBytes > maxRangeBytes)
                    {
                        return (null, ToolError("The file exceeds the maximum read size limit."));
                    }

                    if (ScanChunkPastEndLine(read))
                    {
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                }

                return (
                    SliceLineRange(content.ToString(), startLine, endLine, cancellationToken),
                    null);

            }
            catch (Exception ex)
                when (ex is IOException or UnauthorizedAccessException)
            {

                return (null, ToolError("An I/O error occurred. See server logs."));

            }
            finally
            {

                ArrayPool<char>.Shared.Return(charBuffer);

            }

        }

    }

    /// <summary>
    /// Slices the 1-based inclusive line range out of <paramref name="content"/> by index so the text
    /// this tool returns is a literal substring of the file's decoded bytes. <c>StringReader.ReadLine</c>
    /// consumes <c>"\r\n"</c>, <c>"\n"</c> and a lone <c>"\r"</c> and hands back none of them, so
    /// rejoining with <c>"\n"</c> rewrote every CRLF (and classic-Mac) file and made the chunk
    /// impossible to use as <c>replace_text_block</c>'s verbatim <c>exactSearchText</c>, whose schema
    /// promises the block is matched "including whitespace and newlines". The terminator model matches
    /// <c>WorkspaceTextFile.ParseLines</c>, which is what already makes <c>apply_patch</c> line-ending
    /// faithful. The final selected line's terminator is excluded, so a pure-LF file's chunk is
    /// byte-for-byte what the previous read-and-join produced.
    /// </summary>
    private static string SliceLineRange(
        string content,
        int startLine,
        int endLine,
        CancellationToken cancellationToken)
    {
        int lineNumber = 1;

        int lineStart = 0;

        int sliceStart = -1;

        int sliceEnd = -1;

        int index = 0;

        while (index < content.Length)
        {
            if ((index & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            char character = content[index];

            if (character is not ('\r' or '\n'))
            {
                index++;

                continue;
            }

            if (lineNumber == startLine)
            {
                sliceStart = lineStart;
            }

            if (lineNumber >= startLine)
            {
                sliceEnd = index;
            }

            if (lineNumber == endLine)
            {
                return sliceStart < 0 ? string.Empty : content[sliceStart..sliceEnd];
            }

            index += character == '\r'
                && index + 1 < content.Length
                && content[index + 1] == '\n'
                    ? 2
                    : 1;

            lineNumber++;

            lineStart = index;
        }

        // A trailing segment with no terminator is the file's last line.
        if (lineStart < content.Length && lineNumber >= startLine)
        {
            if (lineNumber == startLine)
            {
                sliceStart = lineStart;
            }

            sliceEnd = content.Length;
        }

        return sliceStart < 0 || sliceEnd < sliceStart ? string.Empty : content[sliceStart..sliceEnd];
    }

    private async Task<McpToolsCallResultWire> ExecuteReplaceTextBlockAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        McpToolsCallResultWire? gate = TryRequireWorkspaceRoot();

        if (gate is not null)
        {
            return gate;
        }

        gate = TryRequirePersistedToolInvocation();

        if (gate is not null)
        {
            return gate;
        }

        ReplaceTextBlockParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ReplaceTextBlockParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "replace_text_block argument deserialization failed.");

            return ToolError("Invalid arguments for replace_text_block.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.RelativePath))
        {
            return ToolError("replace_text_block requires 'relativePath', 'exactSearchText', and 'replacementText'.");
        }

        if (args.ExactSearchText.Length == 0)
        {
            return ToolError("replace_text_block: 'exactSearchText' must be non-empty.");
        }

        if (!TryResolveSandboxedPath(args.RelativePath, out string? absolutePath, out McpToolsCallResultWire? resolveErr))
        {
            return resolveErr!;
        }

        if (!TryRevalidateBeforeIo(absolutePath, out McpToolsCallResultWire? revalidateErr))
        {
            return revalidateErr!;
        }

        ResourceLimits resourceLimits = await ResolveResourceLimitsAsync(cancellationToken).ConfigureAwait(false);

        int maxReadBytes = checked((int)
            ArcanumSettingClamps.MaxFileReadSizeBytes(
                _maxFileReadSizeBytes));

        (string? content, McpToolsCallResultWire? readError) = await SandboxedFileIo.TryReadAllTextAsync(
            _workspaceRoot!,
            absolutePath,
            maxReadBytes,
            cancellationToken).ConfigureAwait(false);

        if (content is null)
        {

            return PrefixToolError("replace_text_block", readError!);

        }

        if (!content.Contains(args.ExactSearchText, StringComparison.Ordinal))
        {
            return ToolError(
                $"Exact search text not found in '{absolutePath}'. Re-read the file with read_file_chunk and use a verbatim block (including whitespace and newlines) before retrying.");
        }

        int occurrences = CountOccurrences(content, args.ExactSearchText);

        string updated = content.Replace(args.ExactSearchText, args.ReplacementText, StringComparison.Ordinal);

        McpToolsCallResultWire? writeLimitError = TryRejectIfWriteExceedsLimit(
            updated,
            resourceLimits.MaxFileWriteMb,
            "replace_text_block");

        if (writeLimitError is not null)
        {
            return writeLimitError;
        }

        (bool writeSuccess, McpToolsCallResultWire? writeError) = await SandboxedFileIo
            .TryWriteAllTextAtomicallyAsync(_workspaceRoot!, absolutePath, updated, cancellationToken)
            .ConfigureAwait(false);

        if (!writeSuccess)
        {

            return PrefixToolError("replace_text_block", writeError!);

        }

        string text = occurrences == 1
            ? $"Replaced 1 occurrence in '{absolutePath}'."
            : $"Replaced {occurrences} occurrences in '{absolutePath}'.";

        return new McpToolsCallResultWire
        {
            Content =
            [
                new McpToolContentTextWire { Text = text },
            ],
            IsError = false,
        };
    }

    private async Task<McpToolsCallResultWire> ExecuteWriteFileAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        McpToolsCallResultWire? gate = TryRequireWorkspaceRoot();

        if (gate is not null)
        {
            return gate;
        }

        gate = TryRequirePersistedToolInvocation();

        if (gate is not null)
        {
            return gate;
        }

        WriteFileParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.WriteFileParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "write_file argument deserialization failed.");

            return ToolError("Invalid arguments for write_file.");
        }

        // Content is checked explicitly: WriteFileParams is positional, so a missing (or explicitly
        // null) 'content' binds to the parameter default without raising JsonException, and the first
        // thing the write-limit guard does is take its UTF-8 byte count.
        if (args is null || string.IsNullOrWhiteSpace(args.RelativePath) || args.Content is null)
        {
            return ToolError("write_file requires 'relativePath' and 'content'.");
        }

        if (!TryResolveSandboxedPath(args.RelativePath, out string? absolutePath, out McpToolsCallResultWire? resolveErr))
        {
            return resolveErr!;
        }

        if (!TryRevalidateBeforeIo(absolutePath, out McpToolsCallResultWire? revalidateErr))
        {
            return revalidateErr!;
        }

        ResourceLimits resourceLimits = await ResolveResourceLimitsAsync(cancellationToken).ConfigureAwait(false);

        McpToolsCallResultWire? writeLimitError = TryRejectIfWriteExceedsLimit(
            args.Content,
            resourceLimits.MaxFileWriteMb,
            "write_file");

        if (writeLimitError is not null)
        {
            return writeLimitError;
        }

        (bool writeSuccess, McpToolsCallResultWire? writeError) = await SandboxedFileIo
            .TryWriteAllTextAtomicallyAsync(_workspaceRoot!, absolutePath, args.Content, cancellationToken)
            .ConfigureAwait(false);

        if (!writeSuccess)
        {

            return PrefixToolError("write_file", writeError!);

        }

        return new McpToolsCallResultWire
        {
            Content =
            [
                new McpToolContentTextWire { Text = $"Wrote file '{absolutePath}'." },
            ],
            IsError = false,
        };
    }

    private async Task<McpToolsCallResultWire> ExecuteListDirectoryAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        McpToolsCallResultWire? gate = TryRequireWorkspaceRoot();

        if (gate is not null)
        {
            return gate;
        }

        ListDirectoryParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ListDirectoryParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "list_directory argument deserialization failed.");

            return ToolError("Invalid arguments for list_directory.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.RelativePath))
        {
            return ToolError("list_directory requires 'relativePath'.");
        }

        if (!TryResolveSandboxedPath(args.RelativePath, out string? absolutePath, out McpToolsCallResultWire? resolveErr))
        {
            return resolveErr!;
        }

        if (!TryRevalidateBeforeIo(absolutePath, out McpToolsCallResultWire? revalidateErr))
        {
            return revalidateErr!;
        }

        return await Task.Run(
            () => ListDirectoryCore(args, absolutePath, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private McpToolsCallResultWire ListDirectoryCore(
        ListDirectoryParams args,
        string absolutePath,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(absolutePath) && !Directory.Exists(absolutePath))
            {
                return ToolError($"list_directory: path is not a directory: '{absolutePath}'.");
            }

            if (!Directory.Exists(absolutePath))
            {
                return ToolError($"list_directory: directory not found: '{absolutePath}'.");
            }

            if (!TryDecodeListDirectoryContinuation(
                    args,
                    out string? afterPath,
                    out string[] decodedOutOfScopeDescendedRelativePaths,
                    out string? continuationError))
            {

                return ToolError(
                    "list_directory: " + continuationError);

            }

            List<string> lines = new(_listDirectoryPageSize + 1);

            long textBudget = Math.Max(
                1_024,
                Math.Min(
                    ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
                        _settings.ToolOutputCapBytes,
                        _maxJsonRpcLineBytes),
                    _maxJsonRpcLineBytes) / 2);

            long materializedBytes = 0;

            bool hasMore = false;

            string? lastPath = null;

            // Snapshot of the out-of-scope tracker as of the last entry actually kept on this page, not
            // the tracker's live state once the loop below returns. The foreach's own lookahead -- it
            // must call MoveNext() one entry past the page boundary to learn whether hasMore is true --
            // resumes the walk's lazy iterator past that boundary entry's own yield, which runs *that*
            // entry's descend decision (including any OutOfScopeDescentTracker.TryRecord for it) even
            // though none of what that decision produces ever makes it into this page. Encoding the
            // live tracker would carry that premature recording into the continuation token, and the
            // next page's checkpoint-itself seek would then see the boundary entry as already recorded
            // and refuse to redescend into it -- silently dropping content that was never shown. Taking
            // the snapshot right after the last kept entry, before the lookahead runs, is what lets the
            // next page's seek redecide that entry's descent itself, exactly as if it had never been
            // peeked at.
            string[] outOfScopeSnapshot = [];

            // Seeks directly to the checkpoint by re-listing only its ancestor directories (a resumed
            // page never re-walks or re-validates the prefix a prior page already emitted); a checkpoint
            // that no longer exists on disk is reported here, up front, exactly as it was when a full
            // replay discovered the same fact by never matching it.
            if (!TrySeekListDirectoryEntries(
                    absolutePath,
                    args.Recursive,
                    afterPath,
                    decodedOutOfScopeDescendedRelativePaths,
                    cancellationToken,
                    out IEnumerable<string> walk,
                    out OutOfScopeDescentTracker outOfScopeTracker))
            {

                return ToolError(
                    "list_directory: the opaque continuation entry no longer exists in this directory snapshot. No page was checkpointed; restart from the first page to observe the changed workspace safely.");

            }

            foreach (string entry in walk)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string relativePath = Path.GetRelativePath(_workspaceRoot!, entry)
                    .Replace(Path.DirectorySeparatorChar, '/');

                int entryBytes = System.Text.Encoding.UTF8.GetByteCount(relativePath) + 1;

                if (entryBytes > textBudget)
                {
                    return ToolError(
                        $"list_directory: protocol-owned output protection rejected one {entryBytes}-byte path because the safe text page is {textBudget} bytes. No page was checkpointed; list a more specific contained directory.");
                }

                if (lines.Count >= _listDirectoryPageSize
                    || materializedBytes + entryBytes > textBudget)
                {
                    hasMore = true;

                    break;
                }

                lines.Add(relativePath);

                materializedBytes += entryBytes;

                lastPath = relativePath;

                // Taken after every kept entry, not just once at the count cap: the byte-budget break
                // below can end the page before the count cap is reached, so which iteration is "last
                // kept" is not knowable in advance. Cheap to retake each time -- pages are bounded by
                // _listDirectoryPageSize, and the tracker itself is budget-bounded (see TryRecord).
                outOfScopeSnapshot = outOfScopeTracker.ToRelativePaths().ToArray();
            }

            // Live tracker state, not the last-kept-entry snapshot used for the continuation token above:
            // a refusal never persists across pages (nothing about it is encoded), so when this page is
            // the last one (hasMore false) there is no later page left to report it instead -- using the
            // snapshot here would silently drop the report for whichever refusal the pagination lookahead
            // itself triggered, exactly the silent-loss shape this marker exists to prevent. The one cost
            // is a boundary alias refused by the lookahead getting reported on both this page and the
            // next (which re-attempts the same descent fresh) -- an over-report, the safe direction,
            // never a false claim of completeness.
            foreach (string refused in outOfScopeTracker.RefusedEntryRelativePaths)
            {

                lines.Add(
                    $"... [TRUNCATED: {refused} was not listed because its contents did not fit the continuation token's byte budget.]");

            }

            if (hasMore)
            {

                string continuation = EncodeListDirectoryContinuation(
                    args,
                    lastPath!,
                    outOfScopeSnapshot);

                lines.Add(
                    $"... [MORE: continuation={continuation}; call list_directory again with the same relativePath/recursive arguments and this opaque continuation token.]" );
            }

            string joined = string.Join("\n", lines);

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = joined },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ToolError("list_directory: operation was canceled.");
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "list_directory: I/O error.");

            return ToolError("list_directory: an I/O error occurred. See server logs.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "list_directory: access denied.");

            return ToolError("list_directory: access denied.");
        }
    }

    /// <summary>
    /// Decodes <paramref name="args"/>'s continuation, if any, into the checkpoint path to resume from
    /// and the set of out-of-scope directory symlink targets (workspace-relative, forward-slashed)
    /// already descended into by an earlier page of this same listing -- the cross-page memory an
    /// out-of-scope alias needs, since <see cref="WalkListDirectoryEntries"/>'s per-page
    /// <c>visitedCanonicalDirectories</c> alone is reseeded fresh on every resumed page (see W6-4 round
    /// 2). An in-scope alias needs no such memory: its target's own real name owns its content, so
    /// whether to descend it is a pure function of the canonical path alone, not of what an earlier page
    /// already saw.
    /// </summary>
    private bool TryDecodeListDirectoryContinuation(
        ListDirectoryParams args,
        out string? afterPath,
        out string[] outOfScopeDescendedRelativePaths,
        out string? error)
    {

        afterPath = null;

        outOfScopeDescendedRelativePaths = [];

        error = null;

        if (string.IsNullOrEmpty(args.Continuation))
        {

            return true;

        }

        if (Encoding.UTF8.GetByteCount(args.Continuation) > _maxJsonRpcLineBytes)
        {

            error = "protocol-owned continuation protection rejected a token larger than the JSON-RPC frame budget. No work was performed; restart from the first page.";

            return false;

        }

        try
        {

            byte[] bytes = Convert.FromBase64String(args.Continuation);

            string payload = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);

            int separator = payload.IndexOf('\n');

            if (separator <= 0
                || !string.Equals(
                    payload[..separator],
                    ComputeListDirectoryScopeFingerprint(args),
                    StringComparison.Ordinal))
            {

                error = "the opaque continuation token does not belong to the same relativePath and recursive arguments. No work was performed; reuse the original arguments or restart from the first page.";

                return false;

            }

            string remainder = payload[(separator + 1)..];

            // A third, optional segment -- the out-of-scope-descent set -- follows the checkpoint path
            // after a second '\n'; entries within it are length-prefixed ("{charCount}:{entry}", back
            // to back with no separator at all -- see ParseLengthPrefixedEntries), not joined by any
            // separator character. A workspace-relative path can legally contain any byte a filesystem
            // allows, including one that used to collide with the single-character join separator this
            // replaced: a target reached only through an alias whose own relative path happened to
            // contain that separator byte would decode back into fragments, silently losing that
            // target's identity (it could then be redescended on a later page, since the fragments never
            // matched anything TrySeekListDirectoryEntries actually looks up) -- length-prefixing has no
            // separator byte for any content to collide with, so this is unconditionally unambiguous.
            int entriesSeparator = remainder.IndexOf('\n');

            afterPath = entriesSeparator < 0
                ? remainder
                : remainder[..entriesSeparator];

            if (afterPath.Length == 0)
            {

                error = "the opaque continuation token contains no checkpoint path. No work was performed; restart from the first page.";

                afterPath = null;

                return false;

            }

            if (entriesSeparator >= 0)
            {

                string entriesPortion = remainder[(entriesSeparator + 1)..];

                outOfScopeDescendedRelativePaths = entriesPortion.Length == 0
                    ? []
                    : ParseLengthPrefixedEntries(entriesPortion);

            }

            return true;

        }
        catch (Exception exception)
            when (exception is FormatException
                  or System.Text.DecoderFallbackException)
        {

            error = "the opaque continuation token is malformed. No work was performed; use a token returned by list_directory or restart from the first page.";

            return false;

        }

    }

    private static string EncodeListDirectoryContinuation(
        ListDirectoryParams args,
        string lastPath,
        IReadOnlyCollection<string> outOfScopeDescendedRelativePaths)
    {

        string payload = ComputeListDirectoryScopeFingerprint(args)
            + "\n"
            + lastPath;

        if (outOfScopeDescendedRelativePaths.Count > 0)
        {

            payload += "\n" + string.Concat(
                outOfScopeDescendedRelativePaths.Select(EncodeLengthPrefixedEntry));

        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));

    }

    // "{charCount}:{entry}", back to back with no join separator -- see the decode-side comment in
    // TryDecodeListDirectoryContinuation for why a single separator character cannot safely stand in
    // for this. entry.Length (UTF-16 code units) is what the decoder's Substring call will consume, not
    // entry's UTF-8 byte count, which can differ for any non-ASCII content -- using anything else here
    // would desynchronize ParseLengthPrefixedEntries starting from the first non-ASCII entry.
    private static string EncodeLengthPrefixedEntry(string entry) =>
        entry.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + entry;

    /// <summary>
    /// Inverse of <see cref="EncodeLengthPrefixedEntry"/>. Throws <see cref="FormatException"/> on any
    /// malformed input (missing digits, missing colon, a length that overruns what remains of
    /// <paramref name="entriesPortion"/>) -- the caller's existing catch clause already treats that
    /// exception as "the opaque continuation token is malformed", the same outcome a corrupted Base64
    /// payload or an invalid UTF-8 byte sequence earlier in the same decode already produces, so this
    /// reuses that path rather than introducing a second, parallel way to report the same thing.
    /// </summary>
    private static string[] ParseLengthPrefixedEntries(string entriesPortion)
    {

        List<string> entries = [];

        int i = 0;

        while (i < entriesPortion.Length)
        {

            int digitsStart = i;

            while (i < entriesPortion.Length
                   && entriesPortion[i] is >= '0' and <= '9')
            {

                i++;

            }

            if (i == digitsStart
                || i >= entriesPortion.Length
                || entriesPortion[i] != ':')
            {

                throw new FormatException(
                    "list_directory continuation: malformed length-prefixed out-of-scope entry.");

            }

            // long, not int, as defense in depth only -- not because int overflows here today. The
            // check inside this loop runs after *every* digit, not once at the end, so length can never
            // exceed entriesPortion.Length entering any single multiply-add: a value already bounded by
            // entriesPortion.Length, itself bounded by TryDecodeListDirectoryContinuation's own
            // Encoding.UTF8.GetByteCount(args.Continuation) > _maxJsonRpcLineBytes check up front, comes
            // nowhere near int.MaxValue no matter how long a forged digit run is. long removes any
            // dependency on that outer bound if this parse is ever reused somewhere it does not apply.
            long length = 0;

            for (int digit = digitsStart; digit < i; digit++)
            {

                length = (length * 10) + (entriesPortion[digit] - '0');

                if (length > entriesPortion.Length)
                {

                    throw new FormatException(
                        "list_directory continuation: out-of-scope entry length prefix out of range.");

                }

            }

            int contentLength = (int)length;

            int contentStart = i + 1;

            if (contentStart + contentLength > entriesPortion.Length)
            {

                throw new FormatException(
                    "list_directory continuation: out-of-scope entry length prefix overruns the token.");

            }

            entries.Add(
                entriesPortion.Substring(contentStart, contentLength));

            i = contentStart + contentLength;

        }

        return [.. entries];

    }

    private static string ComputeListDirectoryScopeFingerprint(
        ListDirectoryParams args)
    {

        string scope = args.RelativePath.Trim()
            .Replace('\\', '/')
            + "\n"
            + args.Recursive;

        byte[] digest = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(scope));

        return Convert.ToHexString(digest.AsSpan(0, 16));

    }

    /// <summary>
    /// Builds the depth-first walk for one page: a fresh <paramref name="afterPath"/>-less call starts
    /// at <paramref name="absolutePath"/>, and a continuation seeks straight to the checkpoint by
    /// re-listing only its ancestor directories — never the prefix an earlier page already emitted —
    /// before <see cref="WalkListDirectoryEntries"/> resumes forward from there. Returns <see
    /// langword="false"/> only when the checkpoint itself could not be found (vanished, or a malformed
    /// token); the caller reports that as the same restart-required error either way.
    /// </summary>
    /// <summary>
    /// Tracks which out-of-scope directory symlink targets one <c>list_directory</c> call has already
    /// descended into, across every continuation of that same call -- the cross-page memory an
    /// out-of-scope alias needs (W6-4 round 2). An in-scope alias needs none: its target's own real
    /// name owns its content, so whether to descend it is a pure function of the canonical path alone
    /// (see <see cref="WalkListDirectoryEntries"/>), never of what an earlier page already saw. An
    /// out-of-scope target has no real name inside the listed scope for the walk to ever reach on its
    /// own, so its only defense against being re-descended on a later page is remembering it here,
    /// carried in the continuation token between pages.
    /// </summary>
    /// <remarks>
    /// Bounded by the same budget the continuation token itself is: an identity that cannot be safely
    /// recorded must not be descended, so a future page is never left needing to remember something
    /// this page silently dropped.
    /// </remarks>
    private sealed class OutOfScopeDescentTracker
    {

        private readonly string _workspaceRoot;

        private readonly int _maxTokenBytes;

        // Headroom for the fingerprint (32 hex characters) and the page's eventual lastPath -- neither
        // known precisely at the point an in-progress page decides whether to descend into one more
        // out-of-scope alias, but each individually small relative to a realistic _maxJsonRpcLineBytes.
        // TryDecodeListDirectoryContinuation's own byte-budget check is the exact, final backstop if
        // this estimate is ever optimistic. Constructor-supplied rather than a fixed const so tests can
        // force TryRecord's refusal deterministically (ArcanumInternalToolServer.
        // ReservedOverheadBytesForTests) without needing a fixture large enough to hit the real,
        // production-sized bound.
        private readonly int _reservedOverheadBytes;

        private long _reservedEntryBytes;

        public OutOfScopeDescentTracker(
            IReadOnlyList<string> decodedRelativePaths,
            string workspaceRoot,
            StringComparer comparer,
            int maxTokenBytes,
            int reservedOverheadBytes)
        {

            _workspaceRoot = workspaceRoot;

            _maxTokenBytes = maxTokenBytes;

            _reservedOverheadBytes = reservedOverheadBytes;

            CanonicalDirectories = new HashSet<string>(comparer);

            foreach (string relative in decodedRelativePaths)
            {

                string canonical = Path.GetFullPath(
                    Path.Combine(workspaceRoot, relative));

                if (CanonicalDirectories.Add(canonical))
                {

                    _reservedEntryBytes += Encoding.UTF8.GetByteCount(relative)
                        + LengthPrefixOverheadBytes(relative);

                }

            }

        }

        public HashSet<string> CanonicalDirectories { get; }

        // The alias path (as shown in the listing, e.g. "scope/scopelink0") for every out-of-scope
        // descent TryRecord refused, one entry per refusal -- including more than one entry for the
        // same underlying target when two different aliases to it were each refused in turn, since each
        // is a separate line the reader sees and each individually needs its own "not listed" marker.
        // Not deduplicated and not budget-checked itself (see ListDirectoryCore, which emits these): a
        // refusal is already the failure case, not something more budget accounting should gate further.
        public List<string> RefusedEntryRelativePaths { get; } = [];

        public IEnumerable<string> ToRelativePaths() =>
            CanonicalDirectories.Select(canonical =>
                Path.GetRelativePath(_workspaceRoot, canonical)
                    .Replace(Path.DirectorySeparatorChar, '/'));

        // Matches EncodeLengthPrefixedEntry's own "{charCount}:" prefix byte-for-byte -- both count in
        // UTF-16 code units (relative.Length), the unit the eventual encode/decode round trip actually
        // uses, and both charge one ASCII byte per digit plus one for the colon, with no separate
        // component-length tracked for the digits themselves versus the colon.
        private static int LengthPrefixOverheadBytes(string relative) =>
            relative.Length.ToString(System.Globalization.CultureInfo.InvariantCulture).Length + 1;

        /// <summary>
        /// Records <paramref name="canonicalTarget"/> as descended, or reports that doing so safely is
        /// no longer possible within this token's byte budget -- in which case <paramref
        /// name="aliasAbsolutePath"/> (the alias entry the walk was about to descend through, not the
        /// target itself, which never appears in the listing under its own name when out of scope) is
        /// recorded into <see cref="RefusedEntryRelativePaths"/> for the caller to report. The caller
        /// must not descend when this returns <see langword="false"/> -- see the type-level remarks.
        /// </summary>
        public bool TryRecord(string canonicalTarget, string aliasAbsolutePath)
        {

            if (CanonicalDirectories.Contains(canonicalTarget))
            {

                return true;

            }

            string relative = Path.GetRelativePath(_workspaceRoot, canonicalTarget)
                .Replace(Path.DirectorySeparatorChar, '/');

            long candidateBytes = Encoding.UTF8.GetByteCount(relative)
                + LengthPrefixOverheadBytes(relative);

            long projectedTokenBytes = _reservedOverheadBytes
                + (((_reservedEntryBytes + candidateBytes) * 4) / 3);

            if (projectedTokenBytes > _maxTokenBytes)
            {

                string aliasRelative = Path.GetRelativePath(_workspaceRoot, aliasAbsolutePath)
                    .Replace(Path.DirectorySeparatorChar, '/');

                RefusedEntryRelativePaths.Add(aliasRelative);

                return false;

            }

            _reservedEntryBytes += candidateBytes;

            _ = CanonicalDirectories.Add(canonicalTarget);

            return true;

        }

    }

    private bool TrySeekListDirectoryEntries(
        string absolutePath,
        bool recursive,
        string? afterPath,
        IReadOnlyList<string> decodedOutOfScopeDescendedRelativePaths,
        CancellationToken cancellationToken,
        out IEnumerable<string> entries,
        out OutOfScopeDescentTracker outOfScopeTracker)
    {

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        outOfScopeTracker = new OutOfScopeDescentTracker(
            decodedOutOfScopeDescendedRelativePaths,
            _workspaceRoot!,
            pathComparer,
            _maxJsonRpcLineBytes,
            ReservedOverheadBytesForTests);

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                _workspaceRoot!,
                absolutePath,
                out string? resolvedRoot))
        {

            entries = [];

            return true;

        }

        // The same resolution the containment check above just used for absolutePath itself, so a
        // directory's canonical identity and this root are never compared across a raw-vs-resolved
        // mismatch (e.g. a workspace root reached through its own ancestor symlink).
        string canonicalScopeRoot = Path.GetFullPath(
            resolvedRoot ?? absolutePath);

        HashSet<string> visitedCanonicalDirectories = new(
            pathComparer);

        _ = visitedCanonicalDirectories.Add(
            canonicalScopeRoot);

        // Every out-of-scope target an earlier page already recorded is, from this page's own
        // perspective, exactly as "already visited" as the scope root or an ancestor -- merging it in
        // here means WalkListDirectoryEntries's existing Add-gated push decision (unchanged from round
        // 1) already refuses to re-descend into it, with no separate comparison needed at that site.
        foreach (string canonical in outOfScopeTracker.CanonicalDirectories)
        {

            _ = visitedCanonicalDirectories.Add(canonical);

        }

        Stack<(string Directory, string[] Entries, int Index)> stack = new();

        if (afterPath is null)
        {

            stack.Push(
                (absolutePath, SortedListDirectoryEntries(absolutePath), 0));

        }
        else if (!TrySeekToListDirectoryCheckpoint(
                     absolutePath,
                     canonicalScopeRoot,
                     afterPath,
                     recursive,
                     visitedCanonicalDirectories,
                     outOfScopeTracker,
                     stack,
                     cancellationToken))
        {

            entries = [];

            return false;

        }

        entries = WalkListDirectoryEntries(
            stack,
            canonicalScopeRoot,
            visitedCanonicalDirectories,
            outOfScopeTracker,
            recursive,
            cancellationToken);

        return true;

    }

    private static string[] SortedListDirectoryEntries(string directory) =>
        Directory.EnumerateFileSystemEntries(
                directory,
                "*",
                SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Walks down <paramref name="afterPath"/>'s ancestor chain from <paramref name="scopeAbsolutePath"/>,
    /// re-listing exactly one directory per level to find where that level's segment sits among its
    /// (sorted, unfiltered) siblings, and pushes a resume frame at each level for the sibling that
    /// follows it. Touches nothing outside that chain: an earlier, already-emitted sibling's subtree is
    /// never re-listed, matching <paramref name="stack"/>'s "smallest resume token, no replay" contract.
    /// </summary>
    private bool TrySeekToListDirectoryCheckpoint(
        string scopeAbsolutePath,
        string canonicalScopeRoot,
        string afterPath,
        bool recursive,
        HashSet<string> visitedCanonicalDirectories,
        OutOfScopeDescentTracker outOfScopeTracker,
        Stack<(string Directory, string[] Entries, int Index)> stack,
        CancellationToken cancellationToken)
    {

        string absoluteCheckpoint = Path.GetFullPath(
            Path.Combine(_workspaceRoot!, afterPath));

        string relativeFromScope = Path.GetRelativePath(
            scopeAbsolutePath,
            absoluteCheckpoint);

        if (relativeFromScope.Length == 0
            || relativeFromScope == "."
            || relativeFromScope.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relativeFromScope))
        {

            return false;

        }

        string[] segments = relativeFromScope.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {

            return false;

        }

        string currentDirectory = scopeAbsolutePath;

        for (int level = 0; level < segments.Length; level++)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string[] siblingEntries = SortedListDirectoryEntries(currentDirectory);

            int foundIndex = -1;

            for (int i = 0; i < siblingEntries.Length; i++)
            {

                if (string.Equals(
                        Path.GetFileName(siblingEntries[i]),
                        segments[level],
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {

                    foundIndex = i;

                    break;

                }

            }

            if (foundIndex < 0)
            {

                return false;

            }

            string matchedEntry = siblingEntries[foundIndex];

            bool matchedIsDirectory = Directory.Exists(matchedEntry);

            // A forged token cannot be trusted to have named a real ancestor: an honest checkpoint's
            // ancestor was descended into on the page that emitted it, which by construction never
            // enqueues a pruned folder, so this can only match here if the token was hand-crafted.
            // Treat it exactly like any other checkpoint that does not resolve to a real position.
            if (matchedIsDirectory
                && IsListDirectorySkipFolder(Path.GetFileName(matchedEntry)))
            {

                return false;

            }

            bool isCheckpointItself = level == segments.Length - 1;

            // The next sibling at this level is where the walk resumes once whatever gets pushed below
            // for this level (the checkpoint's own children, at the last level) is exhausted and popped
            // back to here — same pre-order shape the forward walk already produces on its own.
            stack.Push(
                (currentDirectory, siblingEntries, foundIndex + 1));

            if (!isCheckpointItself)
            {

                if (!matchedIsDirectory)
                {

                    return false;

                }

                ListDirectoryEntryValidationObserverForTests?.Invoke(matchedEntry);

                if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                        _workspaceRoot!,
                        matchedEntry,
                        out string? resolvedAncestor))
                {

                    return false;

                }

                string canonicalAncestor = Path.GetFullPath(
                    resolvedAncestor ?? matchedEntry);

                bool ancestorIsAlias = !visitedCanonicalDirectories.Comparer.Equals(
                    canonicalAncestor,
                    Path.GetFullPath(matchedEntry));

                bool ancestorIsInScope = WorkspacePathPolicy.IsPathUnderWorkspace(
                    canonicalScopeRoot,
                    canonicalAncestor);

                // An honest checkpoint's ancestor was descended into on the page that emitted it, and the
                // forward walk (WalkListDirectoryEntries below) never descends into a directory symlink
                // whose target sits inside the listing scope -- the target's own real name owns that
                // content instead. An ancestor segment that resolves to an in-scope alias here cannot be
                // a position the walk ever produced; treat it exactly like any other checkpoint that does
                // not resolve to a real position.
                if (ancestorIsAlias && ancestorIsInScope)
                {

                    return false;

                }

                // An out-of-scope alias ancestor needs the same cross-page memory as any other
                // out-of-scope descent (W6-4 round 2): this checkpoint's own existence proves it was
                // already descended into on an earlier page, and once a later page's checkpoint moves
                // past it, this seek is the only place that re-derives the fact -- the per-page
                // visitedCanonicalDirectories set below is reseeded fresh every page and does not carry
                // it forward on its own. Recording it here is redundant (a no-op) whenever it already
                // came from the decoded token, and only load-bearing the one time this exact ancestor is
                // encountered for the first time from a seek rather than a forward push.
                if (ancestorIsAlias
                    && !ancestorIsInScope
                    && !outOfScopeTracker.TryRecord(canonicalAncestor, matchedEntry))
                {

                    return false;

                }

                _ = visitedCanonicalDirectories.Add(
                    canonicalAncestor);

                currentDirectory = matchedEntry;

            }
            else if (recursive && matchedIsDirectory)
            {

                ListDirectoryEntryValidationObserverForTests?.Invoke(matchedEntry);

                if (WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                        _workspaceRoot!,
                        matchedEntry,
                        out string? resolvedCheckpoint))
                {

                    string canonicalCheckpoint = Path.GetFullPath(
                        resolvedCheckpoint ?? matchedEntry);

                    bool checkpointIsAlias = !visitedCanonicalDirectories.Comparer.Equals(
                        canonicalCheckpoint,
                        Path.GetFullPath(matchedEntry));

                    bool checkpointIsInScope = WorkspacePathPolicy.IsPathUnderWorkspace(
                        canonicalScopeRoot,
                        canonicalCheckpoint);

                    // Descend only via the canonical path: an in-scope alias is emitted (it already was,
                    // as the checkpoint itself, on the page before this one) but never recursed into --
                    // the target's own real name owns its content, exactly as WalkListDirectoryEntries
                    // decides below for the same shape encountered on a forward (non-resumed) walk.
                    bool refuse = checkpointIsAlias && checkpointIsInScope;

                    // The forward walk that emitted this checkpoint as an entry suspends its iterator
                    // right after yielding it, before ever reaching this same recursion decision itself
                    // (a lazy `yield return`'s continuation only runs on the next MoveNext, and the page
                    // that emitted this checkpoint stopped pulling right there). So an out-of-scope alias
                    // checkpoint being recursed into here, from a seek, is genuinely the first time this
                    // exact descent is decided -- record it, gated by the same budget every other
                    // out-of-scope descent is (W6-4 round 2).
                    bool canRecord = !checkpointIsAlias
                        || checkpointIsInScope
                        || outOfScopeTracker.TryRecord(canonicalCheckpoint, matchedEntry);

                    if (!refuse
                        && canRecord
                        && visitedCanonicalDirectories.Add(canonicalCheckpoint))
                    {

                        stack.Push(
                            (matchedEntry, SortedListDirectoryEntries(matchedEntry), 0));

                    }

                }

            }

        }

        return true;

    }

    /// <summary>
    /// Forward-only pre-order walk over an explicit stack, starting from whatever
    /// <see cref="TrySeekListDirectoryEntries"/> already positioned it at. Every entry this touches is
    /// at or after the checkpoint, so — unlike the former replay-based walk — every entry here gets full
    /// validation; there is no earlier "replaying past the prefix" phase left to defer it for.
    /// </summary>
    private IEnumerable<string> WalkListDirectoryEntries(
        Stack<(string Directory, string[] Entries, int Index)> stack,
        string canonicalScopeRoot,
        HashSet<string> visitedCanonicalDirectories,
        OutOfScopeDescentTracker outOfScopeTracker,
        bool recursive,
        CancellationToken cancellationToken)
    {

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (string directory, string[] siblingEntries, int index) = stack.Pop();

            if (index >= siblingEntries.Length)
            {
                continue;
            }

            string entry = siblingEntries[index];

            stack.Push(
                (directory, siblingEntries, index + 1));

            bool isDirectory = Directory.Exists(entry);

            if (isDirectory
                && IsListDirectorySkipFolder(Path.GetFileName(entry)))
            {
                continue;
            }

            ListDirectoryEntryValidationObserverForTests?.Invoke(entry);

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                    _workspaceRoot!,
                    entry,
                    out string? resolvedEntry))
            {
                continue;
            }

            yield return entry;

            if (recursive && isDirectory)
            {

                string canonicalDirectory = Path.GetFullPath(
                    resolvedEntry ?? entry);

                bool isAlias = !visitedCanonicalDirectories.Comparer.Equals(
                    canonicalDirectory,
                    Path.GetFullPath(entry));

                bool isInScope = WorkspacePathPolicy.IsPathUnderWorkspace(
                    canonicalScopeRoot,
                    canonicalDirectory);

                // Descend only via the canonical path. A directory symlink is always emitted as an entry,
                // but recursed into only when its target sits outside the listing scope -- inside the
                // scope, the target's own real name will (or already did) own that content, so descending
                // through a symlink alias too can only duplicate it. This makes the resume's per-page
                // visited set irrelevant for in-scope targets: whether to descend one is never a function
                // of what an earlier page already saw, so a fresh page has nothing to forget.
                bool refuse = isAlias && isInScope;

                // An out-of-scope alias target has no real name inside the listed scope for a later page
                // to ever reach on its own, so it needs the cross-page memory an in-scope target does
                // not (W6-4 round 2): recording it here, gated by the continuation token's own byte
                // budget, is what lets a later page recognize this exact canonical directory as already
                // shown instead of re-descending into it through a different alias.
                bool canRecord = !isAlias
                    || isInScope
                    || outOfScopeTracker.TryRecord(canonicalDirectory, entry);

                if (!refuse
                    && canRecord
                    && visitedCanonicalDirectories.Add(
                        canonicalDirectory))
                {

                    stack.Push(
                        (entry, SortedListDirectoryEntries(entry), 0));

                }

            }
        }
    }

    private static bool IsListDirectorySkipFolder(string name) =>
        name is "node_modules" or "bin" or "obj" or ".git";
}
