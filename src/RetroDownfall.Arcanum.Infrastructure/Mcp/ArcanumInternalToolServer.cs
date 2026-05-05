using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// In-process MCP JSON-RPC server (Arcanum native tools). Uses the same newline-delimited framing as stdio MCP.
/// </summary>
internal sealed class ArcanumInternalToolServer
{
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

    private readonly ChannelReader<string> _fromClient;

    private readonly ChannelWriter<string> _toClient;

    private readonly McpJsonSerializerContext _json;

    private readonly ILogger<ArcanumInternalToolServer>? _logger;

    internal ArcanumInternalToolServer(
        ChannelReader<string> fromClient,
        ChannelWriter<string> toClient,
        ILogger<ArcanumInternalToolServer>? logger = null,
        McpJsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(fromClient);

        ArgumentNullException.ThrowIfNull(toClient);

        _fromClient = fromClient;

        _toClient = toClient;

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
            _ => ToolError($"Unknown tool: {call.Name}"),
        };

        return BuildToolsCallResponse(rpcId, result);
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
}
