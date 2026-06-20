using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Security;

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

        if (McpSecurityLimits.ExceedsMaxLineUtf8Bytes(line))
        {

            _logger?.LogWarning(
                "Arcanum internal MCP server rejected an inbound JSON-RPC line exceeding {MaxBytes} UTF-8 bytes.",
                McpSecurityLimits.MaxJsonRpcLineUtf8Bytes);

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

        McpToolsCallResultWire result = call.Name switch
        {
            "read_file_chunk" => await ExecuteReadFileChunkAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "replace_text_block" => await ExecuteReplaceTextBlockAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "write_file" => await ExecuteWriteFileAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "list_directory" => await ExecuteListDirectoryAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "execute_command" => await ExecuteCommandAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "adjust_initiative" => await ExecuteAdjustInitiativeAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "use_commlink" => await ExecuteUseCommlinkAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "petition_dungeon_master" => await ExecutePetitionDungeonMasterAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "cast_sending" => await ExecuteCastSendingAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "ask_human" => await ExecuteAskHumanAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "read_lore" => await ExecuteReadLoreAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "scribe_lore" => await ExecuteScribeLoreAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "delete_lore" => await ExecuteDeleteLoreAsync(call.Arguments, toolToken).ConfigureAwait(false),
            "search_archives" => await ExecuteSearchArchivesAsync(call.Arguments, toolToken).ConfigureAwait(false),
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
            _logger?.LogError(ex, "ask_human argument deserialization failed.");

            return ToolError("Invalid arguments for ask_human.");
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
            _logger?.LogError(ex, "ask_human registration failed.");

            return ToolError("ask_human: an internal error occurred.");
        }
    }

    private Task<McpToolsCallResultWire> ExecuteAdjustInitiativeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        AdjustInitiativeArgs? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.AdjustInitiativeArgs);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "adjust_initiative argument deserialization failed.");

            return Task.FromResult(ToolError("Invalid arguments for adjust_initiative."));
        }

        if (args is null || string.IsNullOrWhiteSpace(args.JobName))
        {
            return Task.FromResult(ToolError("adjust_initiative requires a non-empty 'job_name'."));
        }

        string jobName = args.JobName.Trim();

        int clamped = ArcanumSettingClamps.UnseenServantIntervalMinutes(args.IntervalMinutes);

        _pacer.SetDynamicInterval(jobName, args.IntervalMinutes);

        string text =
            $"Unseen Servant job '{jobName}' polling interval set to {clamped} minutes (clamped to allowed range).";

        return Task.FromResult(
            new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = text },
                ],
                IsError = false,
            });
    }

    private async Task<McpToolsCallResultWire> ExecuteUseCommlinkAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        UseCommlinkParams? args;

        try
        {

            args = JsonSerializer.Deserialize(arguments, _json.UseCommlinkParams);

        }
        catch (JsonException ex)
        {

            _logger?.LogError(ex, "use_commlink argument deserialization failed.");

            return ToolError("Invalid arguments for use_commlink.");

        }

        if (args is null
            || string.IsNullOrWhiteSpace(args.Title)
            || string.IsNullOrWhiteSpace(args.Body)
            || string.IsNullOrWhiteSpace(args.Severity))
        {

            return ToolError("use_commlink requires non-empty 'title', 'body', and 'severity'.");

        }

        if (!Enum.TryParse(args.Severity.Trim(), ignoreCase: true, out CommLinkSeverity severity))
        {

            severity = CommLinkSeverity.Info;

        }

        string source = string.IsNullOrWhiteSpace(args.Source) ? "use_commlink" : args.Source.Trim();

        CommLinkMessage message = new(args.Title.Trim(), args.Body.Trim(), severity, source);

        try
        {

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            ICommLinkDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<ICommLinkDispatcher>();

            Result r = await dispatcher
                .DispatchAsync(message, cancellationToken)
                .ConfigureAwait(false);

            if (r.IsFailure)
            {

                return ToolError($"use_commlink failed: {r.Error.Message}");

            }

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = "Comm Link alert dispatched successfully." },
                ],
                IsError = false,
            };

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            _logger?.LogError(ex, "use_commlink dispatch failed.");

            return ToolError("An internal error occurred during use_commlink.");

        }

    }

    private async Task<McpToolsCallResultWire> ExecutePetitionDungeonMasterAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        PetitionDungeonMasterParams? args;

        try
        {

            args = JsonSerializer.Deserialize(arguments, _json.PetitionDungeonMasterParams);

        }
        catch (JsonException ex)
        {

            _logger?.LogError(ex, "petition_dungeon_master argument deserialization failed.");

            return ToolError("Invalid arguments for petition_dungeon_master.");

        }

        if (args is null || string.IsNullOrWhiteSpace(args.Reason))
        {

            return ToolError("petition_dungeon_master requires a non-empty 'reason'.");

        }

        string reason = args.Reason.Trim();

        string source = string.IsNullOrWhiteSpace(args.Source) ? "petition_dungeon_master" : args.Source.Trim();

        CommLinkMessage message = new(
            "Apprentice petitions the Dungeon Master",
            reason,
            CommLinkSeverity.Critical,
            source);

        try
        {

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            ICommLinkDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<ICommLinkDispatcher>();

            Result r = await dispatcher
                .DispatchAsync(message, cancellationToken)
                .ConfigureAwait(false);

            if (r.IsFailure)
            {

                return ToolError($"petition_dungeon_master failed: {r.Error.Message}");

            }

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire
                    {
                        Text = "Petition sent to the Dungeon Master. The Apprentice awaits Divine Intervention.",
                    },
                ],
                IsError = false,
            };

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            _logger?.LogError(ex, "petition_dungeon_master dispatch failed.");

            return ToolError("An internal error occurred during petition_dungeon_master.");

        }

    }

    private async Task<McpToolsCallResultWire> ExecuteCastSendingAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        if (!_conclaveEnabled)
        {
            return ToolError("The Conclave is disabled; cross-Apprentice delegation is not available.");
        }

        if (string.IsNullOrWhiteSpace(_workspaceRoot))
        {
            return ToolError(WorkspaceNotConfiguredMessage);
        }

        CastSendingParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.CastSendingParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "cast_sending argument deserialization failed.");

            return ToolError("Invalid arguments for cast_sending.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Goal))
        {
            return ToolError("cast_sending requires a non-empty 'goal'.");
        }

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IConclaveArchmage archmage = scope.ServiceProvider.GetRequiredService<IConclaveArchmage>();

            Result<Apprentice> result = await archmage
                .CastAsync(
                    new ConclaveCastRequest(args.Goal.Trim(), args.Name, _workspaceRoot!),
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {
                return ToolError($"cast_sending failed: {result.Error.Message}");
            }

            CastSendingResultWire payload = new() { ChildApprenticeId = result.Value!.Id };

            string json = JsonSerializer.Serialize(payload, _json.CastSendingResultWire);

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = json },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "cast_sending failed.");

            return ToolError("An internal error occurred during cast_sending.");
        }
    }

    private async Task<McpToolsCallResultWire> ExecuteReadLoreAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        ReadLoreParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ReadLoreParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "read_lore argument deserialization failed.");

            return ToolError("Invalid arguments for read_lore.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Key))
        {
            return ToolError("read_lore requires a non-empty 'key'.");
        }

        string key = args.Key.Trim();

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IGrimoireRepository repo = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

            string? value = await repo.ReadLoreAsync(key, cancellationToken).ConfigureAwait(false);

            string text = value is null ? "Key not found." : value;

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = text },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "read_lore failed for key {Key}.", key);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

    private async Task<McpToolsCallResultWire> ExecuteScribeLoreAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        ScribeLoreParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ScribeLoreParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "scribe_lore argument deserialization failed.");

            return ToolError("Invalid arguments for scribe_lore.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Key) || string.IsNullOrWhiteSpace(args.Value))
        {
            return ToolError("scribe_lore requires non-empty 'key' and 'value'.");
        }

        string key = args.Key.Trim();

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IGrimoireRepository repo = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

            await repo.ScribeLoreAsync(key, args.Value, cancellationToken).ConfigureAwait(false);

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = $"Lore saved for key '{key}'." },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "scribe_lore failed for key {Key}.", key);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

    private async Task<McpToolsCallResultWire> ExecuteDeleteLoreAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        DeleteLoreParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.DeleteLoreParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "delete_lore argument deserialization failed.");

            return ToolError("Invalid arguments for delete_lore.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Key))
        {
            return ToolError("delete_lore requires a non-empty 'key'.");
        }

        string key = args.Key.Trim();

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IGrimoireRepository repo = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

            bool removed = await repo.DeleteLoreAsync(key, cancellationToken).ConfigureAwait(false);

            string text = removed
                ? $"Key '{key}' was removed from lore."
                : $"Key '{key}' did not exist; nothing was deleted.";

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = text },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "delete_lore failed for key {Key}.", key);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

    private async Task<McpToolsCallResultWire> ExecuteSearchArchivesAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        SearchArchivesParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.SearchArchivesParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "search_archives argument deserialization failed.");

            return ToolError("Invalid arguments for search_archives.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Query))
        {
            return ToolError("search_archives requires a non-empty 'query'.");
        }

        string query = args.Query.Trim();

        int maxQueryLen = ArcanumSettingClamps.ArchiveSearchMaxQueryLength(_settings.ArchiveSearchMaxQueryLength);

        if (query.Length > maxQueryLen)
        {
            query = query[..maxQueryLen];
        }

        int maxResults = ArcanumSettingClamps.ArchiveSearchMaxResults(_settings.ArchiveSearchMaxResults);

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IGrimoireRepository repo = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

            string text = await repo
                .SearchArchivesAsync(query, maxResults, cancellationToken)
                .ConfigureAwait(false);

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = text },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "search_archives failed for query {Query}.", query);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

    private JsonRpcResponse BuildToolsCallResponse(JsonElement rpcId, McpToolsCallResultWire result)
    {
        JsonElement element = JsonSerializer.SerializeToElement(result, _json.McpToolsCallResultWire);

        return new JsonRpcResponse { Id = rpcId, Result = element, Error = null };
    }

    private async Task<ResourceLimits> ResolveResourceLimitsAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        ISanctumGuard sanctumGuard = scope.ServiceProvider.GetRequiredService<ISanctumGuard>();

        return await sanctumGuard
            .GetEffectiveResourceLimitsForWorkspaceAsync(_workspaceRoot, cancellationToken)
            .ConfigureAwait(false);
    }

    private McpToolsCallResultWire? TryRejectIfFileExceedsReadLimit(string absolutePath, string toolName)
    {
        long maxBytes = _maxFileReadSizeBytes;

        try
        {
            long length = new FileInfo(absolutePath).Length;

            if (length > maxBytes)
            {
                return ToolError(
                    $"{toolName}: file size ({length} bytes) exceeds the maximum read limit ({maxBytes} bytes).");
            }
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "{ToolName}: could not inspect file size.", toolName);

            return ToolError($"{toolName}: could not inspect the file size.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "{ToolName}: access denied inspecting file size.", toolName);

            return ToolError($"{toolName}: access denied.");
        }

        return null;
    }

    private static McpToolsCallResultWire? TryRejectIfWriteExceedsLimit(
        string content,
        int maxFileWriteMb,
        string toolName)
    {
        long maxBytes = (long)maxFileWriteMb * 1024L * 1024L;

        long byteCount = Encoding.UTF8.GetByteCount(content);

        if (byteCount <= maxBytes)
        {
            return null;
        }

        return ToolError(
            $"{toolName}: write size ({byteCount} bytes) exceeds the Sanctum MaxFileWriteMb limit ({maxFileWriteMb} MiB).");
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
            _logger?.LogError(ex, "Path resolution failed for relative path.");

            error = ToolError("Could not resolve the specified relative path.");

            return false;
        }

        if (!ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(root, resolved, out _))
        {
            error = ToolError(PathEscapesSandboxMessage);

            return false;
        }

        absolutePath = resolved;

        return true;
    }

    private bool TryRevalidateBeforeIo(
        string absolutePath,
        out McpToolsCallResultWire? error)
    {
        error = null;

        if (!ToolHelpers.RevalidatePathBeforeIo(_workspaceRoot!, absolutePath))
        {
            error = ToolError(PathEscapesSandboxMessage);

            return false;
        }

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
            _logger?.LogError(ex, "read_file_chunk: file not found.");

            return ToolError("read_file_chunk: the specified file was not found.");
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger?.LogError(ex, "read_file_chunk: directory not found.");

            return ToolError("read_file_chunk: the specified directory was not found.");
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

        string content;

        try
        {
            content = await File.ReadAllTextAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            _logger?.LogError(ex, "replace_text_block: file not found.");

            return ToolError("replace_text_block: the specified file was not found.");
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger?.LogError(ex, "replace_text_block: directory not found.");

            return ToolError("replace_text_block: the specified directory was not found.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "replace_text_block: access denied reading.");

            return ToolError("replace_text_block: access denied.");
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "replace_text_block: I/O error reading.");

            return ToolError("replace_text_block: an I/O error occurred. See server logs.");
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

        try
        {
            await File.WriteAllTextAsync(absolutePath, updated, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "replace_text_block: access denied writing.");

            return ToolError("replace_text_block: access denied writing.");
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "replace_text_block: I/O error writing.");

            return ToolError("replace_text_block: an I/O error occurred writing. See server logs.");
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

        string? parentDir = Path.GetDirectoryName(absolutePath);

        if (!string.IsNullOrEmpty(parentDir))
        {
            try
            {
                Directory.CreateDirectory(parentDir);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger?.LogError(ex, "write_file: access denied creating directory.");

                return ToolError("write_file: access denied creating directory.");
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "write_file: I/O error creating directory.");

                return ToolError("write_file: an I/O error occurred creating directory. See server logs.");
            }
        }

        try
        {
            await File.WriteAllTextAsync(absolutePath, args.Content, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "write_file: access denied writing.");

            return ToolError("write_file: access denied.");
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "write_file: I/O error writing.");

            return ToolError("write_file: an I/O error occurred. See server logs.");
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
            _logger?.LogError(ex, "list_directory argument deserialization failed.");

            return Task.FromResult(ToolError("Invalid arguments for list_directory."));
        }

        if (args is null || string.IsNullOrWhiteSpace(args.RelativePath))
        {
            return Task.FromResult(ToolError("list_directory requires 'relativePath'."));
        }

        if (!TryResolveSandboxedPath(args.RelativePath, out string? absolutePath, out McpToolsCallResultWire? resolveErr))
        {
            return Task.FromResult(resolveErr!);
        }

        if (!TryRevalidateBeforeIo(absolutePath, out McpToolsCallResultWire? revalidateErr))
        {
            return Task.FromResult(revalidateErr!);
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
                        _logger?.LogError(ex, "list_directory: I/O error listing subdirectory.");

                        return Task.FromResult(ToolError("list_directory: an I/O error occurred. See server logs."));
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger?.LogError(ex, "list_directory: access denied listing subdirectory.");

                        return Task.FromResult(ToolError("list_directory: access denied."));
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
                    _logger?.LogError(ex, "list_directory: I/O error listing root.");

                    return Task.FromResult(ToolError("list_directory: an I/O error occurred. See server logs."));
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger?.LogError(ex, "list_directory: access denied listing root.");

                    return Task.FromResult(ToolError("list_directory: access denied."));
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
        catch (OperationCanceledException)
        {
            return Task.FromResult(ToolError("list_directory: operation was canceled."));
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "list_directory: I/O error.");

            return Task.FromResult(ToolError("list_directory: an I/O error occurred. See server logs."));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "list_directory: access denied.");

            return Task.FromResult(ToolError("list_directory: access denied."));
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
            _logger?.LogError(ex, "execute_command argument deserialization failed.");

            return ToolError("Invalid arguments for execute_command.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Command))
        {
            return ToolError("execute_command requires 'command'.");
        }

        string commandFileName = args.Command.Trim();

        IReadOnlyList<string> tokenizedArgs = ResolveCommandArgumentTokens(args.ArgumentList, args.Arguments);

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
            FileName = commandFileName,

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,

            WorkingDirectory = workingDir,
        };

        foreach (string token in tokenizedArgs)
        {
            psi.ArgumentList.Add(token);
        }

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
            _logger?.LogError(ex, "execute_command: I/O error starting process.");

            return ToolError("execute_command: failed to start the process.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "execute_command: access denied starting process.");

            return ToolError("execute_command: access denied starting the process.");
        }
        catch (OperationCanceledException)
        {
            return ToolError("execute_command: canceled before start completed.");
        }
        catch (InvalidOperationException ex)
        {
            _logger?.LogError(ex, "execute_command: could not start process.");

            return ToolError("execute_command: failed to start the process.");
        }
        catch (Win32Exception ex)
        {
            _logger?.LogError(ex, "execute_command: could not start process.");

            return ToolError("execute_command: failed to start the process.");
        }

        CancellationTokenRegistration killRegistration = waitToken.Register(
            static state => TryKillProcessEntireTree((Process)state!),
            process);

        long perStreamCapBytes = ArcanumSettingClamps.ToolOutputCapBytes(_settings.ToolOutputCapBytes) / 2L;

        if (perStreamCapBytes < 1024L)
        {
            perStreamCapBytes = 1024L;
        }

        try
        {
            Task<CappedOutput> stdoutTask = ReadStreamCappedAsync(process.StandardOutput, perStreamCapBytes, waitToken);

            Task<CappedOutput> stderrTask = ReadStreamCappedAsync(process.StandardError, perStreamCapBytes, waitToken);

            try
            {
                await process.WaitForExitAsync(waitToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessEntireTree(process);

                await ObserveStreamReadTasksAsync(stdoutTask, stderrTask).ConfigureAwait(false);

                if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return ToolError(
                        $"execute_command: the command timed out after {_executeCommandTimeoutSeconds} seconds.");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return ToolError("execute_command: canceled.");
                }

                return ToolError("execute_command: canceled or timed out.");
            }

            CappedOutput stdout;

            CappedOutput stderr;

            try
            {
                stdout = await stdoutTask.ConfigureAwait(false);

                stderr = await stderrTask.ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "execute_command: I/O error reading process output.");

                return ToolError("execute_command: failed to read process output.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger?.LogError(ex, "execute_command: access denied reading process output.");

                return ToolError("execute_command: access denied reading process output.");
            }
            catch (OperationCanceledException)
            {
                return ToolError("execute_command: canceled while reading output.");
            }

            StringBuilder text = new();

            text.AppendLine("--- stdout ---");

            text.AppendLine(stdout.Text);

            if (stdout.Truncated)
            {
                text.AppendLine($"[truncated: exceeded {perStreamCapBytes} bytes]");
            }

            text.AppendLine("--- stderr ---");

            text.AppendLine(stderr.Text);

            if (stderr.Truncated)
            {
                text.AppendLine($"[truncated: exceeded {perStreamCapBytes} bytes]");
            }

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
        finally
        {
            await killRegistration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task ObserveStreamReadTasksAsync(
        Task<CappedOutput> stdoutTask,
        Task<CappedOutput> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private readonly record struct CappedOutput(string Text, bool Truncated);

    private static async Task<CappedOutput> ReadStreamCappedAsync(
        StreamReader reader,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        StringBuilder builder = new();

        char[] buffer = new char[4096];

        long approximateBytes = 0L;

        bool truncated = false;

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {
                break;
            }

            long encodedSize = Encoding.UTF8.GetByteCount(buffer, 0, read);

            if (approximateBytes + encodedSize > maxBytes)
            {
                long remaining = maxBytes - approximateBytes;

                if (remaining > 0)
                {
                    int safeChars = ChooseSafeCharCount(buffer, read, remaining);

                    builder.Append(buffer, 0, safeChars);
                }

                truncated = true;

                break;
            }

            builder.Append(buffer, 0, read);

            approximateBytes += encodedSize;
        }

        return new CappedOutput(builder.ToString(), truncated);
    }

    private static int ChooseSafeCharCount(char[] buffer, int charCount, long remainingBytes)
    {
        long running = 0L;

        for (int i = 0; i < charCount; i++)
        {
            int charByteSize = Encoding.UTF8.GetByteCount(buffer, i, 1);

            if (running + charByteSize > remainingBytes)
            {
                return i;
            }

            running += charByteSize;
        }

        return charCount;
    }

    private static IReadOnlyList<string> ResolveCommandArgumentTokens(string[]? argumentList, string? argumentsString)
    {
        if (argumentList is { Length: > 0 })
        {
            List<string> direct = new(argumentList.Length);

            foreach (string token in argumentList)
            {
                if (token is null)
                {
                    continue;
                }

                direct.Add(token);
            }

            return direct;
        }

        if (string.IsNullOrEmpty(argumentsString))
        {
            return Array.Empty<string>();
        }

        return TokenizeArgumentsString(argumentsString);
    }

    private static IReadOnlyList<string> TokenizeArgumentsString(string line)
    {
        List<string> tokens = [];

        StringBuilder current = new();

        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;

                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());

                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
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
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
            }
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

            WriteStringProperty(
                w,
                "arguments",
                "Optional command-line arguments as a single string. Tokenized by the host (quoted substrings stay together; whitespace separates tokens). Prefer 'argumentList' when calling from a model SDK.");

            w.WriteStartObject("argumentList");

            w.WriteString("type", "array");

            w.WriteString("description", "Preferred: pre-tokenized argument list. Each entry is passed verbatim to the child process (no shell, no re-parsing).");

            w.WriteStartObject("items");

            w.WriteString("type", "string");

            w.WriteEndObject();

            w.WriteEndObject();

            WriteStringProperty(
                w,
                "workingDirectory",
                "Optional working directory relative to the workspace root. When omitted, the process runs in the workspace root. Must not be an absolute path.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("command");

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

    private static JsonElement BuildReadLoreSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "key",
                "Lore key (e.g. Architecture_State, User_Preferences). Stored in the encrypted Grimoire database.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("key");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildScribeLoreSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "key",
                "Descriptive lore key under which the fact is stored (upsert).");

            WriteStringProperty(w, "value", "Compressed factual summary to persist for later turns.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("key");

            w.WriteStringValue("value");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildDeleteLoreSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(w, "key", "Lore key to remove from the Grimoire.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("key");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildSearchArchivesSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "query",
                "Keywords or FTS5 query text to match against archived chat message content.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("query");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildAdjustInitiativeSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "job_name",
                "Unseen Servant job name as configured under Arcanum:Daemon:Jobs (the 'name' field).");

            WriteIntegerProperty(
                w,
                "interval_minutes",
                "New polling interval in minutes (clamped by the host to the allowed range).");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("job_name");

            w.WriteStringValue("interval_minutes");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);
        });
    }

    private static JsonElement BuildUseCommlinkSchema()
    {

        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "title",
                "Short alert title shown to the operator.");

            WriteStringProperty(
                w,
                "body",
                "Alert body with details the operator should read.");

            WriteStringProperty(
                w,
                "severity",
                "One of: Info, Warning, Critical (case-insensitive). Unknown values are treated as Info.");

            WriteStringProperty(
                w,
                "source",
                "Optional origin label (defaults to use_commlink).");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("title");

            w.WriteStringValue("body");

            w.WriteStringValue("severity");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);

        });

    }

    private static JsonElement BuildPetitionDungeonMasterSchema()
    {

        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "reason",
                "Clear explanation of why the Apprentice is stuck and requires Dungeon Master guidance.");

            WriteStringProperty(
                w,
                "source",
                "Optional origin label (defaults to petition_dungeon_master).");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("reason");

            w.WriteEndArray();

            w.WriteBoolean("additionalProperties", false);

        });

    }

    private static JsonElement BuildCastSendingSchema()
    {
        return BuildSchema(static w =>
        {
            w.WriteString("type", "object");

            w.WriteStartObject("properties");

            WriteStringProperty(
                w,
                "goal",
                "The goal for the new child Apprentice. Describe the delegated sub-task clearly and self-containedly.");

            WriteStringProperty(
                w,
                "name",
                "Optional display name for the child Apprentice. A themed default is used when omitted.");

            w.WriteEndObject();

            w.WriteStartArray("required");

            w.WriteStringValue("goal");

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
