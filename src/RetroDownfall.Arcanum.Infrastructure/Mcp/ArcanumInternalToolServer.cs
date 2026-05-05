using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
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
    private const int ListDirectoryMaxItems = 500;

    private const string ListDirectoryTruncationSuffix =
        "... [TRUNCATED: Max 500 items reached. Please use a more specific path.]";

    private static readonly JsonElement ReadFileChunkSchema = BuildSchema(static w =>
    {
        w.WriteString("type", "object");

        w.WriteStartObject("properties");

        WriteStringProperty(w, "path", "Absolute path to the file to read.");

        WriteIntegerProperty(w, "startLine", "1-based inclusive starting line number.");

        WriteIntegerProperty(w, "endLine", "1-based inclusive ending line number.");

        w.WriteEndObject();

        w.WriteStartArray("required");

        w.WriteStringValue("path");

        w.WriteStringValue("startLine");

        w.WriteStringValue("endLine");

        w.WriteEndArray();

        w.WriteBoolean("additionalProperties", false);
    });

    private static readonly JsonElement ReplaceTextBlockSchema = BuildSchema(static w =>
    {
        w.WriteString("type", "object");

        w.WriteStartObject("properties");

        WriteStringProperty(w, "path", "Absolute path to the file to patch.");

        WriteStringProperty(w, "exactSearchText", "Verbatim block of text to locate in the file, including whitespace and newlines.");

        WriteStringProperty(w, "replacementText", "Replacement block of text. May be empty to delete the matched block.");

        w.WriteEndObject();

        w.WriteStartArray("required");

        w.WriteStringValue("path");

        w.WriteStringValue("exactSearchText");

        w.WriteStringValue("replacementText");

        w.WriteEndArray();

        w.WriteBoolean("additionalProperties", false);
    });

    private static readonly JsonElement ListDirectorySchema = BuildSchema(static w =>
    {
        w.WriteString("type", "object");

        w.WriteStartObject("properties");

        WriteStringProperty(w, "path", "Absolute path to the directory to list.");

        WriteBooleanProperty(w, "recursive", "When true, lists entries recursively; node_modules, bin, obj, and .git folders are skipped.");

        w.WriteEndObject();

        w.WriteStartArray("required");

        w.WriteStringValue("path");

        w.WriteEndArray();

        w.WriteBoolean("additionalProperties", false);
    });

    private static readonly JsonElement ExecuteCommandSchema = BuildSchema(static w =>
    {
        w.WriteString("type", "object");

        w.WriteStartObject("properties");

        WriteStringProperty(w, "command", "Executable or binary name (no shell).");

        WriteStringProperty(w, "arguments", "Command-line arguments as a single string (may be empty).");

        WriteStringProperty(
            w,
            "workingDirectory",
            "Optional absolute working directory. When set, must be a rooted path.");

        w.WriteEndObject();

        w.WriteStartArray("required");

        w.WriteStringValue("command");

        w.WriteStringValue("arguments");

        w.WriteEndArray();

        w.WriteBoolean("additionalProperties", false);
    });

    private static readonly JsonElement AskHumanSchema = BuildSchema(static w =>
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

    private readonly ChannelReader<string> _fromClient;

    private readonly ChannelWriter<string> _toClient;

    private readonly McpJsonSerializerContext _json;

    private readonly IHumanPromptRegistry _humanPrompts;

    private readonly ILogger<ArcanumInternalToolServer>? _logger;

    internal ArcanumInternalToolServer(
        ChannelReader<string> fromClient,
        ChannelWriter<string> toClient,
        IHumanPromptRegistry humanPromptRegistry,
        ILogger<ArcanumInternalToolServer>? logger = null,
        McpJsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(fromClient);

        ArgumentNullException.ThrowIfNull(toClient);

        ArgumentNullException.ThrowIfNull(humanPromptRegistry);

        _fromClient = fromClient;

        _toClient = toClient;

        _humanPrompts = humanPromptRegistry;

        _logger = logger;

        _json = jsonContext ?? McpJsonSerializerContext.Default;
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
                    InputSchema = ReadFileChunkSchema,
                },
                new McpToolDefinitionWire
                {
                    Name = "replace_text_block",
                    Description = "Replaces an exact block of text in a file with new text. Use this to patch files safely.",
                    InputSchema = ReplaceTextBlockSchema,
                },
                new McpToolDefinitionWire
                {
                    Name = "list_directory",
                    Description =
                        "Lists files and folders under an absolute path. Optional recursion; skips node_modules, bin, obj, and .git; returns at most 500 paths.",
                    InputSchema = ListDirectorySchema,
                },
                new McpToolDefinitionWire
                {
                    Name = "execute_command",
                    Description =
                        "Runs a command without a shell (stdout/stderr captured, 60s timeout, process tree killed on timeout). Requires absolute workingDirectory when set.",
                    InputSchema = ExecuteCommandSchema,
                },
                new McpToolDefinitionWire
                {
                    Name = "ask_human",
                    Description =
                        "Ask the human operator a question and wait for their answer. Use a new random UUID for promptId on every call.",
                    InputSchema = AskHumanSchema,
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

    private async Task<McpToolsCallResultWire> ExecuteReadFileChunkAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ReadFileChunkParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ReadFileChunkParams);
        }
        catch (JsonException ex)
        {
            return ToolError($"Invalid arguments for read_file_chunk: {ex.Message}");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Path))
        {
            return ToolError("read_file_chunk requires 'path', 'startLine', and 'endLine'.");
        }

        if (!Path.IsPathRooted(args.Path))
        {
            return ToolError($"read_file_chunk requires an absolute path; got: '{args.Path}'.");
        }

        if (args.StartLine < 1 || args.EndLine < args.StartLine)
        {
            return ToolError(
                $"read_file_chunk requires 1 <= startLine <= endLine; got startLine={args.StartLine}, endLine={args.EndLine}.");
        }

        string absolutePath;

        try
        {
            absolutePath = Path.GetFullPath(args.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return ToolError($"read_file_chunk could not resolve path '{args.Path}': {ex.Message}");
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
        ReplaceTextBlockParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ReplaceTextBlockParams);
        }
        catch (JsonException ex)
        {
            return ToolError($"Invalid arguments for replace_text_block: {ex.Message}");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Path))
        {
            return ToolError("replace_text_block requires 'path', 'exactSearchText', and 'replacementText'.");
        }

        if (!Path.IsPathRooted(args.Path))
        {
            return ToolError($"replace_text_block requires an absolute path; got: '{args.Path}'.");
        }

        if (args.ExactSearchText.Length == 0)
        {
            return ToolError("replace_text_block: 'exactSearchText' must be non-empty.");
        }

        string absolutePath;

        try
        {
            absolutePath = Path.GetFullPath(args.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return ToolError($"replace_text_block could not resolve path '{args.Path}': {ex.Message}");
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

    private Task<McpToolsCallResultWire> ExecuteListDirectoryAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ListDirectoryParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ListDirectoryParams);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(ToolError($"Invalid arguments for list_directory: {ex.Message}"));
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Path))
        {
            return Task.FromResult(ToolError("list_directory requires 'path'."));
        }

        if (!Path.IsPathRooted(args.Path))
        {
            return Task.FromResult(ToolError($"list_directory requires an absolute path; got: '{args.Path}'."));
        }

        string absolutePath;

        try
        {
            absolutePath = Path.GetFullPath(args.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return Task.FromResult(ToolError($"list_directory could not resolve path '{args.Path}': {ex.Message}"));
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

            List<string> lines = new(ListDirectoryMaxItems + 1);

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

                        if (lines.Count >= ListDirectoryMaxItems)
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

                    if (lines.Count >= ListDirectoryMaxItems)
                    {
                        truncated = true;

                        break;
                    }

                    lines.Add(entry);
                }
            }

            if (truncated)
            {
                lines.Add(ListDirectoryTruncationSuffix);
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

        string? workingDirectory = string.IsNullOrWhiteSpace(args.WorkingDirectory) ? null : args.WorkingDirectory;

        if (workingDirectory is not null)
        {
            if (!Path.IsPathRooted(workingDirectory))
            {
                return ToolError($"execute_command requires an absolute workingDirectory when set; got: '{workingDirectory}'.");
            }

            try
            {
                workingDirectory = Path.GetFullPath(workingDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                return ToolError($"execute_command could not resolve workingDirectory '{args.WorkingDirectory}': {ex.Message}");
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
        };

        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        using Process process = new();

        process.StartInfo = psi;

        using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(60));

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
                return ToolError("execute_command: the command timed out after 60 seconds.");
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
