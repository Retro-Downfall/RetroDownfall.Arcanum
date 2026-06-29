using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// In-process MCP JSON-RPC server (Arcanum native tools). Uses the same newline-delimited framing as stdio MCP.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: in-process MCP JSON-RPC tool server; handler behavior covered via ArcanumInternalToolServerTests.
internal sealed partial class ArcanumInternalToolServer
{

    private const string WorkspaceNotConfiguredMessage =
        "Workspace not configured. This tool requires a valid workspace.";

    private const string PathEscapesSandboxMessage =
        "That path would leave the workspace sandbox, so the operation was not performed. Please use a path relative to the workspace root.";

    private static readonly JsonElement NullId = JsonDocument.Parse("null").RootElement.Clone();

    private readonly ChannelReader<string> _fromClient;

    private readonly ChannelWriter<string> _toClient;

    private readonly McpJsonSerializerContext _json;

    private readonly IHumanPromptRegistry _humanPrompts;

    private readonly IServiceScopeFactory _scopeFactory;

    private readonly IUnseenServantPacer _pacer;

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

    private readonly JsonElement _readLoreSchema;

    private readonly JsonElement _scribeLoreSchema;

    private readonly JsonElement _deleteLoreSchema;

    private readonly JsonElement _searchArchivesSchema;

    private readonly JsonElement _adjustInitiativeSchema;

    private readonly JsonElement _useCommlinkSchema;

    private readonly JsonElement _petitionDungeonMasterSchema;

    private readonly JsonElement _castSendingSchema;

    private readonly IntelligenceSettings _settings;

    private readonly long _maxFileReadSizeBytes;

    private readonly bool _conclaveEnabled;

    private readonly McpRequestCancellationBroker _requestCancellationBroker;

    private readonly int _maxJsonRpcLineBytes;

    private readonly string _executeCommandToolDescription;

    internal ArcanumInternalToolServer(
        ChannelReader<string> fromClient,
        ChannelWriter<string> toClient,
        IHumanPromptRegistry humanPromptRegistry,
        IServiceScopeFactory scopeFactory,
        IUnseenServantPacer pacer,
        string? workspaceRootNormalizedOrNull,
        TimeSpan executeCommandTimeout,
        int executeCommandTimeoutSecondsForDisplay,
        int listDirectoryMaxPaths,
        IntelligenceSettings intelligenceSettings,
        long maxFileReadSizeBytes,
        bool conclaveEnabled,
        McpRequestCancellationBroker requestCancellationBroker,
        int maxJsonRpcLineBytes,
        ILogger<ArcanumInternalToolServer>? logger = null,
        McpJsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(fromClient);

        ArgumentNullException.ThrowIfNull(toClient);

        ArgumentNullException.ThrowIfNull(humanPromptRegistry);

        ArgumentNullException.ThrowIfNull(scopeFactory);

        ArgumentNullException.ThrowIfNull(pacer);

        ArgumentNullException.ThrowIfNull(intelligenceSettings);

        ArgumentNullException.ThrowIfNull(requestCancellationBroker);

        if (listDirectoryMaxPaths < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(listDirectoryMaxPaths));
        }

        if (maxJsonRpcLineBytes < 1)
        {

            throw new ArgumentOutOfRangeException(nameof(maxJsonRpcLineBytes));

        }

        _fromClient = fromClient;

        _toClient = toClient;

        _humanPrompts = humanPromptRegistry;

        _scopeFactory = scopeFactory;

        _pacer = pacer;

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

        _settings = intelligenceSettings;

        _maxFileReadSizeBytes = maxFileReadSizeBytes;

        _conclaveEnabled = conclaveEnabled;

        _requestCancellationBroker = requestCancellationBroker;

        _maxJsonRpcLineBytes = maxJsonRpcLineBytes;

        _readFileChunkSchema = BuildReadFileChunkSchema();

        _replaceTextBlockSchema = BuildReplaceTextBlockSchema();

        _writeFileSchema = BuildWriteFileSchema();

        _listDirectorySchema = BuildListDirectorySchema();

        _executeCommandSchema = BuildExecuteCommandSchema(_executeCommandTimeoutSeconds);

        _askHumanSchema = BuildAskHumanSchema();

        _readLoreSchema = BuildReadLoreSchema();

        _scribeLoreSchema = BuildScribeLoreSchema();

        _deleteLoreSchema = BuildDeleteLoreSchema();

        _searchArchivesSchema = BuildSearchArchivesSchema();

        _adjustInitiativeSchema = BuildAdjustInitiativeSchema();

        _useCommlinkSchema = BuildUseCommlinkSchema();

        _petitionDungeonMasterSchema = BuildPetitionDungeonMasterSchema();

        _castSendingSchema = BuildCastSendingSchema();

        _executeCommandToolDescription =
            $"Runs a command without a shell (stdout/stderr captured, {_executeCommandTimeoutSeconds}s timeout, process tree killed on timeout or cooperative cancel). Optional workingDirectory is relative to the workspace root.";

        _toolHandlers = BuildToolHandlerRegistry();
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

        if (McpSecurityLimits.ExceedsMaxLineUtf8Bytes(line, _maxJsonRpcLineBytes))
        {

            _logger?.LogWarning(
                "Arcanum internal MCP server rejected an inbound JSON-RPC line exceeding {MaxBytes} UTF-8 bytes.",
                _maxJsonRpcLineBytes);

            JsonRpcResponse error = new()
            {
                Id = NullId,

                Error = new JsonRpcError
                {
                    Code = -32600,

                    Message = "Request line exceeds maximum UTF-8 byte budget.",

                    Data = null,
                },

                Result = null,
            };

            string wire = JsonSerializer.Serialize(error, _json.JsonRpcResponse);

            await _toClient.WriteAsync(wire + "\n", cancellationToken).ConfigureAwait(false);

            return;

        }

        using JsonDocument doc = JsonDocument.Parse(line, McpSecurityLimits.JsonDocumentOptions);

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
        List<McpToolDefinitionWire> tools =
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
                Name = "adjust_initiative",
                Description =
                    "Dynamically adjusts the polling interval (in minutes) for a background Unseen Servant job based on current conditions.",
                InputSchema = _adjustInitiativeSchema,
            },
            new McpToolDefinitionWire
            {
                Name = "use_commlink",
                Description =
                    "Sends a high-priority Comm Link alert to the operator (e.g. configured webhook). Use when immediate human attention is required.",
                InputSchema = _useCommlinkSchema,
            },
            new McpToolDefinitionWire
            {
                Name = "petition_dungeon_master",
                Description =
                    "Petition the Dungeon Master (human operator) when the Apprentice is stuck on an unresolvable path. Pauses escalation and alerts the operator via Comm Link.",
                InputSchema = _petitionDungeonMasterSchema,
            },
            new McpToolDefinitionWire
            {
                Name = "ask_human",
                Description =
                    "Ask the human operator a question and wait for their answer. Use a new random UUID for promptId on every call.",
                InputSchema = _askHumanSchema,
            },
        ];

        if (_conclaveEnabled)
        {
            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "cast_sending",
                    Description =
                        "Conclave delegation: cast a Sending to spawn a new child Apprentice that pursues a delegated sub-task outside your immediate spell. Returns the new child Apprentice id.",
                    InputSchema = _castSendingSchema,
                });
        }

        if (_settings.EnableLoreSystem)
        {
            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "read_lore",
                    Description =
                        "Reads a persistent key-value fact from the Grimoire (MageSettings). Use before answering when recalling operator context or project state.",
                    InputSchema = _readLoreSchema,
                });

            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "scribe_lore",
                    Description =
                        "Writes or updates a compressed factual summary in the Grimoire under a descriptive key for cross-session recall.",
                    InputSchema = _scribeLoreSchema,
                });

            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "delete_lore",
                    Description = "Removes a lore key from the Grimoire when the operator asks to forget or the fact is obsolete.",
                    InputSchema = _deleteLoreSchema,
                });
        }

        if (_settings.EnableArchiveSearch)
        {
            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "search_archives",
                    Description =
                        "Search the Grimoire conversation history using keyword matching to recall past decisions or context.",
                    InputSchema = _searchArchivesSchema,
                });
        }

        McpToolsListResultWire body = new() { Tools = tools.ToArray() };

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
            _logger?.LogError(ex, "tools/call params deserialization failed.");

            return BuildToolsCallResponse(rpcId, ToolError("tools/call params were not valid JSON."));
        }

        if (call is null || string.IsNullOrWhiteSpace(call.Name))
        {
            return BuildToolsCallResponse(rpcId, ToolError("tools/call params missing required 'name'."));
        }

        if ((call.Name is "read_lore" or "scribe_lore" or "delete_lore") && !_settings.EnableLoreSystem)
        {
            return BuildToolsCallResponse(rpcId, ToolError("The Lore system is disabled in configuration."));
        }

        if (call.Name == "search_archives" && !_settings.EnableArchiveSearch)
        {
            return BuildToolsCallResponse(rpcId, ToolError("Archive search is disabled in configuration."));
        }

        if (call.Name == "cast_sending" && !_conclaveEnabled)
        {
            return BuildToolsCallResponse(rpcId, ToolError("The Conclave is disabled; cross-Apprentice delegation is not available."));
        }

        using CancellationTokenSource toolScope = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _requestCancellationBroker.GetTokenOrFallback(McpClient.NormalizeRpcId(rpcId), cancellationToken));

        CancellationToken toolToken = toolScope.Token;

        if (!_toolHandlers.TryGetValue(call.Name, out InternalToolHandler? handler))
        {

            return BuildToolsCallResponse(rpcId, ToolError($"Unknown tool: {call.Name}"));

        }

        McpToolsCallResultWire result = await handler(call.Arguments, toolToken).ConfigureAwait(false);

        return BuildToolsCallResponse(rpcId, result);
    }

    private JsonRpcResponse BuildToolsCallResponse(JsonElement rpcId, McpToolsCallResultWire result)
    {
        result = EnforceInProcessToolOutputCap(result);

        JsonElement element = JsonSerializer.SerializeToElement(result, _json.McpToolsCallResultWire);

        return new JsonRpcResponse { Id = rpcId, Result = element, Error = null };
    }

    private McpToolsCallResultWire EnforceInProcessToolOutputCap(McpToolsCallResultWire result)
    {

        if (result.IsError || result.Content is not { Length: > 0 })
        {

            return result;

        }

        long effectiveCap = ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
            _settings.ToolOutputCapBytes,
            _maxJsonRpcLineBytes);

        foreach (McpToolContentTextWire textItem in result.Content)
        {

            if (string.IsNullOrEmpty(textItem.Text))
            {

                continue;

            }

            long byteCount = Encoding.UTF8.GetByteCount(textItem.Text);

            if (byteCount > effectiveCap)
            {

                return ToolError(
                    "Tool output too large. Narrow the request range or parameters and retry.");

            }

        }

        return result;

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
}
