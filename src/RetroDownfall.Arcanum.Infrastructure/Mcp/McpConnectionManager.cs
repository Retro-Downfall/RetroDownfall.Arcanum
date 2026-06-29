using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

/// <summary>
/// Loads standard <c>mcp.json</c> from the user profile and from each workspace, spawns MCP servers, and exposes merged tools as <see cref="AITool"/>.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: spawns and manages external MCP server subprocesses; non-spawn paths are covered via InProcessMcpTransport tests.
public sealed class McpConnectionManager(
    ILogger<McpConnectionManager> logger,
    IHumanPromptRegistry humanPromptRegistry,
    IServiceScopeFactory scopeFactory,
    IUnseenServantPacer pacer,
    IEventBus eventBus,
    ITrustedMcpWorkspaceStore trustedMcpWorkspaces,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ArcanumSettings> settings) : IMcpConnectionManager, IAsyncDisposable
{

    /// <summary>Named <see cref="HttpClient"/> for the Streamable HTTP MCP transport (SSRF-guarded egress).</summary>
    public const string McpHttpClientName = "McpHttp";

    private const string GlobalPartitionKey = "__arcanum_mcp_global__";

    private const string NoWorkspaceKey = "__arcanum_no_workspace__";

    private const int MaxMcpConfigBytes = McpSecurityLimits.MaxMcpConfigBytes;

    private static readonly TimeSpan AlwaysOnRestartBackoff = TimeSpan.FromSeconds(60);

    private static readonly McpServerConfig InternalMcpServerConfig = new() { Command = "arcanum-internal" };

    // Bridges Streamable HTTP multi-round tool-response (MRTR) input requests into the shared
    // human-prompt channel (same surface as the in-process ask_human tool).
    private readonly IMcpInputElicitor _httpInputElicitor = new HumanPromptMcpInputElicitor(humanPromptRegistry);

    private readonly SemaphoreSlim _globalInitLock = new(1, 1);

    private readonly SemaphoreSlim _registryLock = new(1, 1);

    private readonly ConcurrentDictionary<string, Lazy<SemaphoreSlim>> _workspaceInitLocks = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, IReadOnlyList<AITool>> _mergedToolsByWorkspace = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Lazy<McpPartitionClients>> _partitionClients = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<(string Name, string? WorkingDirectory), ManagedMcpServerEntry> _registry = new();

    private bool _globalInitialized;

    private bool _globalRegistryLoaded;

    private Dictionary<string, LoadedMcpToolRow> _globalFirstByToolName = new(StringComparer.Ordinal);

    private IReadOnlyList<AITool> _globalSurfaceTools = [];

    private volatile bool _disposed;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureGlobalRegistryLoadedAsync(cancellationToken).ConfigureAwait(false);

        ManagedMcpServerEntry[] globalAlwaysOn = _registry.Values
            .Where(entry => entry.ScopeWorkingDirectory is null && entry.AlwaysOn)
            .ToArray();

        if (globalAlwaysOn.Length == 0)
        {
            return;
        }

        Task<Result>[] startTasks = globalAlwaysOn
            .Select(entry => StartAsync(entry.Name, null, cancellationToken))
            .ToArray();

        Result[] startResults = await Task.WhenAll(startTasks).ConfigureAwait(false);

        List<string> bootstrapFailures = [];

        for (int i = 0; i < globalAlwaysOn.Length; i++)
        {

            if (startResults[i].IsFailure)
            {

                bootstrapFailures.Add($"{globalAlwaysOn[i].Name}: {startResults[i].Error.Message}");

            }

        }

        if (bootstrapFailures.Count > 0)
        {

            logger.LogWarning(
                "MCP bootstrap: {FailureCount} always-on server(s) failed to start: {Failures}",
                bootstrapFailures.Count,
                string.Join("; ", bootstrapFailures));

        }
    }

    /// <inheritdoc />
    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {

        if (_disposed)
        {

            return;

        }

        foreach (ManagedMcpServerEntry entry in _registry.Values.ToArray())
        {
            await StopAsync(entry.Name, entry.ScopeWorkingDirectory, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<Result> StartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Result<ManagedMcpServerEntry> resolved = ResolveEntry(name, workingDirectory);

        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        ManagedMcpServerEntry entry = resolved.Value;

        if (entry.ScopeWorkingDirectory is not null)
        {

            bool trusted = await trustedMcpWorkspaces
                .IsTrustedAsync(entry.ScopeWorkingDirectory, cancellationToken)
                .ConfigureAwait(false);

            if (!trusted)
            {

                return new Error(
                    "Mcp.WorkspaceNotTrusted",
                    "Workspace-local MCP servers require operator approval. POST /api/mcp/trust-workspace for this workspace before starting.");

            }

        }

        List<McpServerEvent> pendingEvents = [];

        Result result;

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (entry.State is McpServerState.Running or McpServerState.Starting)
            {
                result = Result.Success();
            }
            else if (entry.Transport is McpServerTransport.Sse)
            {
                entry.State = McpServerState.Error;

                entry.ErrorMessage = "SSE transport is not yet supported.";

                pendingEvents.Add(BuildEvent(entry, McpServerState.Error, entry.ErrorMessage, []));

                result = new Error("Mcp.SseNotSupported", entry.ErrorMessage);
            }
            else
            {
                entry.State = McpServerState.Starting;

                pendingEvents.Add(BuildEvent(entry, McpServerState.Starting, null, []));

                Result startResult = await StartManagedServerCoreAsync(entry, cancellationToken).ConfigureAwait(false);

                if (startResult.IsFailure)
                {
                    entry.State = McpServerState.Error;

                    entry.ErrorMessage = startResult.Error.Message;

                    ScheduleRestartBackoff(entry);

                    pendingEvents.Add(BuildEvent(entry, McpServerState.Error, entry.ErrorMessage, []));

                    result = startResult;
                }
                else
                {
                    entry.State = McpServerState.Running;

                    entry.LastConnectedAt = DateTimeOffset.UtcNow;

                    entry.ErrorMessage = null;

                    entry.RestartAfterUtc = null;

                    pendingEvents.Add(BuildEvent(entry, McpServerState.Running, null, entry.Tools));

                    InvalidateCachesForServer(entry);

                    SyncPartitionServerMetadata(entry);

                    result = Result.Success();
                }
            }
        }
        finally
        {
            entry.Gate.Release();
        }

        foreach (McpServerEvent ev in pendingEvents)
        {
            PublishEvent(ev);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<Result> StopAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Result<ManagedMcpServerEntry> resolved = ResolveEntry(name, workingDirectory);

        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        ManagedMcpServerEntry entry = resolved.Value;

        McpServerEvent? pendingEvent = null;

        Result result;

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (entry.State is McpServerState.Stopped or McpServerState.Error)
            {
                result = Result.Success();
            }
            else
            {
                await StopManagedServerCoreAsync(entry, cancellationToken).ConfigureAwait(false);

                entry.State = McpServerState.Stopped;

                entry.Tools = [];

                entry.ErrorMessage = null;

                pendingEvent = BuildEvent(entry, McpServerState.Stopped, null, []);

                InvalidateCachesForServer(entry);

                RemoveServerMetadataFromPartition(entry);

                result = Result.Success();
            }
        }
        finally
        {
            entry.Gate.Release();
        }

        if (pendingEvent is not null)
        {
            PublishEvent(pendingEvent);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<Result> RestartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Result<ManagedMcpServerEntry> resolved = ResolveEntry(name, workingDirectory);

        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        ManagedMcpServerEntry entry = resolved.Value;

        if (entry.State is McpServerState.Stopped)
        {
            return await StartAsync(name, workingDirectory, cancellationToken).ConfigureAwait(false);
        }

        List<McpServerEvent> pendingEvents = [];

        Result result;

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            entry.State = McpServerState.Restarting;

            pendingEvents.Add(BuildEvent(entry, McpServerState.Restarting, null, []));

            await StopManagedServerCoreAsync(entry, cancellationToken).ConfigureAwait(false);

            entry.State = McpServerState.Stopped;

            entry.Tools = [];

            entry.ErrorMessage = null;

            pendingEvents.Add(BuildEvent(entry, McpServerState.Stopped, null, []));

            InvalidateCachesForServer(entry);

            RemoveServerMetadataFromPartition(entry);

            if (entry.Transport is McpServerTransport.Sse)
            {
                entry.State = McpServerState.Error;

                entry.ErrorMessage = "SSE transport is not yet supported.";

                pendingEvents.Add(BuildEvent(entry, McpServerState.Error, entry.ErrorMessage, []));

                result = new Error("Mcp.SseNotSupported", entry.ErrorMessage);
            }
            else
            {
                entry.State = McpServerState.Starting;

                pendingEvents.Add(BuildEvent(entry, McpServerState.Starting, null, []));

                Result startResult = await StartManagedServerCoreAsync(entry, cancellationToken).ConfigureAwait(false);

                if (startResult.IsFailure)
                {
                    entry.State = McpServerState.Error;

                    entry.ErrorMessage = startResult.Error.Message;

                    ScheduleRestartBackoff(entry);

                    pendingEvents.Add(BuildEvent(entry, McpServerState.Error, entry.ErrorMessage, []));

                    result = startResult;
                }
                else
                {
                    entry.State = McpServerState.Running;

                    entry.LastConnectedAt = DateTimeOffset.UtcNow;

                    entry.ErrorMessage = null;

                    entry.RestartAfterUtc = null;

                    pendingEvents.Add(BuildEvent(entry, McpServerState.Running, null, entry.Tools));

                    InvalidateCachesForServer(entry);

                    SyncPartitionServerMetadata(entry);

                    result = Result.Success();
                }
            }
        }
        finally
        {
            entry.Gate.Release();
        }

        foreach (McpServerEvent ev in pendingEvents)
        {
            PublishEvent(ev);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<McpServerInfo?> GetStatusAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        cancellationToken.ThrowIfCancellationRequested();

        Result<ManagedMcpServerEntry> resolved = ResolveEntry(name, workingDirectory);

        if (resolved.IsFailure)
        {
            return null;
        }

        ManagedMcpServerEntry entry = resolved.Value;

        if (!await IsWorkspaceServerVisibleAsync(entry, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ToInfo(entry);
    }

    /// <inheritdoc />
    public async Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        cancellationToken.ThrowIfCancellationRequested();

        List<McpServerInfo> statuses = [];

        foreach (ManagedMcpServerEntry entry in _registry.Values
                     .OrderBy(static e => e.ScopeWorkingDirectory ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(static e => e.Name, StringComparer.Ordinal))
        {
            if (!await IsWorkspaceServerVisibleAsync(entry, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            statuses.Add(ToInfo(entry));
        }

        return statuses.ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(string? workingDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string workspaceKey = NormalizeWorkspaceKey(workingDirectory);

        if (_mergedToolsByWorkspace.TryGetValue(workspaceKey, out IReadOnlyList<AITool>? cached))
        {
            return cached;
        }

        await EnsureGlobalLoadedAsync(cancellationToken).ConfigureAwait(false);

        SemaphoreSlim workspaceLock = _workspaceInitLocks
            .GetOrAdd(
                workspaceKey,
                static _ => new Lazy<SemaphoreSlim>(
                    static () => new SemaphoreSlim(1, 1),
                    LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;

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

    /// <inheritdoc />
    public async Task<List<McpServerStatusDto>> GetServerStatusesAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await GetAvailableToolsAsync(workingDirectory, cancellationToken).ConfigureAwait(false);

        List<McpServerStatusDto> result = [];

        if (_partitionClients.TryGetValue(GlobalPartitionKey, out Lazy<McpPartitionClients>? globalPartitionLazy)
            && globalPartitionLazy.IsValueCreated)
        {
            foreach (McpServerMetadata meta in globalPartitionLazy.Value.Servers)
            {
                result.Add(ToStatusDto(meta));
            }
        }

        string workspaceKey = NormalizeWorkspaceKey(workingDirectory);

        bool workspaceTrusted = workspaceKey == NoWorkspaceKey
            || await trustedMcpWorkspaces.IsTrustedAsync(workspaceKey, cancellationToken).ConfigureAwait(false);

        if (_partitionClients.TryGetValue(workspaceKey, out Lazy<McpPartitionClients>? workspacePartitionLazy)
            && workspacePartitionLazy.IsValueCreated)
        {
            foreach (McpServerMetadata meta in workspacePartitionLazy.Value.Servers)
            {
                if (!workspaceTrusted
                    && !string.Equals(meta.ServerName, "arcanum-internal", StringComparison.Ordinal))
                {
                    continue;
                }

                result.Add(ToStatusDto(meta));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _globalInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (ManagedMcpServerEntry entry in _registry.Values.ToArray())
            {
                await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    await StopManagedServerCoreAsync(entry, CancellationToken.None).ConfigureAwait(false);

                    entry.State = McpServerState.Stopped;

                    entry.Tools = [];

                    entry.ErrorMessage = null;
                }
                finally
                {
                    entry.Gate.Release();
                }
            }

            _registry.Clear();

            _globalRegistryLoaded = false;

            foreach (Lazy<McpPartitionClients> partitionLazy in _partitionClients.Values)
            {
                if (!partitionLazy.IsValueCreated)
                {
                    continue;
                }

                foreach (IMcpClient client in partitionLazy.Value.Clients)
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

            foreach (Lazy<SemaphoreSlim> workspaceLockLazy in _workspaceInitLocks.Values)
            {
                if (!workspaceLockLazy.IsValueCreated)
                {
                    continue;
                }

                try
                {
                    workspaceLockLazy.Value.Dispose();
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

        await EnsureGlobalRegistryLoadedAsync(cancellationToken).ConfigureAwait(false);

        foreach (ManagedMcpServerEntry entry in _registry.Values)
        {
            if (entry.ScopeWorkingDirectory is not null || !entry.AlwaysOn)
            {
                continue;
            }

            await StartAsync(entry.Name, null, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "MCP connection manager reloaded (workspace hint: {WorkingDirectory}); global re-bootstrapped, all partitions cleared.",
            string.IsNullOrWhiteSpace(workingDirectory) ? "(empty)" : workingDirectory);
    }

    /// <inheritdoc />
    public async Task<Result> TrustWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {

            return new Error(ErrorCodes.Mcp.MissingWorkspace, "workingDirectory is required to trust a workspace-local mcp.json.");

        }

        string normalized;

        try
        {

            normalized = TrustedMcpWorkspaceStore.NormalizeWorkspaceRoot(workingDirectory);

        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {

            return new Error("Mcp.InvalidWorkspace", "workingDirectory is not a valid path.");

        }

        string mcpPath = Path.Combine(normalized, "mcp.json");

        if (!File.Exists(mcpPath))
        {

            return new Error("Mcp.MissingConfig", "Workspace mcp.json was not found.");

        }

        try
        {

            await trustedMcpWorkspaces.TrustAsync(normalized, cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Failed to trust workspace MCP config at {Workspace}.", normalized);

            return new Error("Mcp.TrustFailed", "Could not record workspace MCP approval.");

        }

        logger.LogInformation("Workspace MCP config trusted at {Workspace}.", normalized);

        return Result.Success();

    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopAllAsync(CancellationToken.None).ConfigureAwait(false);

        foreach (Lazy<McpPartitionClients> partitionLazy in _partitionClients.Values)
        {
            if (!partitionLazy.IsValueCreated)
            {
                continue;
            }

            McpPartitionClients partition = partitionLazy.Value;

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

        _registryLock.Dispose();

        foreach (Lazy<SemaphoreSlim> slimLazy in _workspaceInitLocks.Values)
        {
            if (!slimLazy.IsValueCreated)
            {
                continue;
            }

            slimLazy.Value.Dispose();
        }

        _workspaceInitLocks.Clear();

        foreach (ManagedMcpServerEntry entry in _registry.Values)
        {
            entry.Gate.Dispose();
        }

        _registry.Clear();
    }

    private async Task EnsureGlobalRegistryLoadedAsync(CancellationToken cancellationToken)
    {
        if (_globalRegistryLoaded)
        {
            return;
        }

        await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_globalRegistryLoaded)
            {
                return;
            }

            string globalPath = GetGlobalMcpConfigPath();

            if (File.Exists(globalPath))
            {
                McpConfig? config = await ReadMcpConfigAsync(globalPath, cancellationToken).ConfigureAwait(false);

                if (config?.McpServers is { Count: > 0 })
                {
                    RegisterFromConfigCore(config, scopeWorkingDirectory: null);
                }
            }

            _globalRegistryLoaded = true;
        }
        finally
        {
            _registryLock.Release();
        }
    }

    private void RegisterFromConfigCore(McpConfig config, string? scopeWorkingDirectory)
    {
        int maxServers = GetClampedMcpMaxServers();

        foreach (KeyValuePair<string, McpServerConfig> pair in config.McpServers!)
        {
            if (_registry.Count >= maxServers)
            {

                logger.LogWarning(
                    "MCP server registry at MaxServers cap ({MaxServers}); skipping remaining entries in {Scope}.",
                    maxServers,
                    scopeWorkingDirectory ?? "global");

                break;

            }

            string serverName = pair.Key;

            McpServerConfig cfg = pair.Value;

            if (string.IsNullOrWhiteSpace(cfg.Command) && string.IsNullOrWhiteSpace(cfg.Url))
            {
                logger.LogWarning(
                    "Skipping MCP server {ServerName} ({Scope}): missing command and url.",
                    serverName,
                    scopeWorkingDirectory ?? "global");

                continue;
            }

            McpServerTransport transport = InferTransport(cfg);

            (string Name, string? WorkingDirectory) key = (serverName, scopeWorkingDirectory);

            if (_registry.ContainsKey(key))
            {
                continue;
            }

            ManagedMcpServerEntry entry = new(
                serverName,
                scopeWorkingDirectory,
                cfg,
                transport,
                cfg.AlwaysOn);

            _registry[key] = entry;
        }
    }

    // W3.3 Fix 2: the count-check + TryAdd must be serialized across concurrent
    // registrations. The global path already holds _registryLock (see
    // EnsureGlobalRegistryLoadedAsync); the workspace-build path did not, so
    // parallel workspace loads could both pass the count check and overshoot
    // MaxServers. This wrapper acquires _registryLock for the entire register+
    // count-check so the cap is enforced atomically. Registration is synchronous
    // (no awaits inside RegisterFromConfigCore), so the lock is never held across
    // async work. Callers already holding _registryLock call RegisterFromConfigCore
    // directly to avoid a non-re-entrant SemaphoreSlim deadlock.
    internal async Task RegisterFromConfigAsync(McpConfig config, string? scopeWorkingDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            RegisterFromConfigCore(config, scopeWorkingDirectory);

        }
        finally
        {

            _registryLock.Release();

        }
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

            await EnsureGlobalRegistryLoadedAsync(cancellationToken).ConfigureAwait(false);

            McpPartitionClients globalPartition = GetOrCreatePartition(GlobalPartitionKey);

            List<LoadedMcpToolRow> tagged = [];

            foreach (ManagedMcpServerEntry entry in _registry.Values.Where(static e => e.ScopeWorkingDirectory is null))
            {
                if (!entry.AlwaysOn && entry.State is not McpServerState.Running)
                {
                    continue;
                }

                if (IsRestartBackoffActive(entry))
                {
                    continue;
                }

                if (entry.State is not McpServerState.Running)
                {
                    Result startResult = await StartAsync(entry.Name, null, cancellationToken).ConfigureAwait(false);

                    if (startResult.IsFailure && entry.State is not McpServerState.Running)
                    {
                        SyncPartitionServerMetadata(entry);

                        continue;
                    }
                }

                AttachEntryToPartition(entry, globalPartition, tagged);
            }

            FinalizeGlobalState(tagged);
        }
        finally
        {
            _globalInitLock.Release();
        }
    }

    private void FinalizeGlobalState(List<LoadedMcpToolRow> tagged)
    {
        Dictionary<string, LoadedMcpToolRow> byName = new(StringComparer.Ordinal);

        List<AITool> surface = [];

        foreach (LoadedMcpToolRow row in tagged)
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
        McpPartitionClients partition = GetOrCreatePartition(workspaceKey);

        List<LoadedMcpToolRow> internalTagged = await EnsurePartitionInternalToolsAsync(partition, workspaceKey, cancellationToken).ConfigureAwait(false);

        List<LoadedMcpToolRow> workspaceLocalTagged = [];

        if (workspaceKey != NoWorkspaceKey)
        {
            string localPath = Path.Combine(workspaceKey, "mcp.json");

            if (File.Exists(localPath))
            {
                McpConfig? localConfig = await ReadMcpConfigAsync(localPath, cancellationToken).ConfigureAwait(false);

                if (localConfig?.McpServers is { Count: > 0 })
                {
                    bool workspaceTrusted = await trustedMcpWorkspaces
                        .IsTrustedAsync(workspaceKey, cancellationToken)
                        .ConfigureAwait(false);

                    if (!workspaceTrusted)
                    {

                        return MergeInternalProfileAndLocal(internalTagged, workspaceLocalTagged);

                    }

                    await RegisterFromConfigAsync(localConfig, workspaceKey, cancellationToken).ConfigureAwait(false);

                    foreach (ManagedMcpServerEntry entry in _registry.Values.Where(e => e.ScopeWorkingDirectory == workspaceKey))
                    {
                        if (!entry.AlwaysOn && entry.State is not McpServerState.Running)
                        {
                            continue;
                        }

                        if (IsRestartBackoffActive(entry))
                        {
                            continue;
                        }

                        if (entry.State is not McpServerState.Running)
                        {
                            Result startResult = await StartAsync(entry.Name, workspaceKey, cancellationToken).ConfigureAwait(false);

                            if (startResult.IsFailure && entry.State is not McpServerState.Running)
                            {
                                SyncPartitionServerMetadata(entry);

                                continue;
                            }
                        }

                        AttachEntryToPartition(entry, partition, workspaceLocalTagged);
                    }
                }
            }
        }

        return MergeInternalProfileAndLocal(internalTagged, workspaceLocalTagged);
    }

    private void AttachEntryToPartition(ManagedMcpServerEntry entry, McpPartitionClients partition, List<LoadedMcpToolRow> toolsSink)
    {
        if (entry.Client is not null && !partition.Clients.Contains(entry.Client))
        {
            partition.Clients.Add(entry.Client);
        }

        foreach (LoadedMcpToolRow row in entry.LoadedTools)
        {
            toolsSink.Add(row);
        }

        SyncPartitionServerMetadata(entry);
    }

    private async Task<List<LoadedMcpToolRow>> EnsurePartitionInternalToolsAsync(
        McpPartitionClients partition,
        string workspaceKey,
        CancellationToken cancellationToken)
    {
        if (partition.InternalServerStarted && partition.CachedInternalTools is { } cached)
        {
            return new List<LoadedMcpToolRow>(cached);
        }

        List<LoadedMcpToolRow> internalTagged = [];

        await StartInternalInProcessServerForPartitionAsync(
                partition,
                workspaceKey,
                internalTagged,
                cancellationToken)
            .ConfigureAwait(false);

        partition.InternalServerStarted = true;

        partition.CachedInternalTools = new List<LoadedMcpToolRow>(internalTagged);

        return internalTagged;
    }

    private IReadOnlyList<AITool> MergeInternalProfileAndLocal(
        IReadOnlyList<LoadedMcpToolRow> internalTagged,
        IReadOnlyList<LoadedMcpToolRow> workspaceLocalTagged)
    {
        List<AITool> surface = [];

        Dictionary<string, LoadedMcpToolRow> mergedByName = new(StringComparer.Ordinal);

        foreach (LoadedMcpToolRow row in internalTagged)
        {
            if (mergedByName.TryAdd(row.Tool.Name, row))
            {
                surface.Add(row.Tool);
            }
        }

        foreach (KeyValuePair<string, LoadedMcpToolRow> kv in _globalFirstByToolName)
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
        IReadOnlyDictionary<string, LoadedMcpToolRow> globalByName,
        IReadOnlyList<LoadedMcpToolRow> localTagged)
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

        foreach (LoadedMcpToolRow localRow in localTagged)
        {
            string name = localRow.Tool.Name;

            if (!indexByName.TryGetValue(name, out int idx))
            {
                indexByName[name] = merged.Count;

                merged.Add(localRow.Tool);

                continue;
            }

            if (!globalByName.TryGetValue(name, out LoadedMcpToolRow globalRow)
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
                localRow.Tool.ToolOutputCapBytes,
                fallbackClient: globalRow.Client,
                fallbackLogger: logger);

            merged[idx] = replacement;
        }

        return merged;
    }

    /// <summary>
    /// Decides whether to strip the inherited host environment before spawning an MCP server
    /// subprocess. Secure default: strip for ALL servers — global (modeled as
    /// <c>ScopeWorkingDirectory == null</c>) and workspace-scoped alike — so secrets such as the
    /// <c>ARCANUM_*</c> provider API keys never leak into child processes. A per-server opt-in to
    /// inherit the host environment is a deliberate follow-up; <see cref="McpServerConfig"/> has no
    /// such field yet.
    /// </summary>
    internal static bool ShouldStripUserEnvironment(McpServerConfig cfg)
    {

        ArgumentNullException.ThrowIfNull(cfg);

        return true;

    }

    // W-MCP-HTTP: an stdio server may opt specific host variables back in via `inheritEnv` (e.g.
    // PATH/HOME for npx). Names are matched case-insensitively so the deny-list bypass works on
    // either casing; the host lookup uses the operator-provided name verbatim. Returns null when
    // nothing is opted in so the secure strip-everything default is preserved.
    internal static IReadOnlySet<string>? BuildInheritEnvironmentAllowlist(string[]? inheritEnv)
    {

        if (inheritEnv is not { Length: > 0 })
        {

            return null;

        }

        HashSet<string> allowlist = new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in inheritEnv)
        {

            if (!string.IsNullOrWhiteSpace(name))
            {

                allowlist.Add(name.Trim());

            }

        }

        return allowlist.Count == 0 ? null : allowlist;

    }

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

    private async Task<bool> IsWorkspaceServerVisibleAsync(
        ManagedMcpServerEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.ScopeWorkingDirectory is null)
        {
            return true;
        }

        return await trustedMcpWorkspaces
            .IsTrustedAsync(entry.ScopeWorkingDirectory, cancellationToken)
            .ConfigureAwait(false);
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

                if (!ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, resolved, out _))
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

    private Result<ManagedMcpServerEntry> ResolveEntry(string name, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new Error("Mcp.InvalidName", "Server name is required.");
        }

        string? normalizedScope = NormalizeScopeWorkingDirectory(workingDirectory);

        if (normalizedScope is not null || !string.IsNullOrWhiteSpace(workingDirectory))
        {
            if (normalizedScope is null && !string.IsNullOrWhiteSpace(workingDirectory))
            {
                return new Error("Mcp.InvalidWorkingDirectory", "The working directory could not be resolved.");
            }

            (string Name, string? WorkingDirectory) key = (name, normalizedScope);

            if (_registry.TryGetValue(key, out ManagedMcpServerEntry? exact))
            {
                return exact;
            }

            return new Error("Mcp.NotFound", $"MCP server '{name}' was not found.");
        }

        List<ManagedMcpServerEntry> matches = _registry.Values
            .Where(e => string.Equals(e.Name, name, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count == 0)
        {
            return new Error("Mcp.NotFound", $"MCP server '{name}' was not found.");
        }

        return new Error(ErrorCodes.Mcp.AmbiguousServer, $"Multiple MCP servers named '{name}' exist; specify workingDirectory.");
    }

    private static string? NormalizeScopeWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(workingDirectory.Trim());
        }
        catch (Exception)
        {
            return null;
        }
    }

    // W-MCP-HTTP: an explicit `type` wins; otherwise a configured `url` implies the Streamable
    // HTTP transport (2026-07-28) and a bare command implies stdio. An explicit `type: "sse"`
    // selects the legacy SSE transport, which remains unsupported. Unknown `type` values fall
    // back to URL inference so a hand-edited config still resolves to a usable transport.
    internal static McpServerTransport InferTransport(McpServerConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        if (!string.IsNullOrWhiteSpace(cfg.Type))
        {
            if (string.Equals(cfg.Type, "stdio", StringComparison.OrdinalIgnoreCase))
            {
                return McpServerTransport.Stdio;
            }

            if (string.Equals(cfg.Type, "http", StringComparison.OrdinalIgnoreCase))
            {
                return McpServerTransport.Http;
            }

            if (string.Equals(cfg.Type, "sse", StringComparison.OrdinalIgnoreCase))
            {
                return McpServerTransport.Sse;
            }
        }

        if (!string.IsNullOrWhiteSpace(cfg.Url))
        {
            return McpServerTransport.Http;
        }

        return McpServerTransport.Stdio;
    }

    private static McpServerInfo ToInfo(ManagedMcpServerEntry entry)
    {
        return new McpServerInfo(
            entry.Name,
            entry.ScopeWorkingDirectory,
            entry.Transport,
            entry.AlwaysOn,
            entry.Config.Command,
            entry.Config.Args ?? [],
            entry.Config.Url,
            entry.State,
            entry.ErrorMessage,
            entry.Tools,
            entry.LastConnectedAt);
    }

    private static McpServerEvent BuildEvent(
        ManagedMcpServerEntry entry,
        McpServerState state,
        string? message,
        string[] tools)
    {
        return new McpServerEvent(DateTimeOffset.UtcNow)
        {
            ServerName = entry.Name,
            State = state,
            Message = message,
            Tools = tools,
        };
    }

    private void PublishEvent(McpServerEvent ev)
    {
        eventBus.Publish(ev);
    }

    private void InvalidateCachesForServer(ManagedMcpServerEntry entry)
    {
        _mergedToolsByWorkspace.Clear();

        if (entry.ScopeWorkingDirectory is null)
        {
            _globalInitialized = false;
        }
    }

    private void SyncPartitionServerMetadata(ManagedMcpServerEntry entry)
    {
        string partitionKey = entry.ScopeWorkingDirectory is null ? GlobalPartitionKey : entry.ScopeWorkingDirectory;

        McpPartitionClients partition = GetOrCreatePartition(partitionKey);

        string status = entry.State switch
        {
            McpServerState.Running => "Online",
            McpServerState.Error => "Failed",
            McpServerState.Stopped => "Stopped",
            McpServerState.Starting => "Starting",
            McpServerState.Restarting => "Restarting",
            _ => "Stopped",
        };

        McpServerMetadata metadata = new(
            entry.Name,
            status,
            entry.Tools.ToList(),
            entry.ErrorMessage);

        int existingIndex = partition.Servers.FindIndex(m => string.Equals(m.ServerName, entry.Name, StringComparison.Ordinal));

        if (existingIndex >= 0)
        {
            partition.Servers[existingIndex] = metadata;
        }
        else
        {
            partition.Servers.Add(metadata);
        }
    }

    private void RemoveServerMetadataFromPartition(ManagedMcpServerEntry entry)
    {
        string partitionKey = entry.ScopeWorkingDirectory is null ? GlobalPartitionKey : entry.ScopeWorkingDirectory;

        if (!_partitionClients.TryGetValue(partitionKey, out Lazy<McpPartitionClients>? partitionLazy)
            || !partitionLazy.IsValueCreated)
        {
            return;
        }

        partitionLazy.Value.Servers.RemoveAll(m => string.Equals(m.ServerName, entry.Name, StringComparison.Ordinal));
    }

    private McpPartitionClients GetOrCreatePartition(string partitionKey)
    {
        return _partitionClients
            .GetOrAdd(
                partitionKey,
                static _ => new Lazy<McpPartitionClients>(
                    static () => new McpPartitionClients(),
                    LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    private sealed class McpPartitionClients
    {

        public List<IMcpClient> Clients { get; } = [];

        public List<McpServerMetadata> Servers { get; } = [];

        public bool InternalServerStarted { get; set; }

        public IReadOnlyList<LoadedMcpToolRow>? CachedInternalTools { get; set; }

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
        int executeSeconds = Math.Clamp(settings.CurrentValue.Intelligence.ExecuteCommandTimeoutSeconds, 1, 600);

        int requestSeconds = ArcanumSettingClamps.McpRequestTimeoutSeconds(
            settings.CurrentValue.Mcp.RequestTimeoutSeconds);

        return Math.Min(executeSeconds, requestSeconds);
    }

    private TimeSpan GetClampedMcpRequestTimeout()
    {
        return TimeSpan.FromSeconds(
            ArcanumSettingClamps.McpRequestTimeoutSeconds(settings.CurrentValue.Mcp.RequestTimeoutSeconds));
    }

    private int GetClampedMcpMaxPaginationPages()
    {
        return ArcanumSettingClamps.McpMaxPaginationPages(settings.CurrentValue.Mcp.MaxPaginationPages);
    }

    private int GetClampedMcpMaxServers()
    {

        return ArcanumSettingClamps.McpMaxServers(settings.CurrentValue.Mcp.MaxServers);

    }

    private int GetClampedMcpMaxToolsPerServer()
    {

        return ArcanumSettingClamps.McpMaxToolsPerServer(settings.CurrentValue.Mcp.MaxToolsPerServer);

    }

    private int GetClampedMcpMaxToolsPerListPage()
    {

        return ArcanumSettingClamps.McpMaxToolsPerListPage(settings.CurrentValue.Mcp.MaxToolsPerListPage);

    }

    private int GetClampedMcpMaxToolsTotalBytes()
    {

        return ArcanumSettingClamps.McpMaxToolsTotalBytes(settings.CurrentValue.Mcp.MaxToolsTotalBytes);

    }

    private int GetClampedMcpMaxJsonRpcLineBytes()
    {

        return ArcanumSettingClamps.McpMaxJsonRpcLineBytes(settings.CurrentValue.Mcp.MaxJsonRpcLineBytes);

    }

    private McpClient CreateMcpClient(
        IMcpTransport transport,
        McpRequestCancellationBroker? requestCancellationBroker = null)
    {

        return new McpClient(
            transport,
            GetClampedMcpRequestTimeout(),
            GetClampedMcpMaxPaginationPages(),
            GetClampedToolOutputCapBytes(),
            GetClampedMcpMaxToolsPerServer(),
            GetClampedMcpMaxToolsPerListPage(),
            GetClampedMcpMaxToolsTotalBytes(),
            requestCancellationBroker);

    }

    private TimeSpan GetClampedMcpHttpRequestTimeout()
    {

        return TimeSpan.FromSeconds(
            ArcanumSettingClamps.McpHttpRequestTimeoutSeconds(settings.CurrentValue.Mcp.HttpRequestTimeoutSeconds));

    }

    private McpHttpClient CreateHttpMcpClient(Uri endpoint)
    {

        HttpClient httpClient = httpClientFactory.CreateClient(McpHttpClientName);

        return new McpHttpClient(
            endpoint,
            httpClient,
            GetClampedMcpRequestTimeout(),
            GetClampedMcpMaxPaginationPages(),
            GetClampedToolOutputCapBytes(),
            GetClampedMcpMaxToolsPerServer(),
            GetClampedMcpMaxToolsPerListPage(),
            GetClampedMcpMaxToolsTotalBytes(),
            GetClampedMcpMaxJsonRpcLineBytes(),
            _httpInputElicitor,
            logger);

    }

    // W-MCP-HTTP: validates a Streamable HTTP endpoint before connecting. The URL must be an
    // absolute http/https URI; plaintext http is refused unless the host is in
    // Arcanum:Mcp:AllowedHttpHosts; and the SSRF policy (loopback / private / link-local blocking
    // with DNS-rebind pinning) is enforced up front via OutboundUrlGuard and again at connect time
    // by the named client's egress handler.
    private async Task<Result<Uri>> ResolveValidatedHttpEndpointAsync(McpServerConfig cfg, CancellationToken cancellationToken)
    {

        string? url = cfg.Url;

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? endpoint))
        {

            return Result<Uri>.Failure(new Error("Mcp.InvalidUrl", "MCP HTTP server requires an absolute http or https url."));

        }

        if (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
        {

            return Result<Uri>.Failure(new Error("Mcp.InvalidUrl", "MCP HTTP server url must use the http or https scheme."));

        }

        if (endpoint.Scheme == Uri.UriSchemeHttp && !IsHttpHostAllowed(endpoint.Host))
        {

            return Result<Uri>.Failure(new Error(
                "Mcp.InsecureUrl",
                $"Plaintext http MCP server '{endpoint.Host}' requires the host in Arcanum:Mcp:AllowedHttpHosts; otherwise use https."));

        }

        Result outbound = await OutboundUrlGuard.ValidateUntrustedUrlAsync(url, cancellationToken).ConfigureAwait(false);

        if (outbound.IsFailure)
        {

            return Result<Uri>.Failure(new Error("Mcp.BlockedUrl", outbound.Error.Message));

        }

        return Result<Uri>.Success(endpoint);

    }

    private bool IsHttpHostAllowed(string host)
    {

        string[] allowed = settings.CurrentValue.Mcp.AllowedHttpHosts ?? [];

        foreach (string candidate in allowed)
        {

            if (!string.IsNullOrWhiteSpace(candidate)
                && string.Equals(host, candidate.Trim(), StringComparison.OrdinalIgnoreCase))
            {

                return true;

            }

        }

        return false;

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

    private async Task<McpConfig?> ReadMcpConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        try
        {
            FileInfo fileInfo = new(configPath);

            if (fileInfo.Exists && fileInfo.Length > MaxMcpConfigBytes)
            {

                logger.LogError(
                    "MCP config at {ConfigPath} exceeds the maximum size of {MaxBytes} bytes.",
                    configPath,
                    MaxMcpConfigBytes);

                return null;

            }

            byte[] utf8 = await File.ReadAllBytesAsync(configPath, cancellationToken).ConfigureAwait(false);

            if (utf8.Length > MaxMcpConfigBytes)
            {

                logger.LogError(
                    "MCP config at {ConfigPath} exceeds the maximum size of {MaxBytes} bytes.",
                    configPath,
                    MaxMcpConfigBytes);

                return null;

            }

            return System.Text.Json.JsonSerializer.Deserialize(utf8, McpConfigJsonSerializerContext.Default.McpConfig);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read or parse MCP config at {ConfigPath}.", configPath);

            return null;
        }
    }

}
