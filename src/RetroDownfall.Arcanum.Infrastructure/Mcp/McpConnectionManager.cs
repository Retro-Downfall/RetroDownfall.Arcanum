using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Loads standard <c>mcp.json</c> from the user profile and from each workspace, spawns MCP servers, and exposes merged tools as <see cref="AITool"/>.
/// </summary>
/// <remarks>
/// <para>
/// Global config is <c>~/.config/arcanum/mcp.json</c>. Workspace <c>mcp.json</c> is merged per normalized workspace root; duplicate tool names use the local registration.
/// Merged tool lists and spawned processes are cached per workspace for the process lifetime.
/// </para>
/// <para>
/// The in-process Arcanum internal MCP server is started once per partition key (including a sentinel when no workspace is set) so <c>ask_human</c> remains available globally.
/// </para>
/// </remarks>
public sealed class McpConnectionManager(
    ILogger<McpConnectionManager> logger,
    IHumanPromptRegistry humanPromptRegistry,
    IServiceScopeFactory scopeFactory,
    IUnseenServantPacer pacer,
    IOptions<ArcanumSettings> settings) : IAsyncDisposable
{

    private const string GlobalPartitionKey = "__arcanum_mcp_global__";

    private const string NoWorkspaceKey = "__arcanum_no_workspace__";

    private static readonly McpServerConfig InternalMcpServerConfig = new() { Command = "arcanum-internal" };

    private readonly SemaphoreSlim _globalInitLock = new(1, 1);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceInitLocks = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, IReadOnlyList<AITool>> _mergedToolsByWorkspace = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, McpPartitionClients> _partitionClients = new(StringComparer.Ordinal);

    private bool _globalInitialized;

    private Dictionary<string, LoadedMcpTool> _globalFirstByToolName = new(StringComparer.Ordinal);

    private IReadOnlyList<AITool> _globalSurfaceTools = [];

    private volatile bool _disposed;

    /// <summary>
    /// Returns merged MCP bridge tools: global <c>mcp.json</c> plus workspace-local <c>mcp.json</c> when present.
    /// </summary>
    public async Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(string? workingDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string workspaceKey = NormalizeWorkspaceKey(workingDirectory);

        if (_mergedToolsByWorkspace.TryGetValue(workspaceKey, out IReadOnlyList<AITool>? cached))
        {
            return cached;
        }

        await EnsureGlobalLoadedAsync(cancellationToken).ConfigureAwait(false);

        SemaphoreSlim workspaceLock = _workspaceInitLocks.GetOrAdd(
            workspaceKey,
            static _ => new SemaphoreSlim(1, 1));

        await workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_mergedToolsByWorkspace.TryGetValue(workspaceKey, out cached))
            {
                return cached;
            }

            IReadOnlyList<AITool> merged = await BuildMergedToolsForWorkspaceAsync(workspaceKey, cancellationToken).ConfigureAwait(false);

            _mergedToolsByWorkspace[workspaceKey] = merged;

            return merged;
        }
        finally
        {
            workspaceLock.Release();
        }
    }

    /// <summary>
    /// Returns MCP server rows for the global profile plus the workspace partition (internal + workspace-local servers), in merge order.
    /// </summary>
    public async Task<List<McpServerStatusDto>> GetServerStatusesAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await GetAvailableToolsAsync(workingDirectory, cancellationToken).ConfigureAwait(false);

        List<McpServerStatusDto> result = [];

        if (_partitionClients.TryGetValue(GlobalPartitionKey, out McpPartitionClients? globalPartition))
        {
            foreach (McpServerMetadata meta in globalPartition.Servers)
            {
                result.Add(ToStatusDto(meta));
            }
        }

        string workspaceKey = NormalizeWorkspaceKey(workingDirectory);

        if (_partitionClients.TryGetValue(workspaceKey, out McpPartitionClients? workspacePartition))
        {
            foreach (McpServerMetadata meta in workspacePartition.Servers)
            {
                result.Add(ToStatusDto(meta));
            }
        }

        return result;
    }

    /// <summary>
    /// Disposes all MCP clients, clears workspace caches and partition state, disposes per-workspace locks, resets global bootstrap flags, and immediately re-loads global <c>mcp.json</c>.
    /// </summary>
    public async Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _globalInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (McpPartitionClients partition in _partitionClients.Values)
            {
                foreach (McpClient client in partition.Clients)
                {
                    try
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error disposing MCP client instance during reload.");
                    }
                }
            }

            _partitionClients.Clear();

            _mergedToolsByWorkspace.Clear();

            foreach (SemaphoreSlim workspaceLock in _workspaceInitLocks.Values)
            {
                try
                {
                    workspaceLock.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error disposing workspace init lock during MCP reload.");
                }
            }

            _workspaceInitLocks.Clear();

            _globalInitialized = false;

            _globalFirstByToolName = new(StringComparer.Ordinal);

            _globalSurfaceTools = [];
        }
        finally
        {
            _globalInitLock.Release();
        }

        await EnsureGlobalLoadedAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "MCP connection manager reloaded (workspace hint: {WorkingDirectory}); global re-bootstrapped, all partitions cleared.",
            string.IsNullOrWhiteSpace(workingDirectory) ? "(empty)" : workingDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (McpPartitionClients partition in _partitionClients.Values)
        {
            for (int i = partition.Clients.Count - 1; i >= 0; i--)
            {
                try
                {
                    await partition.Clients[i].DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error disposing MCP client instance.");
                }
            }

            partition.Clients.Clear();
        }

        _partitionClients.Clear();

        _mergedToolsByWorkspace.Clear();

        _globalInitLock.Dispose();

        foreach (SemaphoreSlim slim in _workspaceInitLocks.Values)
        {
            slim.Dispose();
        }

        _workspaceInitLocks.Clear();
    }

    private static string NormalizeWorkspaceKey(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return NoWorkspaceKey;
        }

        try
        {
            return Path.GetFullPath(workingDirectory.Trim());
        }
        catch (Exception)
        {
            return NoWorkspaceKey;
        }
    }

    private static string GetGlobalMcpConfigPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "arcanum",
            "mcp.json");
    }

    private int GetClampedExecuteCommandTimeoutSeconds()
    {
        return Math.Clamp(settings.Value.Intelligence.ExecuteCommandTimeoutSeconds, 1, 600);
    }

    private TimeSpan GetClampedMcpRequestTimeout()
    {
        return TimeSpan.FromSeconds(
            ArcanumSettingClamps.McpRequestTimeoutSeconds(settings.Value.Intelligence.McpRequestTimeoutSeconds));
    }

    private int GetClampedMcpMaxPaginationPages()
    {
        return ArcanumSettingClamps.McpMaxPaginationPages(settings.Value.Intelligence.McpMaxPaginationPages);
    }

    private int GetClampedListDirectoryMaxPaths()
    {
        return ArcanumSettingClamps.ListDirectoryMaxPaths(settings.Value.Intelligence.ListDirectoryMaxPaths);
    }

    private async Task EnsureGlobalLoadedAsync(CancellationToken cancellationToken)
    {
        if (_globalInitialized)
        {
            return;
        }

        await _globalInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_globalInitialized)
            {
                return;
            }

            McpPartitionClients globalPartition = GetOrCreatePartition(GlobalPartitionKey);

            List<McpClient> globalClients = globalPartition.Clients;

            List<LoadedMcpTool> tagged = [];

            string globalPath = GetGlobalMcpConfigPath();

            if (!File.Exists(globalPath))
            {
                FinalizeGlobalState(tagged);

                return;
            }

            McpConfig? config = await ReadMcpConfigAsync(globalPath, cancellationToken).ConfigureAwait(false);

            if (config is null)
            {
                FinalizeGlobalState(tagged);

                return;
            }

            if (config.McpServers is null || config.McpServers.Count == 0)
            {
                logger.LogInformation("MCP config at {ConfigPath} has no mcpServers entries.", globalPath);

                FinalizeGlobalState(tagged);

                return;
            }

            await StartServersFromConfigAsync(
                    config,
                    logScope: "global",
                    clientsSink: globalClients,
                    serverMetadataSink: globalPartition.Servers,
                    toolsSink: tagged,
                    cancellationToken)
                .ConfigureAwait(false);

            FinalizeGlobalState(tagged);
        }
        finally
        {
            _globalInitLock.Release();
        }
    }

    private void FinalizeGlobalState(List<LoadedMcpTool> tagged)
    {
        Dictionary<string, LoadedMcpTool> byName = new(StringComparer.Ordinal);

        List<AITool> surface = [];

        foreach (LoadedMcpTool row in tagged)
        {
            if (byName.TryAdd(row.Tool.Name, row))
            {
                surface.Add(row.Tool);
            }
        }

        _globalFirstByToolName = byName;

        _globalSurfaceTools = surface;

        _globalInitialized = true;
    }

    private async Task<IReadOnlyList<AITool>> BuildMergedToolsForWorkspaceAsync(string workspaceKey, CancellationToken cancellationToken)
    {
        McpPartitionClients partition = _partitionClients.GetOrAdd(workspaceKey, static _ => new McpPartitionClients());

        List<LoadedMcpTool> internalTagged = await EnsurePartitionInternalToolsAsync(partition, workspaceKey, cancellationToken).ConfigureAwait(false);

        List<LoadedMcpTool> workspaceLocalTagged = [];

        if (workspaceKey != NoWorkspaceKey)
        {
            string localPath = Path.Combine(workspaceKey, "mcp.json");

            if (File.Exists(localPath))
            {
                McpConfig? localConfig = await ReadMcpConfigAsync(localPath, cancellationToken).ConfigureAwait(false);

                if (localConfig?.McpServers is { Count: > 0 })
                {
                    await StartServersFromConfigAsync(
                            localConfig,
                            logScope: workspaceKey,
                            clientsSink: partition.Clients,
                            serverMetadataSink: partition.Servers,
                            toolsSink: workspaceLocalTagged,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        return MergeInternalProfileAndLocal(internalTagged, workspaceLocalTagged);
    }

    private async Task<List<LoadedMcpTool>> EnsurePartitionInternalToolsAsync(
        McpPartitionClients partition,
        string workspaceKey,
        CancellationToken cancellationToken)
    {
        if (partition.InternalServerStarted && partition.CachedInternalTools is { } cached)
        {
            return new List<LoadedMcpTool>(cached);
        }

        List<LoadedMcpTool> internalTagged = [];

        await StartInternalInProcessServerForPartitionAsync(
                partition,
                workspaceKey,
                internalTagged,
                cancellationToken)
            .ConfigureAwait(false);

        partition.InternalServerStarted = true;

        partition.CachedInternalTools = new List<LoadedMcpTool>(internalTagged);

        return internalTagged;
    }

    private IReadOnlyList<AITool> MergeInternalProfileAndLocal(
        IReadOnlyList<LoadedMcpTool> internalTagged,
        IReadOnlyList<LoadedMcpTool> workspaceLocalTagged)
    {
        List<AITool> surface = [];

        Dictionary<string, LoadedMcpTool> mergedByName = new(StringComparer.Ordinal);

        foreach (LoadedMcpTool row in internalTagged)
        {
            if (mergedByName.TryAdd(row.Tool.Name, row))
            {
                surface.Add(row.Tool);
            }
        }

        foreach (KeyValuePair<string, LoadedMcpTool> kv in _globalFirstByToolName)
        {
            if (mergedByName.TryAdd(kv.Key, kv.Value))
            {
                surface.Add(kv.Value.Tool);
            }
        }

        if (workspaceLocalTagged.Count == 0)
        {
            return surface;
        }

        return MergeGlobalAndLocal(surface, mergedByName, workspaceLocalTagged);
    }

    private IReadOnlyList<AITool> MergeGlobalAndLocal(
        IReadOnlyList<AITool> globalSurface,
        IReadOnlyDictionary<string, LoadedMcpTool> globalByName,
        IReadOnlyList<LoadedMcpTool> localTagged)
    {
        List<AITool> merged = new(globalSurface.Count + localTagged.Count);

        Dictionary<string, int> indexByName = new(StringComparer.Ordinal);

        foreach (AITool t in globalSurface)
        {
            if (t is not AIFunction fn)
            {
                merged.Add(t);

                continue;
            }

            string name = fn.Name;

            if (!indexByName.TryAdd(name, merged.Count))
            {
                continue;
            }

            merged.Add(t);
        }

        foreach (LoadedMcpTool localRow in localTagged)
        {
            string name = localRow.Tool.Name;

            if (!indexByName.TryGetValue(name, out int idx))
            {
                indexByName[name] = merged.Count;

                merged.Add(localRow.Tool);

                continue;
            }

            if (!globalByName.TryGetValue(name, out LoadedMcpTool globalRow)
                || McpServerRegistrationComparer.Equals(globalRow.Config, localRow.Config))
            {
                merged[idx] = localRow.Tool;

                continue;
            }

            McpBridgeTool replacement = new(
                localRow.Tool.Name,
                localRow.Tool.Description,
                localRow.Tool.JsonSchema,
                localRow.Client,
                fallbackClient: globalRow.Client,
                fallbackLogger: logger);

            merged[idx] = replacement;
        }

        return merged;
    }

    private McpPartitionClients GetOrCreatePartition(string partitionKey)
    {
        return _partitionClients.GetOrAdd(partitionKey, static _ => new McpPartitionClients());
    }

    private sealed class McpPartitionClients
    {

        public List<McpClient> Clients { get; } = [];

        public List<McpServerMetadata> Servers { get; } = [];

        public bool InternalServerStarted { get; set; }

        public IReadOnlyList<LoadedMcpTool>? CachedInternalTools { get; set; }

    }

    private sealed record McpServerMetadata(
        string ServerName,
        string Status,
        List<string> ToolNames,
        string? ErrorMessage);

    private static McpServerStatusDto ToStatusDto(McpServerMetadata meta)
    {
        return new McpServerStatusDto(
            meta.ServerName,
            meta.Status,
            meta.ToolNames.Count,
            new List<string>(meta.ToolNames),
            meta.ErrorMessage);
    }

    private async Task StartInternalInProcessServerForPartitionAsync(
        McpPartitionClients partition,
        string workspaceKey,
        List<LoadedMcpTool> tagged,
        CancellationToken cancellationToken)
    {
        int timeoutSeconds = GetClampedExecuteCommandTimeoutSeconds();

        TimeSpan executeTimeout = TimeSpan.FromSeconds(timeoutSeconds);

        string? workspaceRoot = workspaceKey == NoWorkspaceKey ? null : workspaceKey;

        int listDirectoryMaxPaths = GetClampedListDirectoryMaxPaths();

        (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
            humanPromptRegistry,
            scopeFactory,
            pacer,
            workspaceRoot,
            executeTimeout,
            timeoutSeconds,
            listDirectoryMaxPaths,
            settings.Value.Intelligence,
            logger: null);

        Task serverTask = Task.Run(() => server.RunAsync(transport.LifetimeCancellation), CancellationToken.None);

        ObserveInternalServerTask(serverTask);

        McpClient? client = null;

        List<McpClient> partitionClients = partition.Clients;

        try
        {
            client = new McpClient(transport, GetClampedMcpRequestTimeout(), GetClampedMcpMaxPaginationPages());

            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync(cancellationToken).ConfigureAwait(false);

            partitionClients.Add(client);

            client = null;

            foreach (McpBridgeTool t in tools)
            {
                tagged.Add(new LoadedMcpTool(t, InternalMcpServerConfig, partitionClients[^1]));
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

    private async Task<McpConfig?> ReadMcpConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        try
        {
            byte[] utf8 = await File.ReadAllBytesAsync(configPath, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Deserialize(utf8, McpConfigJsonSerializerContext.Default.McpConfig);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read or parse MCP config at {ConfigPath}.", configPath);

            return null;
        }
    }

    private async Task StartServersFromConfigAsync(
        McpConfig config,
        string logScope,
        List<McpClient> clientsSink,
        List<McpServerMetadata> serverMetadataSink,
        List<LoadedMcpTool> toolsSink,
        CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<string, McpServerConfig> pair in config.McpServers!)
        {
            string serverName = pair.Key;

            McpServerConfig cfg = pair.Value;

            string? command = cfg.Command;

            if (string.IsNullOrWhiteSpace(command))
            {
                logger.LogWarning("Skipping MCP server {ServerName} ({Scope}): missing command.", serverName, logScope);

                continue;
            }

            McpClient? client = null;

            try
            {
                string[] args = cfg.Args ?? [];

                McpProcessTransport transport = new(
                    command.Trim(),
                    arguments: string.Empty,
                    argumentList: args,
                    environment: cfg.Env)
                {
                    OnStderrLine = line => logger.LogDebug(
                        "MCP server {ServerName} ({Scope}) stderr: {Line}",
                        serverName,
                        logScope,
                        line),
                };

                client = new McpClient(transport, GetClampedMcpRequestTimeout(), GetClampedMcpMaxPaginationPages());

                await client.InitializeAsync(cancellationToken).ConfigureAwait(false);

                IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync(cancellationToken).ConfigureAwait(false);

                clientsSink.Add(client);

                client = null;

                foreach (McpBridgeTool t in tools)
                {
                    toolsSink.Add(new LoadedMcpTool(t, cfg, clientsSink[^1]));
                }

                serverMetadataSink.Add(new McpServerMetadata(
                    serverName,
                    "Online",
                    tools.Select(static t => t.Name).ToList(),
                    null));

                logger.LogInformation(
                    "Started MCP server {ServerName} ({Scope}) with {ToolCount} tools.",
                    serverName,
                    logScope,
                    tools.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }

                Exception baseEx = ex.GetBaseException();

                serverMetadataSink.Add(new McpServerMetadata(
                    serverName,
                    "Failed",
                    [],
                    baseEx.Message));

                logger.LogError(
                    ex,
                    "MCP server {ServerName} ({Scope}) failed to start or list tools; continuing with other servers.",
                    serverName,
                    logScope);
            }
        }
    }

    private readonly record struct LoadedMcpTool(McpBridgeTool Tool, McpServerConfig Config, McpClient Client);

}
