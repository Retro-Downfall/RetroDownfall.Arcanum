using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

public sealed partial class McpConnectionManager
{

    // W-MCP-HTTP: transport factory. Stdio spawns a subprocess + correlation client; Http builds a
    // stateless Streamable HTTP client over the SSRF-guarded named HttpClient; legacy SSE remains
    // unsupported. Both transports converge on FinishStartAsync (initialize + tools/list + wiring).
    private async Task<Result> StartManagedServerCoreAsync(ManagedMcpServerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.DetachedClientDisposal is { } detachedDisposal)
        {
            if (!detachedDisposal.IsCompleted)
            {
                return new Error(
                    "Mcp.ClientDisposalIncomplete",
                    "The previous MCP client is still shutting down.");
            }

            entry.DetachedClientDisposal = null;
        }

        McpServerConfig cfg = entry.Config;

        return entry.Transport switch
        {
            McpServerTransport.Http => await StartHttpServerCoreAsync(entry, cfg, cancellationToken).ConfigureAwait(false),
            McpServerTransport.Sse => new Error("Mcp.SseNotSupported", "SSE transport is not yet supported."),
            _ => await StartStdioServerCoreAsync(entry, cfg, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<Result> StartStdioServerCoreAsync(
        ManagedMcpServerEntry entry,
        McpServerConfig cfg,
        CancellationToken cancellationToken)
    {
        string? command = cfg.Command;

        if (string.IsNullOrWhiteSpace(command))
        {
            return new Error("Mcp.MissingCommand", $"MCP server '{entry.Name}' has no command.");
        }

        string[] args = cfg.Args ?? [];

        string logScope = entry.ScopeWorkingDirectory ?? "global";

        ManagedMcpServerEntry capturedEntry = entry;

        long transportGeneration = ++entry.TransportGeneration;

        bool stripUserEnvironment = ShouldStripUserEnvironment(cfg);

        IReadOnlySet<string>? inheritEnvironmentAllowlist = BuildInheritEnvironmentAllowlist(cfg.InheritEnv);

        Result<string?> cwdResult = ResolveValidatedSubprocessCwd(cfg.Cwd, entry.ScopeWorkingDirectory);

        if (cwdResult.IsFailure)
        {
            return cwdResult.Error;
        }

        IReadOnlyDictionary<string, string>? scrubbedEnvironment = McpSecurityLimits.ScrubProcessEnvironment(
            cfg.Env,
            stripUserEnvironment,
            inheritEnvironmentAllowlist);

        Dictionary<string, string> environmentVariables = scrubbedEnvironment is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(scrubbedEnvironment, StringComparer.Ordinal);

        StdioClientTransport transport = new(new StdioClientTransportOptions
        {
            Command = command.Trim(),
            Arguments = args.ToList(),
            WorkingDirectory = cwdResult.Value,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environmentVariables!,
            StandardErrorLines = line => logger.LogDebug(
                "MCP server {ServerName} ({Scope}) stderr: {Line}",
                entry.Name,
                logScope,
                line),
        });

        SdkMcpClientWrapper sdkClient =
            CreateSdkMcpClientWrapper(transport);

        sdkClient.OnTransportEnded = () =>
            HandleTransportEnded(
                capturedEntry,
                transportGeneration);
        McpClientGeneration client = new(sdkClient);

        return await FinishStartAsync(entry, cfg, client, logScope, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> StartHttpServerCoreAsync(
        ManagedMcpServerEntry entry,
        McpServerConfig cfg,
        CancellationToken cancellationToken)
    {
        Result<Uri> endpointResult = await ResolveValidatedHttpEndpointAsync(cfg, cancellationToken).ConfigureAwait(false);

        if (endpointResult.IsFailure)
        {
            return endpointResult.Error;
        }

        string logScope = entry.ScopeWorkingDirectory ?? "global";

        ManagedMcpServerEntry capturedEntry = entry;

        long transportGeneration = ++entry.TransportGeneration;

        SdkMcpClientWrapper sdkClient =
            CreateHttpMcpClient(endpointResult.Value);

        // W-MCP-HTTP: the official SDK's Streamable HTTP transport is stateful (an Mcp-Session-Id
        // tracked server-side), unlike the pre-SDK-migration stateless-POST McpHttpClient. Wiring
        // OnTransportEnded off McpClient.Completion here means a dropped/expired HTTP session now
        // reactively flips the entry to Error + publishes an McpServerEvent, the same as stdio —
        // a capability the bespoke stateless implementation never had.
        sdkClient.OnTransportEnded = () =>
            HandleTransportEnded(
                capturedEntry,
                transportGeneration);
        McpClientGeneration client = new(sdkClient);

        return await FinishStartAsync(entry, cfg, client, logScope, cancellationToken).ConfigureAwait(false);
    }

    // Shared start completion for both transports: run the initialize handshake, project tools, and
    // wire the entry. On any non-cancellation failure the freshly created client is disposed (which
    // tears down a stdio subprocess) and the entry is reset so a retry starts clean.
    private async Task<Result> FinishStartAsync(
        ManagedMcpServerEntry entry,
        McpServerConfig cfg,
        IMcpClient client,
        string logScope,
        CancellationToken cancellationToken)
    {
        IMcpClient? pending = client;

        try
        {
            await pending.InitializeAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<McpBridgeTool> tools = await pending.GetToolsAsync(cancellationToken).ConfigureAwait(false);

            entry.Client = pending;

            pending = null;

            entry.LoadedTools.Clear();

            foreach (McpBridgeTool t in tools)
            {
                entry.LoadedTools.Add(new LoadedMcpToolRow(t, cfg, entry.Client));
            }

            entry.Tools = tools.Select(static t => t.Name).ToArray();

            logger.LogInformation(
                "Started MCP server {ServerName} ({Scope}) with {ToolCount} tools.",
                entry.Name,
                logScope,
                tools.Count);

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            // Unlike the non-cancellation path below, entry.Client/LoadedTools/Tools are left
            // untouched here — the caller (StartManagedServerAsync) is responsible for resetting
            // entry.State off Starting on cancellation so a future start attempt is not
            // short-circuited into a false "already starting" success. This method only owns not
            // leaking the subprocess/client that InitializeAsync/GetToolsAsync may have partially
            // stood up before cancellation.
            if (pending is not null)
            {
                await pending.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (pending is not null)
            {
                await pending.DisposeAsync().ConfigureAwait(false);
            }

            Exception baseEx = ex.GetBaseException();

            entry.Client = null;

            entry.LoadedTools.Clear();

            entry.Tools = [];

            logger.LogError(
                ex,
                "MCP server {ServerName} ({Scope}) failed to start or list tools.",
                entry.Name,
                entry.ScopeWorkingDirectory ?? "global");

            return new Error("Mcp.StartFailed", baseEx.Message);
        }
    }

    /// <summary>
    /// Wraps <see cref="StartManagedServerCoreAsync"/> so a canceled start (the caller's
    /// <paramref name="cancellationToken"/> firing mid-handshake) resets <paramref name="entry"/> off
    /// <see cref="McpServerState.Starting"/> before rethrowing. <see cref="FinishStartAsync"/> already
    /// disposes any partially-started client/subprocess on cancellation; without this reset, the entry
    /// would remain stuck at <see cref="McpServerState.Starting"/> forever, which would make a future
    /// <c>StartAsync</c> call for the same entry short-circuit into a false "already starting" success
    /// (see the state check at the top of <c>StartAsync</c>/<c>RestartAsync</c>) without ever actually
    /// starting anything.
    /// </summary>
    private async Task<Result> StartManagedServerWithCancellationHandlingAsync(
        ManagedMcpServerEntry entry,
        List<McpServerEvent> pendingEvents,
        CancellationToken cancellationToken)
    {
        try
        {
            return await StartManagedServerCoreAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            entry.State = McpServerState.Error;

            entry.ErrorMessage = "MCP server start was canceled.";

            pendingEvents.Add(BuildEvent(entry, McpServerState.Error, entry.ErrorMessage, []));

            throw;
        }
    }

    private async Task<bool> StopManagedServerCoreAsync(
        ManagedMcpServerEntry entry)
    {
        IMcpClient? client = entry.Client;

        entry.Client = null;

        entry.LoadedTools.Clear();

        Task? disposal = entry.DetachedClientDisposal;

        try
        {
            if (client is not null)
            {
                disposal = null;
                disposal = client.DisposeAsync()
                    .AsTask();

                entry.DetachedClientDisposal = disposal;
                TrackPendingWorkspaceRetirement(disposal);

                using CancellationTokenSource cleanupDeadline =
                    new(WorkspaceRetirementCleanupTimeout);

                await disposal
                    .WaitAsync(cleanupDeadline.Token)
                    .ConfigureAwait(false);
            }
            else if (disposal is not null)
            {
                if (!disposal.IsCompletedSuccessfully)
                {
                    _ = disposal.Exception;

                    return false;
                }
            }
        }
        catch (OperationCanceledException)
            when (disposal is { IsCompleted: false })
        {
            logger.LogWarning(
                "Timed out disposing a detached MCP client for server {ServerName}.",
                entry.Name);

            return false;
        }
        catch (Exception ex)
        {
            entry.DetachedClientDisposal =
                disposal
                ?? Task.FromException(ex);

            logger.LogWarning(ex, "Error disposing MCP client for server {ServerName}.", entry.Name);

            return false;
        }
        finally
        {
            string partitionKey = entry.ScopeWorkingDirectory is null
                ? GlobalPartitionKey
                : entry.ScopeWorkingDirectory;

            if (client is not null
                && _partitionClients.TryGetValue(
                    partitionKey,
                    out Lazy<McpPartitionClients>? partitionLazy)
                && partitionLazy.IsValueCreated)
            {
                partitionLazy.Value.Clients.Remove(client);
            }
        }

        entry.DetachedClientDisposal = null;

        return true;
    }

    private void RemoveClientFromPartition(ManagedMcpServerEntry entry, IMcpClient client)
    {
        string partitionKey = entry.ScopeWorkingDirectory is null ? GlobalPartitionKey : entry.ScopeWorkingDirectory;

        if (_partitionClients.TryGetValue(partitionKey, out Lazy<McpPartitionClients>? partitionLazy)
            && partitionLazy.IsValueCreated
            && partitionLazy.Value.Clients.Contains(client))
        {
            partitionLazy.Value.Clients.Remove(client);
        }
    }

    private static bool IsRestartBackoffActive(ManagedMcpServerEntry entry) =>
        entry.RestartAfterUtc is { } until && DateTimeOffset.UtcNow < until;

    private static void ScheduleRestartBackoff(ManagedMcpServerEntry entry)
    {
        if (entry.AlwaysOn)
        {
            entry.RestartAfterUtc = DateTimeOffset.UtcNow + AlwaysOnRestartBackoff;
        }
    }

    private static Result<string?> ResolveValidatedSubprocessCwd(string? configuredCwd, string? scopeWorkspace)
    {
        if (string.IsNullOrWhiteSpace(configuredCwd))
        {
            return Result<string?>.Success(null);
        }

        string trimmed = configuredCwd.Trim();

        try
        {
            if (scopeWorkspace is not null)
            {
                string workspaceRoot = Path.GetFullPath(scopeWorkspace);

                string resolved = Path.IsPathRooted(trimmed)
                    ? Path.GetFullPath(trimmed)
                    : Path.GetFullPath(Path.Combine(workspaceRoot, trimmed));

                if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, resolved, out _))
                {
                    return Result<string?>.Failure(new Error(
                        "Mcp.InvalidCwd",
                        "MCP server cwd must stay within the workspace sandbox."));
                }

                if (!Directory.Exists(resolved))
                {
                    return Result<string?>.Failure(new Error(
                        "Mcp.InvalidCwd",
                        "MCP server cwd does not exist or is not a directory."));
                }

                return Result<string?>.Success(resolved);
            }

            if (!Path.IsPathRooted(trimmed))
            {
                return Result<string?>.Failure(new Error(
                    "Mcp.InvalidCwd",
                    "Global MCP server cwd must be an absolute path."));
            }

            string globalResolved = Path.GetFullPath(trimmed);

            if (!Directory.Exists(globalResolved))
            {
                return Result<string?>.Failure(new Error(
                    "Mcp.InvalidCwd",
                    "MCP server cwd does not exist or is not a directory."));
            }

            return Result<string?>.Success(globalResolved);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return Result<string?>.Failure(new Error("Mcp.InvalidCwd", "MCP server cwd could not be resolved."));
        }
    }

    private void HandleTransportEnded(ManagedMcpServerEntry entry, long transportGeneration)
    {

        if (_disposed)
        {

            return;

        }

        Task handlerTask = Task.Run(async () =>
        {

            if (_disposed)
            {

                return;

            }

            if (!ManagedMcpServerEntry.IsTransportGenerationCurrent(transportGeneration, entry.TransportGeneration))
            {

                return;

            }

            McpServerEvent? pendingEvent = null;

            try
            {
                await entry.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (_disposed || entry.State is not McpServerState.Running)
                {
                    return;
                }

                entry.State = McpServerState.Error;

                entry.ErrorMessage = "MCP server process exited unexpectedly.";

                IMcpClient? client = entry.Client;

                entry.Client = null;

                entry.LoadedTools.Clear();

                entry.Tools = [];

                if (client is not null)
                {
                    try
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error disposing MCP client after transport exit for server {ServerName}.", entry.Name);
                    }

                    RemoveClientFromPartition(entry, client);
                }

                ScheduleRestartBackoff(entry);

                pendingEvent = BuildEvent(entry, McpServerState.Error, entry.ErrorMessage, []);

                InvalidateCachesForServer(entry);

                RemoveServerMetadataFromPartition(entry);
            }
            finally
            {
                try
                {
                    entry.Gate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Host is shutting down and disposed the per-server gate.
                }
            }

            if (pendingEvent is not null)
            {
                PublishEvent(pendingEvent);
            }
        });

        _pendingTransportEndedTasks[handlerTask] = 0;

        _ = handlerTask.ContinueWith(
            completed =>
            {
                _ = _pendingTransportEndedTasks.TryRemove(completed, out _);
                if (completed.IsFaulted && completed.Exception is not null)
                {
                    logger.LogWarning(
                        completed.Exception.GetBaseException(),
                        "MCP transport-ended handler faulted.");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private int GetListDirectoryPageSize()
    {
        return ArcanumRuntimeDefaults.Intelligence.ListDirectoryPageSize;
    }

    private long GetClampedToolOutputCapBytes()
    {
        return ArcanumSettingClamps.ToolOutputCapBytes(
            ArcanumRuntimeDefaults.Intelligence.ToolOutputCapBytes);
    }

    private async Task StartInternalInProcessServerForPartitionAsync(
        McpPartitionClients partition,
        string workspaceKey,
        List<LoadedMcpToolRow> tagged,
        CancellationToken cancellationToken)
    {
        string? workspaceRoot = workspaceKey == NoWorkspaceKey ? null : workspaceKey;

        int listDirectoryMaxPaths = GetListDirectoryPageSize();

        long maxFileReadSizeBytes = ArcanumSettingClamps.MaxFileReadSizeBytes(
            ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes);

        EmbeddingSettings embeddings = settings.CurrentValue.ResolveEmbeddings();

        bool sagaEnabled = embeddings.Enabled && embeddings.SagaEnabled;

        ConclaveSettings conclave = settings.CurrentValue.ResolveConclave();

        ConclaveA2ASettings a2a =
            conclave.A2A ?? ArcanumRuntimeDefaults.Conclave.A2A;

        AttachmentsSettings attachments = settings.CurrentValue.ResolveAttachments();

        bool attachmentsToolEnabled = attachments.Enabled && attachments.EnableModelAttachTool;

        ArcanumEdition edition = ArcanumEnvironment.ResolveEdition(settings.CurrentValue.Edition);

        bool allowHostProcessTools = HostProcessToolPolicy.AreAllowed(edition);

        // Feature-gated only. The Conclave tools follow Arcanum:Features:Conclave (and the A2A client
        // follows Arcanum:Features:A2AClient) on every edition; an operator who opts in is not silently
        // handed a toolset that never appears because the host runs the default Local edition (issue #12).
        bool conclaveEffective = conclave.Enabled;

        bool a2aClientEffective = conclaveEffective && a2a.Enabled && a2a.ClientEnabled;

        WorkspaceCheckSettings workspaceCheck =
            settings.CurrentValue.ResolveWorkspaceChecks();

        (ChannelWriter<string> toServer, ChannelReader<string> fromServer, ArcanumInternalToolServer server) =
            InProcessMcpTransport.CreateServerChannelPair(
                humanPromptRegistry,
                scopeFactory,
                pacer,
                workspaceRoot,
                listDirectoryMaxPaths,
                settings.CurrentValue.ResolveIntelligence(),
                maxFileReadSizeBytes,
                conclaveEffective,
                sagaEnabled,
                a2aClientEffective,
                attachmentsToolEnabled,
                GetClampedMcpMaxJsonRpcLineBytes(),
                logger: null,
                allowHostProcessTools: allowHostProcessTools,
                codingToolsSettings: settings.CurrentValue.ResolveCodingTools(),
                workspaceCheckRuntime: new WorkspaceCheckRuntime(
                    workspaceCheck,
                    scopeFactory,
                    currentSettingsProvider: () =>
                        settings.CurrentValue.ResolveWorkspaceChecks()));

        // The server's own RunAsync loop terminates when the client-to-server channel completes
        // (ChannelClientTransport session dispose calls toServer.TryComplete() on client disposal).
        // On setup failure before a client is attached, the finally block completes the channel so
        // the server task cannot orphan forever.
        Task serverTask = Task.Run(() => server.RunAsync(_hostLifetimeCts.Token), CancellationToken.None);

        ObserveInternalServerTask(serverTask);

        ChannelClientTransport clientTransport = new(
            toServer,
            fromServer,
            GetClampedMcpMaxJsonRpcLineBytes(),
            server.AmbientConnectionKey);

        McpClientGeneration? client = null;

        List<IMcpClient> partitionClients = partition.Clients;

        bool attached = false;

        try
        {
            client = new McpClientGeneration(
                CreateSdkMcpClientWrapper(
                    clientTransport));

            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync(cancellationToken).ConfigureAwait(false);

            partitionClients.Add(client);
            partition.InternalClient = client;

            client = null;

            attached = true;

            foreach (McpBridgeTool t in tools)
            {
                McpBridgeTool trustedTool = t;

                if (string.Equals(
                        t.Name,
                        ToolRiskClassifier.SearchWorkspaceToolName,
                        StringComparison.Ordinal))
                {

                    trustedTool = trustedTool.WithTrustedStructuredResult(
                        TrustedStructuredToolResultKind.WorkspaceSearch);
                }
                else if (string.Equals(
                             t.Name,
                             ToolRiskClassifier.ApplyPatchToolName,
                             StringComparison.Ordinal))
                {

                    trustedTool = trustedTool.WithTrustedStructuredResult(
                        TrustedStructuredToolResultKind.WorkspacePatch);
                }
                else if (string.Equals(
                             t.Name,
                             ToolRiskClassifier.WorkspaceCheckToolName,
                             StringComparison.Ordinal))
                {

                    trustedTool = trustedTool
                        .WithTrustedStructuredResult(
                            TrustedStructuredToolResultKind.WorkspaceCheck);
                }

                tagged.Add(
                    new LoadedMcpToolRow(
                        trustedTool,
                        InternalMcpServerConfig,
                        partitionClients[^1]));
            }

            partition.Servers.Add(new McpServerMetadata(
                "arcanum-internal",
                "Online",
                tools.Select(static t => t.Name).ToList(),
                null));

            logger.LogInformation(
                "Started in-process Arcanum internal MCP server for partition {Partition} with {ToolCount} tools.",
                workspaceKey == NoWorkspaceKey ? "no-workspace" : workspaceKey,
                tools.Count);
        }
        catch
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);

                client = null;
            }

            throw;
        }
        finally
        {
            if (!attached)
            {
                // Complete the client→server channel so RunAsync exits; dispose any leftover client
                // (idempotent if catch already disposed) and briefly drain the server task.
                toServer.TryComplete();

                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }

                try
                {
                    await serverTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best-effort drain; ObserveInternalServerTask already logs faults.
                }
            }
        }
    }

    private void ObserveInternalServerTask(Task serverTask)
    {
        _ = serverTask.ContinueWith(
            t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                {
                    logger.LogWarning(
                        t.Exception.GetBaseException(),
                        "Arcanum internal MCP server task ended with an exception.");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

}
