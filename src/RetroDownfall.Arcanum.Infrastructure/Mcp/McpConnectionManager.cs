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
public sealed partial class McpConnectionManager(
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

}
