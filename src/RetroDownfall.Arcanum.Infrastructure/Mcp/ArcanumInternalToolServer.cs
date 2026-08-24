using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

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

    private readonly int _listDirectoryPageSize;

    private readonly string _listDirectoryToolsListDescription;

    private readonly JsonElement _readFileChunkSchema;

    private readonly JsonElement _replaceTextBlockSchema;

    private readonly JsonElement _writeFileSchema;

    private readonly JsonElement _listDirectorySchema;

    private readonly JsonElement _searchWorkspaceSchema;

    private readonly JsonElement _applyPatchSchema;

    private readonly JsonElement _workspaceCheckSchema;

    private readonly JsonElement _executeCommandSchema;

    private readonly JsonElement _readCommandOutputSchema;

    private readonly JsonElement _askHumanSchema;

    private readonly JsonElement _scribeLexiconSchema;

    private readonly JsonElement _deleteLexiconSchema;

    private readonly JsonElement _searchArchivesSchema;

    private readonly JsonElement _proposeCovenantSchema;

    private readonly JsonElement _retireCovenantSchema;

    private readonly JsonElement _covenantMutationOutputSchema;

    private readonly JsonElement _readSagaSchema;

    private readonly JsonElement _attachSessionFileSchema;

    private readonly JsonElement _refreshSessionFileSchema;

    private readonly JsonElement _adjustInitiativeSchema;

    private readonly JsonElement _sendCommLinkAlertSchema;

    private readonly JsonElement _petitionDungeonMasterSchema;

    private readonly JsonElement _castSendingSchema;

    private readonly JsonElement _dispatchSendingSchema;

    private readonly JsonElement _continueSendingSchema;

    private readonly IntelligenceSettings _settings;

    private readonly WorkspaceSearchSettings _workspaceSearchSettings;

    private readonly WorkspacePatchSettings _workspacePatchSettings;

    private readonly WorkspaceCheckSettings _workspaceCheckSettings;

    private readonly WorkspaceCheckProfileCatalog _workspaceCheckProfiles;

    private readonly IWorkspaceCheckRuntime _workspaceCheckRuntime;

    private readonly long _maxFileReadSizeBytes;

    private readonly bool _conclaveEnabled;

    private readonly bool _sagaEnabled;

    private readonly bool _attachmentsToolEnabled;

    private readonly bool _a2aClientEnabled;

    private readonly bool _allowHostProcessTools;

    /// <summary>
    /// The live Covenant availability publisher, or <see langword="null"/> on a host that composed no
    /// Covenant tier at all. Both Covenant handlers are registered either way and refuse on their own.
    /// </summary>
    private readonly ICovenantAvailability? _covenantAvailability;

    private readonly CovenantToolCapabilityRegistry? _covenantCapabilities;

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

    private readonly CommandOutputArtifactStore _commandOutputArtifacts = new();

    internal string? CommandOutputArtifactRootForTests =>
        _commandOutputArtifacts.RootPathForTests;

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
        int listDirectoryMaxPaths,
        IntelligenceSettings intelligenceSettings,
        long maxFileReadSizeBytes,
        bool conclaveEnabled,
        bool sagaEnabled,
        bool a2aClientEnabled,
        bool attachmentsToolEnabled,
        int maxJsonRpcLineBytes,
        bool allowHostProcessTools = false,
        string? ambientConnectionKey = null,
        ILogger<ArcanumInternalToolServer>? logger = null,
        McpJsonSerializerContext? jsonContext = null,
        CodingToolsSettings? codingToolsSettings = null,
        IWorkspaceCheckRuntime? workspaceCheckRuntime = null)
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

        _listDirectoryPageSize = listDirectoryMaxPaths;

        _listDirectoryToolsListDescription =
            "Lists files and folders under a path relative to the workspace root. Optional recursion; skips node_modules, bin, obj, and .git. Large listings return a continuation cursor instead of dropping paths.";

        _settings = intelligenceSettings;

        WorkspaceSearchSettings configuredSearch =
            codingToolsSettings?.Search ?? new WorkspaceSearchSettings();

        _workspaceSearchSettings = new WorkspaceSearchSettings
        {
            MaxPatternChars =
                ArcanumSettingClamps.WorkspaceSearchMaxPatternChars(
                    configuredSearch.MaxPatternChars),
            RegexTimeoutMilliseconds =
                ArcanumSettingClamps.WorkspaceSearchRegexTimeoutMilliseconds(
                    configuredSearch.RegexTimeoutMilliseconds),
            MaxPreviewChars =
                ArcanumSettingClamps.WorkspaceSearchMaxPreviewChars(
                    configuredSearch.MaxPreviewChars),
        };

        WorkspacePatchSettings configuredPatch =
            codingToolsSettings?.Patch ?? new WorkspacePatchSettings();

        _workspacePatchSettings =
            ArcanumSettingClamps.NormalizeWorkspacePatchSettings(
                configuredPatch);

        _workspaceCheckSettings =
            codingToolsSettings?.WorkspaceCheck
            ?? new WorkspaceCheckSettings();

        _workspaceCheckProfiles = WorkspaceCheckProfileCatalog.Create(
            _workspaceCheckSettings);

        _workspaceCheckRuntime = workspaceCheckRuntime
            ?? new WorkspaceCheckRuntime(
                _workspaceCheckSettings,
                scopeFactory,
                logger);

        _maxFileReadSizeBytes = maxFileReadSizeBytes;

        _conclaveEnabled = conclaveEnabled;

        _sagaEnabled = sagaEnabled;

        _attachmentsToolEnabled = attachmentsToolEnabled;

        _a2aClientEnabled = a2aClientEnabled;

        _allowHostProcessTools = allowHostProcessTools;

        // Resolved once from a throwaway scope because both are host singletons. A host that composed
        // no Covenant tier resolves nothing and the two handlers stay inert.
        using (IServiceScope composition = scopeFactory.CreateScope())
        {

            _covenantAvailability = composition.ServiceProvider.GetService<ICovenantAvailability>();

            _covenantCapabilities = composition.ServiceProvider.GetService<CovenantToolCapabilityRegistry>();

        }

        _maxJsonRpcLineBytes = maxJsonRpcLineBytes;

        _ambientConnectionKey = string.IsNullOrWhiteSpace(ambientConnectionKey)
            ? Guid.NewGuid().ToString("N")
            : ambientConnectionKey;

        _readFileChunkSchema = BuildReadFileChunkSchema();

        _replaceTextBlockSchema = BuildReplaceTextBlockSchema();

        _writeFileSchema = BuildWriteFileSchema();

        _listDirectorySchema = BuildListDirectorySchema();

        _searchWorkspaceSchema = BuildSearchWorkspaceSchema(_workspaceSearchSettings);

        _applyPatchSchema = BuildApplyPatchSchema(_workspacePatchSettings);

        _workspaceCheckSchema = BuildWorkspaceCheckSchema(
            _workspaceCheckProfiles,
            _workspaceCheckSettings);

        _executeCommandSchema = BuildExecuteCommandSchema();

        _readCommandOutputSchema = BuildReadCommandOutputSchema(
            GetMaxCommandOutputPageBytes());

        _askHumanSchema = BuildAskHumanSchema();

        _scribeLexiconSchema = BuildScribeLexiconSchema();

        _deleteLexiconSchema = BuildDeleteLexiconSchema();

        _searchArchivesSchema = BuildSearchArchivesSchema();

        _proposeCovenantSchema = BuildProposeCovenantSchema();

        _retireCovenantSchema = BuildRetireCovenantSchema();

        _covenantMutationOutputSchema = BuildCovenantMutationOutputSchema();

        _readSagaSchema = BuildReadSagaSchema();

        _attachSessionFileSchema = BuildAttachSessionFileSchema();

        _refreshSessionFileSchema = BuildRefreshSessionFileSchema();

        _adjustInitiativeSchema = BuildAdjustInitiativeSchema();

        _sendCommLinkAlertSchema = BuildSendCommLinkAlertSchema();

        _petitionDungeonMasterSchema = BuildPetitionDungeonMasterSchema();

        _castSendingSchema = BuildCastSendingSchema();

        _dispatchSendingSchema = BuildDispatchSendingSchema();

        _continueSendingSchema = BuildContinueSendingSchema();

        _executeCommandToolDescription =
            "Runs a command without a shell (stdout/stderr previewed in memory; complete larger streams remain retrievable through read_command_output until this connection closes). Runs until completion or cooperative caller cancellation, which kills the process tree. Optional workingDirectory is relative to the workspace root.";

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
        using CancellationTokenSource connectionLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        CancellationToken connectionToken =
            connectionLifetime.Token;

        try
        {
            await foreach (string line in _fromClient
                .ReadAllAsync(connectionToken)
                .ConfigureAwait(false))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                Task lineTask = Task.Run(
                    () => HandleLineSafelyAsync(
                        line,
                        connectionToken),
                    CancellationToken.None);

                _inFlightLineTasks[lineTask] = 0;

                _ = lineTask.ContinueWith(
                    static (completed, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(completed, out _),
                    _inFlightLineTasks,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
            when (connectionToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            await connectionLifetime.CancelAsync().ConfigureAwait(false);

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

            await _commandOutputArtifacts.DisposeAsync()
                .ConfigureAwait(false);

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

    /// <summary>
    /// The delimiter appended to every response before it is handed to the channel. It is part of the
    /// element every inbound reader receives and measures, so the outbound guard has to budget for it.
    /// </summary>
    private const string ResponseLineDelimiter = "\n";

    private const int ResponseLineDelimiterUtf8Bytes = 1;

    private async Task WriteResponseAsync(JsonRpcResponse response, CancellationToken cancellationToken)
    {

        string wire = JsonSerializer.Serialize(response, _json.JsonRpcResponse);

        wire = BoundOutboundResponseLine(response, wire);

        await _responseWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            await _toClient.WriteAsync(wire + ResponseLineDelimiter, cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _responseWriteLock.Release();

        }

    }

    /// <summary>
    /// Returns <paramref name="wire"/> unchanged when it fits the frame budget, otherwise a small
    /// typed JSON-RPC error carrying the same id. Every inbound reader drops an oversized line
    /// silently, so writing the original frame would strand the request until the caller's own
    /// timeout: the upstream tool-output cap only assumes a 2x JSON escaping allowance, while the
    /// default encoder expands <c>&lt;</c>, <c>&amp;</c>, <c>"</c> and friends six-fold, so a result
    /// that legitimately cleared that cap can still overflow here. Substituting rather than throwing
    /// keeps <see cref="HandleLineSafelyAsync"/>'s own error writer — which routes through this same
    /// method — from faulting. The budget covers the payload plus its delimiter, because that whole
    /// element is what the reader measures: budgeting only the payload leaves a one-byte window in
    /// which the writer admits a frame the reader then discards.
    /// </summary>
    private string BoundOutboundResponseLine(JsonRpcResponse response, string wire)
    {

        if (!ExceedsOutboundFrameBudget(wire))
        {

            return wire;

        }

        _logger?.LogWarning(
            "Arcanum internal MCP server replaced a {ActualBytes}-byte JSON-RPC response line exceeding {MaxBytes} UTF-8 bytes with an oversized-output error.",
            Encoding.UTF8.GetByteCount(wire),
            _maxJsonRpcLineBytes);

        JsonRpcResponse oversized = new()
        {
            Id = response.Id,

            Error = new JsonRpcError
            {
                Code = -32603,

                Message = "Tool output too large for one JSON-RPC frame. Narrow the request range or parameters and retry.",

                Data = null,
            },

            Result = null,
        };

        string replacement = JsonSerializer.Serialize(oversized, _json.JsonRpcResponse);

        if (!ExceedsOutboundFrameBudget(replacement))
        {

            return replacement;

        }

        // Only reachable when the request id itself consumed nearly the whole inbound budget. Drop
        // the id rather than the frame so the peer still sees a protocol-level failure.
        JsonRpcResponse idless = oversized with
        {
            Id = JsonSerializer.SerializeToElement((string?)null, _json.String),
        };

        return JsonSerializer.Serialize(idless, _json.JsonRpcResponse);

    }

    private bool ExceedsOutboundFrameBudget(string wire) =>
        (long)Encoding.UTF8.GetByteCount(wire) + ResponseLineDelimiterUtf8Bytes > _maxJsonRpcLineBytes;

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
        List<McpToolDefinitionWire> tools = [];

        if (_workspaceRoot is not null)
        {
            tools.AddRange(
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
                    Name = ToolRiskClassifier.SearchWorkspaceToolName,
                    Description =
                        "Searches strict UTF-8 workspace files in process using exact literal or bounded line-scoped regex matching. Returns deterministic structured matches and skip/cap counters.",
                    InputSchema = _searchWorkspaceSchema,
                },
                new McpToolDefinitionWire
                {
                    Name = ToolRiskClassifier.ApplyPatchToolName,
                    Description =
                        "Applies a canonical unified diff as one reversible, sequential multi-file transaction. "
                        + "All paths and hunks are planned before mutation; dryRun performs the same validation without staging or writes.",
                    InputSchema = _applyPatchSchema,
                },
            ]);
        }

        if (_workspaceRoot is not null
            && _workspaceCheckRuntime.GetStatus(_workspaceRoot).IsEligible)
        {

            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = ToolRiskClassifier.WorkspaceCheckToolName,
                    Description =
                        "Runs a closed operator-owned .NET build, test, or lint profile. "
                        + "Workspace-authored MSBuild tasks, generators, analyzers, and tests execute as code. "
                        + "The source workspace is read-only; build/intermediate/test roots are writable outside it. "
                        + "The filesystem jail does not isolate network egress.",
                    InputSchema = _workspaceCheckSchema,
                });
        }

        if (_allowHostProcessTools && _workspaceRoot is not null)
        {
            tools.AddRange(
            [
                new McpToolDefinitionWire
                {
                    Name = "execute_command",
                    Description = _executeCommandToolDescription,
                    InputSchema = _executeCommandSchema,
                },
                new McpToolDefinitionWire
                {
                    Name = "read_command_output",
                    Description =
                        "Reads a UTF-8 page from a complete stdout or stderr artifact returned by execute_command. Use each nextOffset until complete; handles expire when this MCP connection closes.",
                    InputSchema = _readCommandOutputSchema,
                },
            ]);
        }

        tools.Add(
            new McpToolDefinitionWire
            {
                Name = "adjust_initiative",
                Description =
                    "Dynamically adjusts the polling interval (in minutes) for a background Unseen Servant job based on current conditions.",
                InputSchema = _adjustInitiativeSchema,
            });

        tools.Add(
            new McpToolDefinitionWire
            {
                Name = "send_commlink_alert",
                Description =
                    "Send a one-way external notification through the configured Comm Link. This does not wait for or receive a reply.",
                InputSchema = _sendCommLinkAlertSchema,
            });

        tools.Add(
            new McpToolDefinitionWire
            {
                Name = "petition_dungeon_master",
                Description =
                    "Escalate an autonomous task that cannot safely continue. The Apprentice pauses for later Dungeon Master intervention. Do not use for ordinary questions.",
                InputSchema = _petitionDungeonMasterSchema,
            });

        tools.Add(
            new McpToolDefinitionWire
            {
                Name = "ask_human",
                Description =
                    "Ask the active operator a question and wait for their response. Use only during attended interactive turns.",
                InputSchema = _askHumanSchema,
            });

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

            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "continue_sending",
                    Description =
                        "Answer a Sending that a remote agent parked because it needed more input or authentication, resuming the same remote task instead of re-running the goal. Use the task_id a continuable dispatch_sending returned.",
                    InputSchema = _continueSendingSchema,
                });
        }

        if (_settings.EnableLexiconSystem)
        {
            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "scribe_lexicon",
                    Description =
                        "Records structured agent memory only when authorized by the current conversation. Attaching or indexing never promotes automatically. When a fact is attachment-derived, attachment_id is required and must name content materialized in this turn; typed provenance is retained.",
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

        if (CovenantToolsAvailable())
        {
            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = CovenantToolNames.ProposeCovenant,
                    Description =
                        "Propose one standing preference the operator has expressed, so it is honored in later sessions without being restated. "
                        + "It is stored for their review and is never treated as an instruction until they confirm it. "
                        + "Use it for durable preferences about how they want you to work, not for facts about the task at hand.",
                    InputSchema = _proposeCovenantSchema,
                    OutputSchema = _covenantMutationOutputSchema,
                });

            if (CovenantRetirementAvailable)
            {
                tools.Add(
                    new McpToolDefinitionWire
                    {
                        Name = CovenantToolNames.RetireCovenant,
                        Description =
                            "Retire one standing preference that this turn actually showed you, when the operator says it no longer applies. "
                            + "It requires their approval, and it can only target content admitted into this exact model call.",
                        InputSchema = _retireCovenantSchema,
                        OutputSchema = _covenantMutationOutputSchema,
                    });
            }
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

            tools.Add(
                new McpToolDefinitionWire
                {
                    Name = "refresh_session_file",
                    Description =
                        "Securely read the verified workspace source for a turn-visible session attachment, persist or reuse its latest version, and queue that content for the next model request. Select by attachmentId or logicalKey; source paths are host-owned and cannot be supplied or overridden.",
                    InputSchema = _refreshSessionFileSchema,
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

        if (call.Name is "attach_session_file" or "refresh_session_file" && !_attachmentsToolEnabled)
        {
            return BuildToolsCallResponse(rpcId, ToolError("Session attachment model tool is disabled in configuration."));
        }

        if (call.Name == "cast_sending" && !_conclaveEnabled)
        {
            return BuildToolsCallResponse(rpcId, ToolError("The Conclave is disabled; cross-Apprentice delegation is not available."));
        }

        if (call.Name is "dispatch_sending" or "continue_sending" && !_a2aClientEnabled)
        {
            return BuildToolsCallResponse(rpcId, ToolError($"A2A is disabled; {call.Name} is not available."));
        }

        string requestKey = NormalizeRequestId(rpcId);

        using CancellationTokenSource toolScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Reject duplicate in-flight ids rather than overwriting the CTS (which would orphan the
        // first call's cancellation registration and break notifications/cancelled correlation).
        if (!_inFlightToolCalls.TryAdd(requestKey, toolScope))
        {
            // The rejection touches no shared state. Every binding store is keyed by
            // (connectionKey, requestId) alone, so anything unbound here is exactly what the call
            // that won the id is still going to resolve — it does not read its bindings until after
            // dispatch, and TryResolve does not consume them. The winner unbinds all four stores in
            // its own finally.
            return new JsonRpcResponse
            {
                Id = rpcId,
                Result = null,
                Error = new JsonRpcError
                {
                    Code = -32600,
                    Message = "Duplicate JSON-RPC request id: a tools/call with this id is already in flight.",
                    Data = null,
                },
            };
        }

        Guid? previousAmbient = SessionAttachmentToolAmbient.CurrentSessionId;
        ApplyPatchInvocationContext? previousPatchAmbient =
            ApplyPatchInvocationAmbient.Current;
        PersistedToolInvocationContext? previousPersistedAmbient =
            PersistedToolInvocationAmbient.Current;
        ApprenticeToolInvocationContext? previousApprenticeAmbient =
            ApprenticeToolInvocationAmbient.Current;
        CovenantToolCapabilityGrant? previousCovenantAmbient =
            CovenantToolInvocationAmbient.Current;
        CovenantToolCapabilityGrant? takenCovenantCapability = null;
        try
        {
            JsonElement toolArguments = call.Arguments;

            Guid? resolvedSession = ResolveAmbientSessionForToolsCall(requestKey, ref toolArguments);

            SessionAttachmentToolAmbient.CurrentSessionId = resolvedSession;

            ApplyPatchInvocationAmbient.Current =
                ApplyPatchInvocationBinding.TryResolveRequest(
                    _ambientConnectionKey,
                    requestKey,
                    out ApplyPatchInvocationContext? patchContext)
                    ? patchContext
                    : null;
            ApplyPatchInvocationAmbient.Current?.MarkDispatched();
            PersistedToolInvocationAmbient.Current =
                PersistedToolInvocationBinding.TryResolveRequest(
                    _ambientConnectionKey,
                    requestKey,
                    out PersistedToolInvocationContext?
                        persistedContext)
                    ? persistedContext
                    : null;

            // Tells Apprentice-scoped tools (dispatch_sending) which Apprentice is calling and which
            // A2A delegation chain it inherited, so a Sending it makes extends that chain instead of
            // restarting it (issue #59).
            ApprenticeToolInvocationAmbient.Current =
                ApprenticeToolInvocationBinding.TryResolveRequest(
                    _ambientConnectionKey,
                    requestKey,
                    out ApprenticeToolInvocationContext? apprenticeContext)
                    ? apprenticeContext
                    : null;

            // Taken before dispatch, exactly once, on the server's own task. A handler that resolved
            // its own capability could be raced by a second call reusing the same request id.
            if (CovenantToolNames.IsCovenantMutationTool(call.Name)
                && _covenantCapabilities is not null
                && _covenantCapabilities.TryTake(_ambientConnectionKey, requestKey) is { IsSuccess: true } grant)
            {
                takenCovenantCapability = grant.Value;
            }

            CovenantToolInvocationAmbient.Current = takenCovenantCapability;

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
            ApplyPatchInvocationAmbient.Current = previousPatchAmbient;
            PersistedToolInvocationAmbient.Current =
                previousPersistedAmbient;
            ApprenticeToolInvocationAmbient.Current =
                previousApprenticeAmbient;
            CovenantToolInvocationAmbient.Current = previousCovenantAmbient;

            if (takenCovenantCapability is { } spent)
            {
                // Drain first, then free the id. Freeing it first would let a later call install a
                // capability under an id whose previous handler is still finishing.
                await spent.Capability.DisposeAsync().ConfigureAwait(false);

                _ = _covenantCapabilities?.Remove(
                    _ambientConnectionKey,
                    requestKey,
                    spent.Nonce);
            }

            SessionAttachmentToolAmbient.UnbindRequest(_ambientConnectionKey, requestKey);
            ApplyPatchInvocationBinding.UnbindRequest(
                _ambientConnectionKey,
                requestKey);
            PersistedToolInvocationBinding.UnbindRequest(
                _ambientConnectionKey,
                requestKey);
            ApprenticeToolInvocationBinding.UnbindRequest(
                _ambientConnectionKey,
                requestKey);

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

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());

        return document.RootElement.Clone();

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
