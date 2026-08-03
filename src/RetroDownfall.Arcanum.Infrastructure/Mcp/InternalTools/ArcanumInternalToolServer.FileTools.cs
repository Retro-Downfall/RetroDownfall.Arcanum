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

        int take = requestedLines;

        List<string> selected = new(take);

        int maxReadBytes = checked((int)
            ArcanumSettingClamps.MaxFileReadSizeBytes(
                _maxFileReadSizeBytes));

        (string? content, McpToolsCallResultWire? readError) =
            await SandboxedFileIo.TryReadAllTextAsync(
                    _workspaceRoot!,
                    absolutePath,
                    maxReadBytes,
                    cancellationToken)
                .ConfigureAwait(false);

        if (content is null)
        {
            return PrefixToolError("read_file_chunk", readError!);
        }

        using StringReader reader = new(content);

        int currentLine = 0;

        while (currentLine < args.StartLine - 1)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? skipped = reader.ReadLine();

            if (skipped is null)
            {
                break;
            }

            currentLine++;
        }

        while (selected.Count < take)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line = reader.ReadLine();

            if (line is null)
            {
                break;
            }

            selected.Add(line);
        }

        string joined = string.Join("\n", selected);

        return CapToolTextResult(joined, "read_file_chunk");
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

        if (args is null || string.IsNullOrWhiteSpace(args.RelativePath))
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

            bool continuationReached = afterPath is null;

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

            foreach (string entry in EnumerateListDirectoryEntries(
                         absolutePath,
                         args.Recursive,
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string relativePath = Path.GetRelativePath(_workspaceRoot!, entry)
                    .Replace(Path.DirectorySeparatorChar, '/');

                if (!continuationReached)
                {

                    if (string.Equals(
                            relativePath,
                            afterPath,
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal))
                    {

                        continuationReached = true;

                    }

                    continue;

                }

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

            if (!continuationReached)
            {

                return ToolError(
                    "list_directory: the opaque continuation entry no longer exists in this directory snapshot. No page was checkpointed; restart from the first page to observe the changed workspace safely.");

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

    private IEnumerable<string> EnumerateListDirectoryEntries(
        string absolutePath,
        bool recursive,
        CancellationToken cancellationToken)
    {

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                _workspaceRoot!,
                absolutePath,
                out string? resolvedRoot))
        {

            yield break;

        }

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        HashSet<string> visitedCanonicalDirectories = new(
            pathComparer);

        _ = visitedCanonicalDirectories.Add(
            Path.GetFullPath(resolvedRoot ?? absolutePath));

        Queue<string> directories = new();

        directories.Enqueue(absolutePath);

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string directory = directories.Dequeue();

            string[] entries = Directory.EnumerateFileSystemEntries(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();

            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                        _workspaceRoot!,
                        entry,
                        out string? resolvedEntry))
                {
                    continue;
                }

                bool isDirectory = Directory.Exists(entry);

                if (isDirectory
                    && IsListDirectorySkipFolder(Path.GetFileName(entry)))
                {
                    continue;
                }

                yield return entry;

                if (recursive && isDirectory)
                {

                    string canonicalDirectory = Path.GetFullPath(
                        resolvedEntry ?? entry);

                    if (visitedCanonicalDirectories.Add(
                            canonicalDirectory))
                    {

                        directories.Enqueue(entry);

                    }

                }
            }

            if (!recursive)
            {
                yield break;
            }
        }
    }

    private static bool IsListDirectorySkipFolder(string name) =>
        name is "node_modules" or "bin" or "obj" or ".git";
}
