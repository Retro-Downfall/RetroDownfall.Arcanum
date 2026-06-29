using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

public sealed partial class McpConnectionManager
{

    // W-MCP-HTTP: transport factory. Stdio spawns a subprocess + correlation client; Http builds a
    // stateless Streamable HTTP client over the SSRF-guarded named HttpClient; legacy SSE remains
    // unsupported. Both transports converge on FinishStartAsync (initialize + tools/list + wiring).
    private async Task<Result> StartManagedServerCoreAsync(ManagedMcpServerEntry entry, CancellationToken cancellationToken)
    {
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

        McpProcessTransport transport = new(
            command.Trim(),
            arguments: string.Empty,
            maxJsonRpcLineBytes: GetClampedMcpMaxJsonRpcLineBytes(),
            argumentList: args,
            environment: cfg.Env,
            workingDirectory: cwdResult.Value,
            stripUserEnvironment: stripUserEnvironment,
            inheritEnvironmentAllowlist: inheritEnvironmentAllowlist)
        {
            OnStderrLine = line => logger.LogDebug(
                "MCP server {ServerName} ({Scope}) stderr: {Line}",
                entry.Name,
                logScope,
                line),
            OnTransportEnded = () => HandleTransportEnded(capturedEntry, transportGeneration),
        };

        McpClient client = CreateMcpClient(transport);

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

        McpHttpClient client = CreateHttpMcpClient(endpointResult.Value);

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

    private async Task StopManagedServerCoreAsync(ManagedMcpServerEntry entry, CancellationToken cancellationToken)
    {
        IMcpClient? client = entry.Client;

        entry.Client = null;

        entry.LoadedTools.Clear();

        if (client is not null)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error disposing MCP client for server {ServerName}.", entry.Name);
            }
        }

        string partitionKey = entry.ScopeWorkingDirectory is null ? GlobalPartitionKey : entry.ScopeWorkingDirectory;

        if (client is not null
            && _partitionClients.TryGetValue(partitionKey, out Lazy<McpPartitionClients>? partitionLazy)
            && partitionLazy.IsValueCreated
            && partitionLazy.Value.Clients.Contains(client))
        {
            partitionLazy.Value.Clients.Remove(client);
        }
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

        _ = Task.Run(async () =>
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
    }

    private int GetClampedListDirectoryMaxPaths()
    {
        return ArcanumSettingClamps.ListDirectoryMaxPaths(settings.CurrentValue.Intelligence.ListDirectoryMaxPaths);
    }

    private long GetClampedToolOutputCapBytes()
    {
        return ArcanumSettingClamps.ToolOutputCapBytes(settings.CurrentValue.Intelligence.ToolOutputCapBytes);
    }

    private async Task StartInternalInProcessServerForPartitionAsync(
        McpPartitionClients partition,
        string workspaceKey,
        List<LoadedMcpToolRow> tagged,
        CancellationToken cancellationToken)
    {
        int timeoutSeconds = GetClampedExecuteCommandTimeoutSeconds();

        TimeSpan executeTimeout = TimeSpan.FromSeconds(timeoutSeconds);

        string? workspaceRoot = workspaceKey == NoWorkspaceKey ? null : workspaceKey;

        int listDirectoryMaxPaths = GetClampedListDirectoryMaxPaths();

        long maxFileReadSizeBytes = ArcanumSettingClamps.MaxFileReadSizeBytes(
            settings.CurrentValue.Workspaces?.MaxFileReadSizeBytes ?? new WorkspaceSettings().MaxFileReadSizeBytes);

        (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
            humanPromptRegistry,
            scopeFactory,
            pacer,
            workspaceRoot,
            executeTimeout,
            timeoutSeconds,
            listDirectoryMaxPaths,
            settings.CurrentValue.Intelligence,
            maxFileReadSizeBytes,
            settings.CurrentValue.Conclave.Enabled,
            GetClampedMcpMaxJsonRpcLineBytes(),
            logger: null);

        Task serverTask = Task.Run(() => server.RunAsync(transport.LifetimeCancellation), CancellationToken.None);

        ObserveInternalServerTask(serverTask);

        McpClient? client = null;

        List<IMcpClient> partitionClients = partition.Clients;

        try
        {
            client = CreateMcpClient(transport, requestCancellationBroker: transport.RequestCancellation);

            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync(cancellationToken).ConfigureAwait(false);

            partitionClients.Add(client);

            client = null;

            foreach (McpBridgeTool t in tools)
            {
                tagged.Add(new LoadedMcpToolRow(t, InternalMcpServerConfig, partitionClients[^1]));
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
            }

            throw;
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
