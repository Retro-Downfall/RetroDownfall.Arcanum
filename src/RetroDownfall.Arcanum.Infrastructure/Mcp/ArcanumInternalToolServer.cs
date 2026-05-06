using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// In-process MCP JSON-RPC server (Arcanum native tools). Uses the same newline-delimited framing as stdio MCP.
/// </summary>
internal sealed class ArcanumInternalToolServer
{

    private const string WorkspaceNotConfiguredMessage =
        "Workspace not configured. This tool requires a valid workspace.";

    private const string PathEscapesSandboxMessage =
        "That path would leave the workspace sandbox, so the operation was not performed. Please use a path relative to the workspace root.";

    private readonly ChannelReader<string> _fromClient;

    private readonly ChannelWriter<string> _toClient;

    private readonly McpJsonSerializerContext _json;

    private readonly IHumanPromptRegistry _humanPrompts;

    private readonly ILogger<ArcanumInternalToolServer>? _logger;

    private readonly string? _workspaceRoot;

    private readonly TimeSpan _executeCommandTimeout;

    private readonly int _executeCommandTimeoutSeconds;

    private readonly int _listDirectoryMaxPaths;

    private readonly string _listDirectoryTruncationSuffix;

    private readonly string _listDirectoryToolsListDescription;

    private readonly JsonElement _readFileChunkSchema;

    private readonly JsonElement _replaceTextBlockSchema;

    private readonly JsonElement _writeFileSchema;

    private readonly JsonElement _listDirectorySchema;

    private readonly JsonElement _executeCommandSchema;

    private readonly JsonElement _askHumanSchema;

    private readonly string _executeCommandToolDescription;

    internal ArcanumInternalToolServer(
        ChannelReader<string> fromClient,
        ChannelWriter<string> toClient,
        IHumanPromptRegistry humanPromptRegistry,
        string? workspaceRootNormalizedOrNull,
        TimeSpan executeCommandTimeout,
        int executeCommandTimeoutSecondsForDisplay,
        int listDirectoryMaxPaths,
        ILogger<ArcanumInternalToolServer>? logger = null,
        McpJsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(fromClient);

        ArgumentNullException.ThrowIfNull(toClient);

        ArgumentNullException.ThrowIfNull(humanPromptRegistry);

        if (listDirectoryMaxPaths < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(listDirectoryMaxPaths));
        }

        _fromClient = fromClient;

        _toClient = toClient;

        _humanPrompts = humanPromptRegistry;

        _logger = logger;

        _json = jsonContext ?? McpJsonSerializerContext.Default;

        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRootNormalizedOrNull)
            ? null
            : workspaceRootNormalizedOrNull;

        _executeCommandTimeout = executeCommandTimeout;

        _executeCommandTimeoutSeconds = executeCommandTimeoutSecondsForDisplay;

        _listDirectoryMaxPaths = listDirectoryMaxPaths;

        _listDirectoryTruncationSuffix =
            $"... [TRUNCATED: Max {listDirectoryMaxPaths} items reached. Please use a more specific path.]";

        _listDirectoryToolsListDescription =
            "Lists files and folders under a path relative to the workspace root. Optional recursion; skips node_modules, bin, obj, and .git; returns at most "
            + $"{listDirectoryMaxPaths} paths.";

        _readFileChunkSchema = BuildReadFileChunkSchema();

        _replaceTextBlockSchema = BuildReplaceTextBlockSchema();

        _writeFileSchema = BuildWriteFileSchema();

        _listDirectorySchema = BuildListDirectorySchema();

        _executeCommandSchema = BuildExecuteCommandSchema(_executeCommandTimeoutSeconds);

        _askHumanSchema = BuildAskHumanSchema();

        _executeCommandToolDescription =
            $"Runs a command without a shell (stdout/stderr captured, {_executeCommandTimeoutSeconds}s timeout, process tree killed on timeout). Optional workingDirectory is relative to the workspace root.";
    }

    /// <summary>
    /// Processes inbound JSON-RPC lines until <paramref name="cancellationToken"/> is canceled or the client completes the channel.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (string line in _fromClient.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Arcanum internal MCP server failed handling one line.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _toClient.TryComplete();
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        using JsonDocument doc = JsonDocument.Parse(line);

        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("method", out JsonElement methodProp) || methodProp.ValueKind != JsonValueKind.String)
        {
            return;
        }

        if (!root.TryGetProperty("id", out _))
        {
            return;
        }

        JsonRpcRequest? request = JsonSerializer.Deserialize(root, _json.JsonRpcRequest);

        if (request is null || string.IsNullOrEmpty(request.Method) || request.Id is null)
        {
            return;
        }

        JsonElement rpcId = request.Id.Value;

        try
        {
            JsonRpcResponse response = request.Method switch
            {
                "initialize" => BuildInitializeResponse(request, rpcId),
                "tools/list" => BuildToolsListResponse(rpcId),
                "tools/call" => await BuildToolsCallResponseAsync(request, rpcId, cancellationToken).ConfigureAwait(false),
                _ => BuildMethodNotFoundResponse(rpcId, request.Method),
            };

            string wire = JsonSerializer.Serialize(response, _json.JsonRpcResponse);

            await _toClient.WriteAsync(wire + "\n", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Arcanum internal MCP error building response for method {Method}.", request.Method);

            JsonRpcResponse err = new()
            {
                Id = rpcId,
                Error = new JsonRpcError
                {
                    Code = -32603,
                    Message = "Internal error.",
                    Data = null,
                },
                Result = null,
            };

            string wire = JsonSerializer.Serialize(err, _json.JsonRpcResponse);

            await _toClient.WriteAsync(wire + "\n", cancellationToken).ConfigureAwait(false);
        }
    }

    private JsonRpcResponse BuildInitializeResponse(JsonRpcRequest request, JsonElement rpcId)
    {
        string protocolVersion = "2024-11-05";

        if (request.Params is { } p)
        {
            McpInitializeParams? init = JsonSerializer.Deserialize(p, _json.McpInitializeParams);

            if (!string.IsNullOrWhiteSpace(init?.ProtocolVersion))
            {
                protocolVersion = init.ProtocolVersion;
            }
        }

        McpInitializeServerResult body = new()
        {
            ProtocolVersion = protocolVersion,
            Capabilities = new McpServerCapabilitiesWire(),
            ServerInfo = new McpServerInfoWire
            {
                Name = "ArcanumInternal",

                Version =
                    typeof(ArcanumInternalToolServer).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            },
        };

        JsonElement result = JsonSerializer.SerializeToElement(body, _json.McpInitializeServerResult);

        return new JsonRpcResponse { Id = rpcId, Result = result, Error = null };
    }

    private JsonRpcResponse BuildToolsListResponse(JsonElement rpcId)
    {
        McpToolsListResultWire body = new()
        {
            Tools =
            [
                new McpToolDefinitionWire
                {
                    Name = "read_file_chunk",
                    Description = "Reads a specific range of lines from a file to avoid token exhaustion.",
                    InputSchema = _readFileChunkSchema,
                },
                new McpToolDefinitionWire
                {
                    Name = "replace_text_block",
                    Description = "Replaces an exact block of text in a file with new text. Use this to patch files safely.",
                    InputSchema = _replaceTextBlockSchema,
                },
                new McpToolDefinitionWire
                {
                    Name = "write_file",
                    Description = "Create a new file or completely overwrite an existing file",
                    InputSchema = _writeFileSchema,
                },
                new McpToolDefinitionWire
                {
                    Name = "list_directory",
                    Description = _listDirectoryToolsListDescription,
                    InputSchema = _listDirectorySchema,
                },
                new McpToolDefinitionWire
                {
                    Name = "execute_command",
                    Description = _executeCommandToolDescription,
                    InputSchema = _executeCommandSchema,
                },
                new McpToolDefinitionWire
                {
                    Name = "ask_human",
                    Description =
                        "Ask the human operator a question and wait for their answer. Use a new random UUID for promptId on every call.",
                    InputSchema = _askHumanSchema,
                },
            ],
        };

        JsonElement result = JsonSerializer.SerializeToElement(body, _json.McpToolsListResultWire);

        return new JsonRpcResponse { Id = rpcId, Result = result, Error = null };
    }

    private async Task<JsonRpcResponse> BuildToolsCallResponseAsync(
        JsonRpcRequest request,
        JsonElement rpcId,
        CancellationToken cancellationToken)
    {
        if (request.Params is not { } paramsElement)
        {
            return BuildToolsCallResponse(rpcId, ToolError("tools/call requires params with 'name' and 'arguments'."));
        }

        McpToolsCallParams? call;

        try
        {
            call = JsonSerializer.Deserialize(paramsElement, _json.McpToolsCallParams);
        }
        catch (JsonException ex)
        {
            return BuildToolsCallResponse(rpcId, ToolError($"tools/call params were not valid JSON: {ex.Message}"));
        }

        if (call is null || string.IsNullOrWhiteSpace(call.Name))
        {
            return BuildToolsCallResponse(rpcId, ToolError("tools/call params missing required 'name'."));
        }

        McpToolsCallResultWire result = call.Name switch
        {
            "read_file_chunk" => await ExecuteReadFileChunkAsync(call.Arguments, cancellationToken).ConfigureAwait(false),
            "replace_text_block" => await ExecuteReplaceTextBlockAsync(call.Arguments, cancellationToken).ConfigureAwait(false),
            "write_file" => await ExecuteWriteFileAsync(call.Arguments, cancellationToken).ConfigureAwait(false),
            "list_directory" => await ExecuteListDirectoryAsync(call.Arguments, cancellationToken).ConfigureAwait(false),
            "execute_command" => await ExecuteCommandAsync(call.Arguments, cancellationToken).ConfigureAwait(false),
            "ask_human" => await ExecuteAskHumanAsync(call.Arguments, cancellationToken).ConfigureAwait(false),
            _ => ToolError($"Unknown tool: {call.Name}"),
        };

        return BuildToolsCallResponse(rpcId, result);
    }

    private async Task<McpToolsCallResultWire> ExecuteAskHumanAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        AskHumanParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.AskHumanParams);
        }
        catch (JsonException ex)
        {
            return ToolError($"Invalid arguments for ask_human: {ex.Message}");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Question) || string.IsNullOrWhiteSpace(args.PromptId))
        {
            return ToolError("ask_human requires non-empty 'question' and 'promptId'.");
        }

        try
        {
            string answer = await _humanPrompts
                .WaitForResponseAsync(args.PromptId.Trim(), cancellationToken)
                .ConfigureAwait(false);

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = answer },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return ToolError(ex.Message);
        }
    }

    private JsonRpcResponse BuildToolsCallResponse(JsonElement rpcId, McpToolsCallResultWire result)
    {
        JsonElement element = JsonSerializer.SerializeToElement(result, _json.McpToolsCallResultWire);

        return new JsonRpcResponse { Id = rpcId, Result = element, Error = null };
    }

    private McpToolsCallResultWire? TryRequireWorkspaceRoot()
    {
        if (string.IsNullOrWhiteSpace(_workspaceRoot))
        {
            return ToolError(WorkspaceNotConfiguredMessage);
        }

        return null;
    }

    private bool TryResolveSandboxedPath(
        string relativePath,
        [NotNullWhen(true)] out string? absolutePath,
        out McpToolsCallResultWire? error)
    {
        absolutePath = null;

        error = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = ToolError("A non-empty relativePath is required.");

            return false;
        }

        if (Path.IsPathRooted(relativePath))
        {
            error = ToolError(
                $"Paths must be relative to the workspace root; rooted paths are not allowed (got: '{relativePath}').");

            return false;
        }

        string root = _workspaceRoot!;

        string resolved;

        try
        {
            resolved = Path.GetFullPath(Path.Combine(root, relativePath.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            error = ToolError($"Could not resolve relativePath '{relativePath}': {ex.Message}");

            return false;
        }

        if (!ToolHelpers.IsPathUnderWorkspace(root, resolved))
        {
            error = ToolError(PathEscapesSandboxMessage);

            return false;
        }

        absolutePath = resolved;

        return true;
    }

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
            return ToolError($"Invalid arguments for read_file_chunk: {ex.Message}");
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

        if (!TryResolveSandboxedPath(args.RelativePath, out string? absolutePath, out McpToolsCallResultWire? resolveErr))
        {
            return resolveErr!;
        }

        int take = args.EndLine - args.StartLine + 1;

        List<string> selected = new(take);

        try
        {
            IAsyncEnumerable<string> lines = File.ReadLinesAsync(absolutePath, cancellationToken)
                .Skip(args.StartLine - 1)
                .Take(take);

            await foreach (string line in lines.ConfigureAwait(false))
            {
                selected.Add(line);
            }
        }
        catch (FileNotFoundException ex)
        {
            return ToolError($"read_file_chunk: file not found: '{absolutePath}'. {ex.Message}");
        }
        catch (DirectoryNotFoundException ex)
        {
            return ToolError($"read_file_chunk: directory not found for '{absolutePath}'. {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolError($"read_file_chunk: access denied reading '{absolutePath}'. {ex.Message}");
        }
        catch (IOException ex)
        {
            return ToolError($"read_file_chunk: I/O error reading '{absolutePath}'. {ex.Message}");
        }

        string joined = string.Join("\n", selected);

        return new McpToolsCallResultWire
        {
            Content =
            [
                new McpToolContentTextWire { Text = joined },
            ],
            IsError = false,
        };
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
            return ToolError($"Invalid arguments for replace_text_block: {ex.Message}");
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

        string content;

        try
        {
            content = await File.ReadAllTextAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            return ToolError($"replace_text_block: file not found: '{absolutePath}'. {ex.Message}");
        }
        catch (DirectoryNotFoundException ex)
        {
            return ToolError($"replace_text_block: directory not found for '{absolutePath}'. {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolError($"replace_text_block: access denied reading '{absolutePath}'. {ex.Message}");
        }
        catch (IOException ex)
        {
            return ToolError($"replace_text_block: I/O error reading '{absolutePath}'. {ex.Message}");
        }

        if (!content.Contains(args.ExactSearchText, StringComparison.Ordinal))
        {
            return ToolError(
                $"Exact search text not found in '{absolutePath}'. Re-read the file with read_file_chunk and use a verbatim block (including whitespace and newlines) before retrying.");
        }

        int occurrences = CountOccurrences(content, args.ExactSearchText);

        string updated = content.Replace(args.ExactSearchText, args.ReplacementText, StringComparison.Ordinal);

        try
        {
            await File.WriteAllTextAsync(absolutePath, updated, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolError($"replace_text_block: access denied writing '{absolutePath}'. {ex.Message}");
        }
        catch (IOException ex)
        {
            return ToolError($"replace_text_block: I/O error writing '{absolutePath}'. {ex.Message}");
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
            return ToolError($"Invalid arguments for write_file: {ex.Message}");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.RelativePath))
        {
            return ToolError("write_file requires 'relativePath' and 'content'.");
        }

        if (!TryResolveSandboxedPath(args.RelativePath, out string? absolutePath, out McpToolsCallResultWire? resolveErr))
        {
            return resolveErr!;
        }

        string? parentDir = Path.GetDirectoryName(absolutePath);

        if (!string.IsNullOrEmpty(parentDir))
        {
            try
            {
                Directory.CreateDirectory(parentDir);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ToolError($"write_file: access denied creating directory for '{absolutePath}'. {ex.Message}");
            }
            catch (IOException ex)
            {
                return ToolError($"write_file: I/O error creating directory for '{absolutePath}'. {ex.Message}");
            }
        }

        try
        {
            await File.WriteAllTextAsync(absolutePath, args.Content, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolError($"write_file: access denied writing '{absolutePath}'. {ex.Message}");
        }
        catch (IOException ex)
        {
            return ToolError($"write_file: I/O error writing '{absolutePath}'. {ex.Message}");
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

    private Task<McpToolsCallResultWire> ExecuteListDirectoryAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        McpToolsCallResultWire? gate = TryRequireWorkspaceRoot();

        if (gate is not null)
        {
            return Task.FromResult(gate);
        }

        ListDirectoryParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ListDirectoryParams);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(ToolError($"Invalid arguments for list_directory: {ex.Message}"));
        }

        if (args is null || string.IsNullOrWhiteSpace(args.RelativePath))
        {
            return Task.FromResult(ToolError("list_directory requires 'relativePath'."));
        }

        if (!TryResolveSandboxedPath(args.RelativePath, out string? absolutePath, out McpToolsCallResultWire? resolveErr))
        {
            return Task.FromResult(resolveErr!);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(absolutePath) && !Directory.Exists(absolutePath))
            {
                return Task.FromResult(ToolError($"list_directory: path is not a directory: '{absolutePath}'."));
            }

            if (!Directory.Exists(absolutePath))
            {
                return Task.FromResult(ToolError($"list_directory: directory not found: '{absolutePath}'."));
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
                        return Task.FromResult(ToolError($"list_directory: I/O error listing '{dir}'. {ex.Message}"));
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        return Task.FromResult(ToolError($"list_directory: access denied listing '{dir}'. {ex.Message}"));
                    }

                    foreach (string entry in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

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
                    return Task.FromResult(ToolError($"list_directory: I/O error listing '{absolutePath}'. {ex.Message}"));
                }
                catch (UnauthorizedAccessException ex)
                {
                    return Task.FromResult(
                        ToolError($"list_directory: access denied listing '{absolutePath}'. {ex.Message}"));
                }

                foreach (string entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

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

            return Task.FromResult(
                new McpToolsCallResultWire
                {
                    Content =
                    [
                        new McpToolContentTextWire { Text = joined },
                    ],
                    IsError = false,
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromResult(ToolError($"list_directory: canceled. {ex.Message}"));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolError($"list_directory: I/O error for '{absolutePath}'. {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolError($"list_directory: access denied for '{absolutePath}'. {ex.Message}"));
        }
    }

    private static bool IsListDirectorySkipFolder(string name) =>
        name is "node_modules" or "bin" or "obj" or ".git";

    private async Task<McpToolsCallResultWire> ExecuteCommandAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        McpToolsCallResultWire? gate = TryRequireWorkspaceRoot();

        if (gate is not null)
        {
            return gate;
        }

        ExecuteCommandParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ExecuteCommandParams);
        }
        catch (JsonException ex)
        {
            return ToolError($"Invalid arguments for execute_command: {ex.Message}");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Command))
        {
            return ToolError("execute_command requires 'command' and 'arguments'.");
        }

        string argumentsLine = args.Arguments;

        string root = _workspaceRoot!;

        string workingDir;

        if (string.IsNullOrWhiteSpace(args.WorkingDirectory))
        {
            workingDir = root;
        }
        else
        {
            if (Path.IsPathRooted(args.WorkingDirectory))
            {
                return ToolError(
                    "execute_command: workingDirectory must be relative to the workspace root; absolute paths are not allowed.");
            }

            if (!TryResolveSandboxedPath(args.WorkingDirectory.Trim(), out string? resolvedCwd, out McpToolsCallResultWire? cwdErr))
            {
                return cwdErr!;
            }

            workingDir = resolvedCwd;

            if (!Directory.Exists(workingDir))
            {
                return ToolError(
                    $"execute_command: workingDirectory does not exist or is not a directory: '{args.WorkingDirectory}'.");
            }
        }

        ProcessStartInfo psi = new()
        {
            FileName = args.Command,

            Arguments = argumentsLine,

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,

            WorkingDirectory = workingDir,
        };

        using Process process = new();

        process.StartInfo = psi;

        using CancellationTokenSource timeoutCts = new(_executeCommandTimeout);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        CancellationToken waitToken = linked.Token;

        try
        {
            if (!process.Start())
            {
                return ToolError("execute_command: failed to start the process.");
            }
        }
        catch (IOException ex)
        {
            return ToolError($"execute_command: I/O error starting process. {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolError($"execute_command: access denied starting process. {ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            return ToolError($"execute_command: canceled before start completed. {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return ToolError($"execute_command: could not start process. {ex.Message}");
        }
        catch (Win32Exception ex)
        {
            return ToolError($"execute_command: could not start process. {ex.Message}");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(waitToken);

        Task<string> stderrTask = process.StandardError.ReadToEndAsync(waitToken);

        try
        {
            await process.WaitForExitAsync(waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            TryKillProcessEntireTree(process);

            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return ToolError(
                    $"execute_command: the command timed out after {_executeCommandTimeoutSeconds} seconds.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return ToolError($"execute_command: canceled. {ex.Message}");
            }

            return ToolError($"execute_command: canceled or timed out. {ex.Message}");
        }

        string stdout;

        string stderr;

        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);

            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return ToolError($"execute_command: I/O error reading process output. {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolError($"execute_command: access denied reading process output. {ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            return ToolError($"execute_command: canceled while reading output. {ex.Message}");
        }

        StringBuilder text = new();

        text.AppendLine("--- stdout ---");

        text.AppendLine(stdout);

        text.AppendLine("--- stderr ---");

        text.AppendLine(stderr);

        text.Append("--- exit code ---\n");

        text.Append(process.ExitCode);

        return new McpToolsCallResultWire
        {
            Content =
            [
                new McpToolContentTextWire { Text = text.ToString() },
            ],
            IsError = false,
        };
    }

    private static void TryKillProcessEntireTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static McpToolsCallResultWire ToolError(string text)
    {
        return new McpToolsCallResultWire
        {
            Content =
            [
                new McpToolContentTextWire { Text = text },
            ],
            IsError = true,
        };
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;

        int index = 0;

        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;

            index += value.Length;
        }

        return count;
    }

    private static JsonRpcResponse BuildMethodNotFoundResponse(JsonElement rpcId, string method)
    {
        return new JsonRpcResponse
        {
            Id = rpcId,
            Result = null,
            Error = new JsonRpcError
            {
                Code = -32601,
                Message = $"Method not found: {method}",
                Data = null,
            },
        };
    }

    private static JsonElement BuildReadFileChunkSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "relativePath",
                "Path to the file relative to the workspace root (not an absolute path).");

            WriteIntegerProperty(w, "startLine", "1-based inclusive starting line number.");

            WriteIntegerProperty(w, "endLine", "1-based inclusive ending line number.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("relativePath");

            w.WriteStringValue("startLine");

            w.WriteStringValue("endLine");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildReplaceTextBlockSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "relativePath",
                "Path to the file relative to the workspace root (not an absolute path).");

            WriteStringProperty(w, "exactSearchText", "Verbatim block of text to locate in the file, including whitespace and newlines.");

            WriteStringProperty(w, "replacementText", "Replacement block of text. May be empty to delete the matched block.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("relativePath");

            w.WriteStringValue("exactSearchText");

            w.WriteStringValue("replacementText");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildWriteFileSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "relativePath",
                "Path to the file relative to the workspace root (not an absolute path).");

            WriteStringProperty(w, "content", "Full file contents. Replaces the entire file if it already exists.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("relativePath");

            w.WriteStringValue("content");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildListDirectorySchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "relativePath",
                "Directory path relative to the workspace root (use '.' for the workspace root).");

            WriteBooleanProperty(w, "recursive", "When true, lists entries recursively; node_modules, bin, obj, and .git folders are skipped.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("relativePath");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildExecuteCommandSchema(int timeoutSeconds)
    {
        return BuildSchema(w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "command",
                $"Executable or binary name (no shell). The host enforces a {timeoutSeconds} second timeout.");

            WriteStringProperty(w, "arguments", "Command-line arguments as a single string (may be empty).");

            WriteStringProperty(
                w,
                "workingDirectory",
                "Optional working directory relative to the workspace root. When omitted, the process runs in the workspace root. Must not be an absolute path.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("command");

            w.WriteStringValue("arguments");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildAskHumanSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(w, "question", "The question or context to show the human operator.");

            WriteStringProperty(
                w,
                "promptId",
                "Unique correlation id for this prompt. Generate a new random UUID (RFC 4122) for every ask_human call.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("question");

            w.WriteStringValue("promptId");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildSchema(Action<Utf8JsonWriter> writeBody)
    {
        ArrayBufferWriter<byte> buffer = new(512);

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            writeBody(writer);

            writer.WriteEndObject();
        }

        using JsonDocument doc = JsonDocument.Parse(buffer.WrittenMemory);

        return doc.RootElement.Clone();
    }

    private static void WriteStringProperty(Utf8JsonWriter w, string name, string description)
    {
        w.WriteStartObject(name);

        w.WriteString("type", "string");

        w.WriteString("description", description);

        w.WriteEndObject();
    }

    private static void WriteIntegerProperty(Utf8JsonWriter w, string name, string description)
    {
        w.WriteStartObject(name);

        w.WriteString("type", "integer");

        w.WriteString("description", description);

        w.WriteEndObject();
    }

    private static void WriteBooleanProperty(Utf8JsonWriter w, string name, string description)
    {
        w.WriteStartObject(name);

        w.WriteString("type", "boolean");

        w.WriteString("description", description);

        w.WriteEndObject();
    }

}
