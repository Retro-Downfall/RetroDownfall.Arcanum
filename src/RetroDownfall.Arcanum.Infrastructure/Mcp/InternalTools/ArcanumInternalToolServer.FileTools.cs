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

        McpToolsCallResultWire? sizeError = TryRejectIfFileExceedsReadLimit(absolutePath, "read_file_chunk");

        if (sizeError is not null)
        {
            return sizeError;
        }

        int take = requestedLines;

        List<string> selected = new(take);

        if (!SandboxedFileIo.TryOpenForRead(_workspaceRoot!, absolutePath, out FileStream? stream, out McpToolsCallResultWire? openError))
        {

            return PrefixToolError("read_file_chunk", openError!);

        }

        try
        {

            await using (stream)
            {

                using StreamReader reader = new(stream);

                int currentLine = 0;

                while (currentLine < args.StartLine - 1)
                {

                    string? skipped = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                    if (skipped is null)
                    {

                        break;

                    }

                    currentLine++;

                }

                while (selected.Count < take)
                {

                    string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                    if (line is null)
                    {

                        break;

                    }

                    selected.Add(line);

                }

            }

        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "read_file_chunk: access denied.");

            return ToolError("read_file_chunk: access denied.");
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "read_file_chunk: I/O error.");

            return ToolError("read_file_chunk: an I/O error occurred. See server logs.");
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

        McpToolsCallResultWire? sizeError = TryRejectIfFileExceedsReadLimit(absolutePath, "replace_text_block");

        if (sizeError is not null)
        {
            return sizeError;
        }

        ResourceLimits resourceLimits = await ResolveResourceLimitsAsync(cancellationToken).ConfigureAwait(false);

        (string? content, McpToolsCallResultWire? readError) = await SandboxedFileIo.TryReadAllTextAsync(
            _workspaceRoot!,
            absolutePath,
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

            List<string> lines = new(_listDirectoryMaxPaths + 1);

            bool truncated = false;

            if (args.Recursive)
            {
                Queue<string> dirs = new();

                dirs.Enqueue(absolutePath);

                while (dirs.Count > 0 && !truncated)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string dir = dirs.Dequeue();

                    IEnumerable<string> entries;

                    try
                    {
                        entries = Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly);
                    }
                    catch (IOException ex)
                    {
                        _logger?.LogError(ex, "list_directory: I/O error listing subdirectory.");

                        return ToolError("list_directory: an I/O error occurred. See server logs.");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger?.LogError(ex, "list_directory: access denied listing subdirectory.");

                        return ToolError("list_directory: access denied.");
                    }

                    foreach (string entry in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_workspaceRoot!, entry, out _))
                        {
                            continue;
                        }

                        string name = Path.GetFileName(entry);

                        if (Directory.Exists(entry) && IsListDirectorySkipFolder(name))
                        {
                            continue;
                        }

                        if (lines.Count >= _listDirectoryMaxPaths)
                        {
                            truncated = true;

                            break;
                        }

                        lines.Add(entry);

                        if (Directory.Exists(entry))
                        {
                            dirs.Enqueue(entry);
                        }
                    }
                }
            }
            else
            {
                IEnumerable<string> entries;

                try
                {
                    entries = Directory.EnumerateFileSystemEntries(
                        absolutePath,
                        "*",
                        SearchOption.TopDirectoryOnly);
                }
                catch (IOException ex)
                {
                    _logger?.LogError(ex, "list_directory: I/O error listing root.");

                    return ToolError("list_directory: an I/O error occurred. See server logs.");
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger?.LogError(ex, "list_directory: access denied listing root.");

                    return ToolError("list_directory: access denied.");
                }

                foreach (string entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(_workspaceRoot!, entry, out _))
                    {
                        continue;
                    }

                    string name = Path.GetFileName(entry);

                    if (Directory.Exists(entry) && IsListDirectorySkipFolder(name))
                    {
                        continue;
                    }

                    if (lines.Count >= _listDirectoryMaxPaths)
                    {
                        truncated = true;

                        break;
                    }

                    lines.Add(entry);
                }
            }

            if (truncated)
            {
                lines.Add(_listDirectoryTruncationSuffix);
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

    private static bool IsListDirectorySkipFolder(string name) =>
        name is "node_modules" or "bin" or "obj" or ".git";
}
