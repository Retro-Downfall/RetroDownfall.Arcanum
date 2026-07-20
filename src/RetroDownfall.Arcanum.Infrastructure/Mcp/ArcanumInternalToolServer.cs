using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
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

    private readonly JsonElement _scribeLexiconSchema;

    private readonly JsonElement _deleteLexiconSchema;

    private readonly JsonElement _searchArchivesSchema;

    private readonly JsonElement _readSagaSchema;

    private readonly JsonElement _attachSessionFileSchema;

    private readonly JsonElement _adjustInitiativeSchema;

    private readonly JsonElement _useCommlinkSchema;

    private readonly JsonElement _petitionDungeonMasterSchema;

    private readonly JsonElement _castSendingSchema;

    private readonly JsonElement _dispatchSendingSchema;

    private readonly IntelligenceSettings _settings;

    private readonly long _maxFileReadSizeBytes;

    private readonly bool _conclaveEnabled;

    private readonly bool _sagaEnabled;

    private readonly bool _attachmentsToolEnabled;

    private readonly bool _a2aClientEnabled;

    /// <summary>
    /// In-flight <c>tools/call</c> request ids to their linked <see cref="CancellationTokenSource"/>, so
    /// an inbound <c>notifications/cancelled</c> can cancel cooperative tool work (e.g. <c>execute_command</c>)
    /// without waiting for the transport itself to tear down. Replaces the pre-SDK-migration
    /// <c>McpRequestCancellationBroker</c>, which correlated ids on the client side before the request was
    /// written; the SDK client now dispatches <c>notifications/cancelled</c> itself, so this server reads it
    /// directly off the wire.
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlightToolCalls = new(StringComparer.Ordinal);

    private readonly int _maxJsonRpcLineBytes;

    private readonly string _executeCommandToolDescription;

    private readonly string _ambientConnectionKey;

    /// <summary>Per-connection key shared with the paired client transport for ambient session binding.</summary>
    internal string AmbientConnectionKey => _ambientConnectionKey;

    /// <summary>Serializes concurrent JSON-RPC response writes onto <see cref="_toClient"/>.</summary>
    private readonly SemaphoreSlim _responseWriteLock = new(1, 1);

    /// <summary>
    /// Test seam: when set, <see cref="HandleLineAsync"/> throws after a request id is known so
    /// <see cref="HandleLineSafelyAsync"/> can be asserted for sanitized error responses.
    /// </summary>
    internal Func<string, Exception?>? LineHandlerFaultForTesting { get; set; }

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
        bool sagaEnabled,
        bool a2aClientEnabled,
        bool attachmentsToolEnabled,
        int maxJsonRpcLineBytes,
        string? ambientConnectionKey = null,
        ILogger<ArcanumInternalToolServer>? logger = null,
        McpJsonSerializerContext? jsonContext = null)
    {
        ArgumentNullException.ThrowIfNull(fromClient);

        ArgumentNullException.ThrowIfNull(toClient);

        ArgumentNullException.ThrowIfNull(humanPromptRegistry);

        ArgumentNullException.ThrowIfNull(scopeFactory);

        ArgumentNullException.ThrowIfNull(pacer);

        ArgumentNullException.ThrowIfNull(intelligenceSettings);

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

        _sagaEnabled = sagaEnabled;

        _attachmentsToolEnabled = attachmentsToolEnabled;

        _a2aClientEnabled = a2aClientEnabled;

        _maxJsonRpcLineBytes = maxJsonRpcLineBytes;

        _ambientConnectionKey = string.IsNullOrWhiteSpace(ambientConnectionKey)
            ? Guid.NewGuid().ToString("N")
            : ambientConnectionKey;

        _readFileChunkSchema = BuildReadFileChunkSchema();

        _replaceTextBlockSchema = BuildReplaceTextBlockSchema();

        _writeFileSchema = BuildWriteFileSchema();

        _listDirectorySchema = BuildListDirectorySchema();

        _executeCommandSchema = BuildExecuteCommandSchema(_executeCommandTimeoutSeconds);

        _askHumanSchema = BuildAskHumanSchema();

        _scribeLexiconSchema = BuildScribeLexiconSchema();

        _deleteLexiconSchema = BuildDeleteLexiconSchema();

        _searchArchivesSchema = BuildSearchArchivesSchema();

        _readSagaSchema = BuildReadSagaSchema();

        _attachSessionFileSchema = BuildAttachSessionFileSchema();

        _adjustInitiativeSchema = BuildAdjustInitiativeSchema();

        _useCommlinkSchema = BuildUseCommlinkSchema();

        _petitionDungeonMasterSchema = BuildPetitionDungeonMasterSchema();

        _castSendingSchema = BuildCastSendingSchema();

        _dispatchSendingSchema = BuildDispatchSendingSchema();

        _executeCommandToolDescription =
            $"Runs a command without a shell (stdout/stderr captured, {_executeCommandTimeoutSeconds}s timeout, process tree killed on timeout or cooperative cancel). Optional workingDirectory is relative to the workspace root.";

        _toolHandlers = BuildToolHandlerRegistry();
    }

    /// <summary>
    /// In-flight per-line handler tasks, dispatched (not awaited) by <see cref="RunAsync"/> so a
    /// long-running <c>tools/call</c> never blocks the read loop from consuming the next line —
    /// critically, an inbound <c>notifications/cancelled</c> for that same in-flight call must be
    /// read and processed while the call is still running, which a sequential await-per-line loop
    /// could never do (the loop itself would be blocked awaiting the very call the notification is
    /// meant to cancel). Self-removing on completion; awaited (briefly) on shutdown so responses
    /// in flight get a chance to be written before the outbound channel is completed.
    /// </summary>
    private readonly ConcurrentDictionary<Task, byte> _inFlightLineTasks = new();

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

                Task lineTask = Task.Run(() => HandleLineSafelyAsync(line, cancellationToken), CancellationToken.None);

                _inFlightLineTasks[lineTask] = 0;

                _ = lineTask.ContinueWith(
                    static (completed, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(completed, out _),
                    _inFlightLineTasks,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            Task[] pending = _inFlightLineTasks.Keys.ToArray();

            if (pending.Length > 0)
            {
                using CancellationTokenSource drainTimeout = new(TimeSpan.FromSeconds(5));

                try
                {
                    await Task.WhenAll(pending).WaitAsync(drainTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Best-effort drain; any handler still running past the grace period will have
                    // its response write fail harmlessly once _toClient is completed below.
                }
            }

            _toClient.TryComplete();
        }
    }

    private async Task HandleLineSafelyAsync(string line, CancellationToken cancellationToken)
    {
        try
        {
            await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Server shutting down; nothing left to respond to.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Arcanum internal MCP server failed handling one line.");

            if (TryExtractJsonRpcRequestId(line, out JsonElement requestId))
            {
                await WriteSanitizedInternalErrorAsync(requestId, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken cancellationToken)
    {

        if (McpSecurityLimits.ExceedsMaxLineUtf8Bytes(line, _maxJsonRpcLineBytes))
        {

            _logger?.LogWarning(
                "Arcanum internal MCP server rejected an inbound JSON-RPC line exceeding {MaxBytes} UTF-8 bytes.",
                _maxJsonRpcLineBytes);

            if (TryExtractJsonRpcRequestId(line, out JsonElement oversizedId))
            {

                JsonRpcResponse error = new()
                {
                    Id = oversizedId,

                    Error = new JsonRpcError
                    {
                        Code = -32600,

                        Message = "Request line exceeds maximum UTF-8 byte budget.",

                        Data = null,
                    },

                    Result = null,
                };

                await WriteResponseAsync(error, cancellationToken).ConfigureAwait(false);

            }

            return;

        }

        if (LineHandlerFaultForTesting is not null)
        {
            Exception? fault = LineHandlerFaultForTesting(line);

            if (fault is not null)
            {
                throw fault;
            }
        }

        using JsonDocument doc = JsonDocument.Parse(line, McpSecurityLimits.JsonDocumentOptions);

        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("method", out JsonElement methodProp) || methodProp.ValueKind != JsonValueKind.String)
        {
            return;
        }

        if (!root.TryGetProperty("id", out _))
        {
            // A method with no "id" is a notification. The only one this server acts on is the
            // SDK client's wire-level cancellation signal for an in-flight tools/call.
            if (string.Equals(methodProp.GetString(), "notifications/cancelled", StringComparison.Ordinal))
            {
                HandleCancelledNotification(root);
            }

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

            await WriteResponseAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Arcanum internal MCP error building response for method {Method}.", request.Method);

            await WriteSanitizedInternalErrorAsync(rpcId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteSanitizedInternalErrorAsync(JsonElement requestId, CancellationToken cancellationToken)
    {

        JsonRpcResponse err = new()
        {
            Id = requestId,
            Error = new JsonRpcError
            {
                Code = -32603,
                Message = "Internal error.",
                Data = null,
            },
            Result = null,
        };

        await WriteResponseAsync(err, cancellationToken).ConfigureAwait(false);

    }

    private async Task WriteResponseAsync(JsonRpcResponse response, CancellationToken cancellationToken)
    {

        string wire = JsonSerializer.Serialize(response, _json.JsonRpcResponse);

        await _responseWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            await _toClient.WriteAsync(wire + "\n", cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _responseWriteLock.Release();

        }

    }

    private static bool TryExtractJsonRpcRequestId(string line, out JsonElement requestId)
    {

        requestId = default;

        try
        {

            using JsonDocument doc = JsonDocument.Parse(line, McpSecurityLimits.JsonDocumentOptions);

            if (!doc.RootElement.TryGetProperty("id", out JsonElement idProp))
            {

                return false;

            }

            if (idProp.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {

                return false;

            }

            requestId = idProp.Clone();

            return true;

        }
        catch (JsonException)
        {

            return false;

        }

    }

    /// <summary>
    /// Handles an inbound <c>notifications/cancelled</c> line: resolves <c>params.requestId</c> to the
    /// matching in-flight <c>tools/call</c> and cancels its linked token. Malformed or unmatched
    /// notifications are silently ignored (best-effort, matching the notification's fire-and-forget contract).
    /// </summary>
    private void HandleCancelledNotification(JsonElement root)
    {
        if (!root.TryGetProperty("params", out JsonElement cancelParams)
            || cancelParams.ValueKind != JsonValueKind.Object
            || !cancelParams.TryGetProperty("requestId", out JsonElement requestIdEl))
        {
            return;
        }

        string requestKey = NormalizeRequestId(requestIdEl);

        if (!_inFlightToolCalls.TryGetValue(requestKey, out CancellationTokenSource? cts))
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The tool call already completed and disposed its CancellationTokenSource
            // concurrently with this notification; nothing left to cancel.
        }
    }

    /// <summary>
    /// Normalizes a JSON-RPC id (string or number) to a stable dictionary key, matching the id shape a
    /// client's <c>tools/call</c> request and its later <c>notifications/cancelled.params.requestId</c>
    /// both use for the same in-flight request.
    /// </summary>
    private static string NormalizeRequestId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => id.GetString() ?? id.GetRawText(),
        _ => id.GetRawText(),
    };

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

        if (_a2aClientEnabled)
        {
            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "dispatch_sending",
                    Description =
                        "Archmage Client: dispatch a Sending to an external A2A-compatible agent at agent_url and block until it responds. Returns the remote agent's response text.",
                    InputSchema = _dispatchSendingSchema,
                });
        }

        if (_settings.EnableLexiconSystem)
        {
            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "scribe_lexicon",
                    Description =
                        "Records structured agent memory: creates or updates a named Lexicon entity (Name + Type + Facts). Appends non-duplicate facts to an existing entity matched case-insensitively by name. Use to remember durable facts about people, projects, APIs, or daemon state.",
                    InputSchema = _scribeLexiconSchema,
                });

            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "delete_lexicon",
                    Description = "Removes a Lexicon entity by name when a memory is obsolete or the operator asks to forget it.",
                    InputSchema = _deleteLexiconSchema,
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

        if (_sagaEnabled)
        {
            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "read_saga",
                    Description =
                        "Semantically search Saga (Arcanum's long-term associative memory) for durable facts, decisions, and preferences extracted from past sessions. Read-only — there is no scribe_saga or delete_saga tool; Saga memories are auto-extracted and operator-deletable only.",
                    InputSchema = _readSagaSchema,
                });
        }

        if (_attachmentsToolEnabled)
        {
            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "attach_session_file",
                    Description =
                        "Re-attach a bound session attachment (text or image) into the next model turn by logical name. Uses the current session only; optional version selects a specific revision (latest when omitted). Content is injected multimodally after the tool result — do not expect image bytes in the tool text.",
                    InputSchema = _attachSessionFileSchema,
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

        if ((call.Name is "scribe_lexicon" or "delete_lexicon") && !_settings.EnableLexiconSystem)
        {
            return BuildToolsCallResponse(rpcId, ToolError("The Lexicon system is disabled in configuration."));
        }

        if (call.Name == "search_archives" && !_settings.EnableArchiveSearch)
        {
            return BuildToolsCallResponse(rpcId, ToolError("Archive search is disabled in configuration."));
        }

        if (call.Name == "read_saga" && !_sagaEnabled)
        {
            return BuildToolsCallResponse(rpcId, ToolError("Saga is disabled in configuration."));
        }

        if (call.Name == "attach_session_file" && !_attachmentsToolEnabled)
        {
            return BuildToolsCallResponse(rpcId, ToolError("Session attachment model tool is disabled in configuration."));
        }

        if (call.Name == "cast_sending" && !_conclaveEnabled)
        {
            return BuildToolsCallResponse(rpcId, ToolError("The Conclave is disabled; cross-Apprentice delegation is not available."));
        }

        if (call.Name == "dispatch_sending" && !_a2aClientEnabled)
        {
            return BuildToolsCallResponse(rpcId, ToolError("A2A is disabled; dispatch_sending is not available."));
        }

        string requestKey = NormalizeRequestId(rpcId);

        using CancellationTokenSource toolScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _inFlightToolCalls[requestKey] = toolScope;

        Guid? previousAmbient = SessionAttachmentToolAmbient.CurrentSessionId;

        try
        {
            JsonElement toolArguments = call.Arguments;

            Guid? resolvedSession = ResolveAmbientSessionForToolsCall(requestKey, ref toolArguments);

            SessionAttachmentToolAmbient.CurrentSessionId = resolvedSession;

            if (!_toolHandlers.TryGetValue(call.Name, out InternalToolHandler? handler))
            {

                return BuildToolsCallResponse(rpcId, ToolError($"Unknown tool: {call.Name}"));

            }

            McpToolsCallResultWire result = await handler(toolArguments, toolScope.Token).ConfigureAwait(false);

            return BuildToolsCallResponse(rpcId, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // toolScope was cancelled by an inbound notifications/cancelled for THIS request
            // (HandleCancelledNotification), not by a server-wide shutdown (which is signaled by
            // cancellationToken itself, checked above). Return a normal tool-error response so
            // RunAsync's read loop keeps processing subsequent lines instead of this
            // OperationCanceledException propagating out and ending the whole in-process server.
            return BuildToolsCallResponse(rpcId, ToolError("Tool call was cancelled."));
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = previousAmbient;

            SessionAttachmentToolAmbient.UnbindRequest(_ambientConnectionKey, requestKey);

            _inFlightToolCalls.TryRemove(requestKey, out _);
        }
    }

    /// <summary>
    /// Resolves the inference session for this tools/call via request-id map (preferred) or host
    /// opaque token (fallback). Strips the opaque token from <paramref name="arguments"/> before
    /// tool logic so it is never visible to handlers.
    /// </summary>
    private Guid? ResolveAmbientSessionForToolsCall(string requestKey, ref JsonElement arguments)
    {

        Guid? session = null;

        if (SessionAttachmentToolAmbient.TryResolveRequest(_ambientConnectionKey, requestKey, out Guid byRequest))
        {
            session = byRequest;
        }

        arguments = StripOpaqueInvocationToken(arguments, out string? opaqueToken);

        if (opaqueToken is not null)
        {
            if (session is null
                && SessionAttachmentToolAmbient.TryTakeOpaqueToken(opaqueToken, out Guid byToken))
            {
                session = byToken;
            }
            else
            {
                // Model-supplied or already-resolved junk — never persist; drop the binding if any.
                SessionAttachmentToolAmbient.ForgetOpaqueToken(opaqueToken);
            }
        }

        return session;

    }

    private static JsonElement StripOpaqueInvocationToken(JsonElement arguments, out string? opaqueToken)
    {

        opaqueToken = null;

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return arguments;
        }

        if (!arguments.TryGetProperty(
                SessionAttachmentToolAmbient.OpaqueInvocationTokenArgumentName,
                out JsonElement tokenElement))
        {
            return arguments;
        }

        opaqueToken = tokenElement.ValueKind == JsonValueKind.String
            ? tokenElement.GetString()
            : tokenElement.ToString();

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            foreach (JsonProperty property in arguments.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        SessionAttachmentToolAmbient.OpaqueInvocationTokenArgumentName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());

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
