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

            // Seeks directly to the checkpoint by re-listing only its ancestor directories (a resumed
            // page never re-walks or re-validates the prefix a prior page already emitted); a checkpoint
            // that no longer exists on disk is reported here, up front, exactly as it was when a full
            // replay discovered the same fact by never matching it.
            if (!TrySeekListDirectoryEntries(
                    absolutePath,
                    args.Recursive,
                    afterPath,
                    cancellationToken,
                    out IEnumerable<string> walk))
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
            }

            if (hasMore)
            {

                string continuation = EncodeListDirectoryContinuation(
                    args,
                    lastPath!);

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

    private bool TryDecodeListDirectoryContinuation(
        ListDirectoryParams args,
        out string? afterPath,
        out string? error)
    {

        afterPath = null;

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

            afterPath = payload[(separator + 1)..];

            if (afterPath.Length == 0)
            {

                error = "the opaque continuation token contains no checkpoint path. No work was performed; restart from the first page.";

                afterPath = null;

                return false;

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
        string lastPath)
    {

        string payload = ComputeListDirectoryScopeFingerprint(args)
            + "\n"
            + lastPath;

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));

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
    private bool TrySeekListDirectoryEntries(
        string absolutePath,
        bool recursive,
        string? afterPath,
        CancellationToken cancellationToken,
        out IEnumerable<string> entries)
    {

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                _workspaceRoot!,
                absolutePath,
                out string? resolvedRoot))
        {

            entries = [];

            return true;

        }

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        // The same resolution the containment check above just used for absolutePath itself, so a
        // directory's canonical identity and this root are never compared across a raw-vs-resolved
        // mismatch (e.g. a workspace root reached through its own ancestor symlink).
        string canonicalScopeRoot = Path.GetFullPath(
            resolvedRoot ?? absolutePath);

        HashSet<string> visitedCanonicalDirectories = new(
            pathComparer);

        _ = visitedCanonicalDirectories.Add(
            canonicalScopeRoot);

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

                // An honest checkpoint's ancestor was descended into on the page that emitted it, and the
                // forward walk (WalkListDirectoryEntries below) never descends into a directory symlink
                // whose target sits inside the listing scope -- the target's own real name owns that
                // content instead. An ancestor segment that resolves to an in-scope alias here cannot be
                // a position the walk ever produced; treat it exactly like any other checkpoint that does
                // not resolve to a real position.
                if (!visitedCanonicalDirectories.Comparer.Equals(
                        canonicalAncestor,
                        Path.GetFullPath(matchedEntry))
                    && WorkspacePathPolicy.IsPathUnderWorkspace(
                        canonicalScopeRoot,
                        canonicalAncestor))
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

                    // Descend only via the canonical path: an in-scope alias is emitted (it already was,
                    // as the checkpoint itself, on the page before this one) but never recursed into --
                    // the target's own real name owns its content, exactly as WalkListDirectoryEntries
                    // decides below for the same shape encountered on a forward (non-resumed) walk.
                    bool isInScopeAlias = !visitedCanonicalDirectories.Comparer.Equals(
                            canonicalCheckpoint,
                            Path.GetFullPath(matchedEntry))
                        && WorkspacePathPolicy.IsPathUnderWorkspace(
                            canonicalScopeRoot,
                            canonicalCheckpoint);

                    if (!isInScopeAlias
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

                // Descend only via the canonical path. A directory symlink is always emitted as an entry,
                // but recursed into only when its target sits outside the listing scope -- inside the
                // scope, the target's own real name will (or already did) own that content, so descending
                // through a symlink alias too can only duplicate it. This makes the resume's per-page
                // visited set irrelevant for in-scope targets: whether to descend one is never a function
                // of what an earlier page already saw, so a fresh page has nothing to forget.
                bool isInScopeAlias = !visitedCanonicalDirectories.Comparer.Equals(
                        canonicalDirectory,
                        Path.GetFullPath(entry))
                    && WorkspacePathPolicy.IsPathUnderWorkspace(
                        canonicalScopeRoot,
                        canonicalDirectory);

                if (!isInScopeAlias
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
