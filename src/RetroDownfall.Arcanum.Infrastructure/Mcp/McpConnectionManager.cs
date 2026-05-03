using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Loads Cursor-style <c>mcp.json</c> from the user profile and from each workspace, spawns MCP servers, and exposes merged tools as <see cref="AITool"/>.
/// </summary>
/// <remarks>
/// <para>
/// Global config is <c>~/.config/arcanum/mcp.json</c>. Workspace <c>mcp.json</c> is merged per normalized workspace root; duplicate tool names use the local registration.
/// Merged tool lists and spawned processes are cached per workspace for the process lifetime.
/// </para>
/// </remarks>
public sealed class McpConnectionManager(ILogger<McpConnectionManager> logger) : IAsyncDisposable
{
    private const string GlobalPartitionKey = "__arcanum_mcp_global__";

    private const string NoWorkspaceKey = "__arcanum_no_workspace__";

    private readonly SemaphoreSlim _globalInitLock = new(1, 1);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceInitLocks = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, IReadOnlyList<AITool>> _mergedToolsByWorkspace = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, List<McpClient>> _clientsByPartition = new(StringComparer.Ordinal);

    private bool _globalInitialized;

    private Dictionary<string, LoadedMcpTool> _globalFirstByToolName = new(StringComparer.Ordinal);

    private IReadOnlyList<AITool> _globalSurfaceTools = [];

    private bool _disposed;

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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (List<McpClient> bucket in _clientsByPartition.Values)
        {
            for (int i = bucket.Count - 1; i >= 0; i--)
            {
                try
                {
                    await bucket[i].DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error disposing MCP client instance.");
                }
            }

            bucket.Clear();
        }

        _clientsByPartition.Clear();

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

            List<McpClient> globalClients = GetOrCreateClientList(GlobalPartitionKey);

            string globalPath = GetGlobalMcpConfigPath();

            if (!File.Exists(globalPath))
            {
                FinalizeGlobalState([]);

                return;
            }

            McpConfig? config = await ReadMcpConfigAsync(globalPath, cancellationToken).ConfigureAwait(false);

            if (config is null)
            {
                FinalizeGlobalState([]);

                return;
            }

            if (config.McpServers is null || config.McpServers.Count == 0)
            {
                logger.LogInformation("MCP config at {ConfigPath} has no mcpServers entries.", globalPath);

                FinalizeGlobalState([]);

                return;
            }

            List<LoadedMcpTool> tagged = [];

            await StartServersFromConfigAsync(
                    config,
                    logScope: "global",
                    clientsSink: globalClients,
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
            string n = row.Tool.Name;

            if (byName.ContainsKey(n))
            {
                continue;
            }

            byName[n] = row;

            surface.Add(row.Tool);
        }

        _globalFirstByToolName = byName;

        _globalSurfaceTools = surface;

        _globalInitialized = true;
    }

    private async Task<IReadOnlyList<AITool>> BuildMergedToolsForWorkspaceAsync(string workspaceKey, CancellationToken cancellationToken)
    {
        if (workspaceKey == NoWorkspaceKey)
        {
            return _globalSurfaceTools;
        }

        string localPath = Path.Combine(workspaceKey, "mcp.json");

        if (!File.Exists(localPath))
        {
            return _globalSurfaceTools;
        }

        McpConfig? localConfig = await ReadMcpConfigAsync(localPath, cancellationToken).ConfigureAwait(false);

        if (localConfig is null)
        {
            return _globalSurfaceTools;
        }

        if (localConfig.McpServers is null || localConfig.McpServers.Count == 0)
        {
            return _globalSurfaceTools;
        }

        List<McpClient> localClients = GetOrCreateClientList(workspaceKey);

        List<LoadedMcpTool> localTagged = [];

        await StartServersFromConfigAsync(
                localConfig,
                logScope: workspaceKey,
                clientsSink: localClients,
                toolsSink: localTagged,
                cancellationToken)
            .ConfigureAwait(false);

        if (localTagged.Count == 0)
        {
            return _globalSurfaceTools;
        }

        return MergeGlobalAndLocal(_globalSurfaceTools, _globalFirstByToolName, localTagged);
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

            if (indexByName.ContainsKey(name))
            {
                continue;
            }

            indexByName[name] = merged.Count;

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

            McpClient? fallback = null;

            if (globalByName.TryGetValue(name, out LoadedMcpTool globalRow)
                && !McpServerRegistrationComparer.Equals(globalRow.Config, localRow.Config))
            {
                fallback = globalRow.Client;
            }

            McpBridgeTool replacement = new(
                localRow.Tool.Name,
                localRow.Tool.Description,
                localRow.Tool.JsonSchema,
                localRow.Client,
                fallbackClient: fallback,
                fallbackLogger: logger);

            merged[idx] = replacement;
        }

        return merged;
    }

    private List<McpClient> GetOrCreateClientList(string partitionKey)
    {
        return _clientsByPartition.GetOrAdd(partitionKey, static _ => []);
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

                client = new McpClient(transport);

                await client.InitializeAsync(cancellationToken).ConfigureAwait(false);

                IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync(cancellationToken).ConfigureAwait(false);

                clientsSink.Add(client);

                client = null;

                foreach (McpBridgeTool t in tools)
                {
                    toolsSink.Add(new LoadedMcpTool(t, cfg, clientsSink[^1]));
                }

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
