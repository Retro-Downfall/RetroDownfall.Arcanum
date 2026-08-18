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

    private static readonly TimeSpan WorkspaceRetirementCleanupTimeout =
        TimeSpan.FromSeconds(30);

    private static readonly McpServerConfig InternalMcpServerConfig = new() { Command = "arcanum-internal" };

    private readonly SemaphoreSlim _globalInitLock = new(1, 1);

    private readonly SemaphoreSlim _registryLock = new(1, 1);

    private readonly ConcurrentDictionary<string, Lazy<SemaphoreSlim>> _workspaceInitLocks = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, CachedMcpToolSurface>
        _mergedToolsByWorkspace = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Lazy<McpPartitionClients>> _partitionClients = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<(string Name, string? WorkingDirectory), ManagedMcpServerEntry> _registry = new();

    /// <summary>
    /// Tracks in-flight <see cref="HandleTransportEnded"/> background tasks (fire-and-forget from the
    /// transport's <c>OnTransportEnded</c> callback) so <see cref="DisposeAsync"/> can await them
    /// before tearing down <see cref="ManagedMcpServerEntry.Gate"/> and the registry — otherwise a
    /// handler still mid-flight could acquire an about-to-be-disposed gate, mutate an entry, or
    /// publish an event concurrently with shutdown. Self-removing on completion to avoid unbounded
    /// growth across the connection manager's lifetime.
    /// </summary>
    private readonly ConcurrentDictionary<Task, byte> _pendingTransportEndedTasks = new();

    private readonly ConcurrentDictionary<Task, byte>
        _retiredPartitionDisposals = new();

    private readonly ConcurrentDictionary<Task, byte>
        _pendingWorkspaceRetirements = new();

    private readonly ConcurrentDictionary<string, WorkspaceRetirementState>
        _workspaceRetirementStates = new(StringComparer.Ordinal);

    private readonly object _internalSettingsCacheGate = new();

    private string _cachedInternalToolSettingsFingerprint =
        InternalCodingToolSettingsFingerprint.Build(
            settings.CurrentValue.ResolveCodingTools());

    private long _toolSurfaceGeneration;

    private readonly WorkspaceCheckSettings _defaultWorkspaceCheckSettings =
        new();

    private bool _globalInitialized;

    private bool _globalRegistryLoaded;

    /// <summary>
    /// Shared global bootstrap / surface-build operation so concurrent
    /// <see cref="InitializeAsync"/> / <see cref="EnsureGlobalLoadedAsync"/> callers observe one
    /// initialization and do not both call <c>StartAsync</c> for the same AlwaysOn server outside
    /// the per-entry gate race window.
    /// </summary>
    private Task? _globalInitOperation;

    private Dictionary<string, LoadedMcpToolRow> _globalFirstByToolName = new(StringComparer.Ordinal);

    private IReadOnlyList<AITool> _globalSurfaceTools = [];

    private volatile bool _disposed;

    private readonly CancellationTokenSource _hostLifetimeCts = new();

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // BootstrapBlocksStartup awaits this before Kestrel accepts requests. Completing global
        // load (AlwaysOn starts + surface attach) preserves that contract while sharing one
        // in-flight operation across concurrent callers.
        return EnsureGlobalLoadedAsync(cancellationToken);
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

        List<McpServerEvent> pendingEvents = [];

        Result result;

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            try
            {
                if (!IsCurrentRegistryEntry(entry))
                {
                    result = EntryNotFoundError(entry);
                }
                else if (!await IsWorkspaceServerVisibleAsync(entry, cancellationToken).ConfigureAwait(false))
                {
                    result = IsCurrentRegistryEntry(entry)
                        ? WorkspaceNotTrustedError()
                        : EntryNotFoundError(entry);
                }
                else if (entry.State is McpServerState.Running or McpServerState.Starting)
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

                    Result startResult = await StartManagedServerWithCancellationHandlingAsync(entry, pendingEvents, cancellationToken)
                        .ConfigureAwait(false);

                    if (startResult.IsFailure)
                    {
                        entry.State = McpServerState.Error;

                        entry.ErrorMessage = startResult.Error.Message;

                        ScheduleRestartBackoff(entry);

                        pendingEvents.Add(BuildEvent(entry, McpServerState.Error, entry.ErrorMessage, []));

                        result = startResult;
                    }
                    else if (!await IsWorkspaceServerVisibleAsync(
                                      entry,
                                      cancellationToken)
                                  .ConfigureAwait(false))
                    {
                        _ = await StopManagedServerCoreAsync(
                                entry)
                            .ConfigureAwait(false);

                        Error notTrusted = IsCurrentRegistryEntry(entry)
                            ? WorkspaceNotTrustedError()
                            : EntryNotFoundError(entry);

                        entry.State = McpServerState.Error;

                        entry.Tools = [];

                        entry.ErrorMessage = notTrusted.Message;

                        pendingEvents.Add(BuildEvent(
                            entry,
                            McpServerState.Error,
                            entry.ErrorMessage,
                            []));

                        result = notTrusted;
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
        }
        finally
        {
            // A finally (rather than a plain statement after the try) so a canceled start still
            // publishes the Error-state event queued by StartManagedServerWithCancellationHandlingAsync
            // before the OperationCanceledException propagates to the caller.
            foreach (McpServerEvent ev in pendingEvents)
            {
                PublishEvent(ev);
            }
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
                bool disposalCompleted =
                    await StopManagedServerCoreAsync(entry)
                        .ConfigureAwait(false);

                entry.State = McpServerState.Stopped;

                entry.Tools = [];

                entry.ErrorMessage = null;

                pendingEvent = BuildEvent(entry, McpServerState.Stopped, null, []);

                InvalidateCachesForServer(entry);

                RemoveServerMetadataFromPartition(entry);

                cancellationToken.ThrowIfCancellationRequested();

                result = disposalCompleted
                    ? Result.Success()
                    : new Error(
                        "Mcp.ClientDisposalIncomplete",
                        "The MCP client is still shutting down.");
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
            try
            {
                if (!IsCurrentRegistryEntry(entry))
                {
                    result = EntryNotFoundError(entry);
                }
                else
                {
                    entry.State = McpServerState.Restarting;

                    pendingEvents.Add(BuildEvent(entry, McpServerState.Restarting, null, []));

                    bool disposalCompleted =
                        await StopManagedServerCoreAsync(entry)
                            .ConfigureAwait(false);

                    entry.State = McpServerState.Stopped;

                    entry.Tools = [];

                    entry.ErrorMessage = null;

                    pendingEvents.Add(BuildEvent(entry, McpServerState.Stopped, null, []));

                    InvalidateCachesForServer(entry);

                    RemoveServerMetadataFromPartition(entry);

                    cancellationToken.ThrowIfCancellationRequested();

                    if (!disposalCompleted)
                    {
                        entry.State = McpServerState.Error;

                        entry.ErrorMessage =
                            "The previous MCP client is still shutting down.";

                        pendingEvents.Add(BuildEvent(
                            entry,
                            McpServerState.Error,
                            entry.ErrorMessage,
                            []));

                        result = new Error(
                            "Mcp.ClientDisposalIncomplete",
                            entry.ErrorMessage);
                    }
                    else if (entry.Transport is McpServerTransport.Sse)
                    {
                        entry.State = McpServerState.Error;

                        entry.ErrorMessage = "SSE transport is not yet supported.";

                        pendingEvents.Add(BuildEvent(entry, McpServerState.Error, entry.ErrorMessage, []));

                        result = new Error("Mcp.SseNotSupported", entry.ErrorMessage);
                    }
                    else if (!await IsWorkspaceServerVisibleAsync(
                                 entry,
                                 cancellationToken)
                             .ConfigureAwait(false))
                    {
                        Error notTrusted = IsCurrentRegistryEntry(entry)
                            ? WorkspaceNotTrustedError()
                            : EntryNotFoundError(entry);

                        entry.State = McpServerState.Error;

                        entry.ErrorMessage = notTrusted.Message;

                        pendingEvents.Add(BuildEvent(entry, McpServerState.Error, entry.ErrorMessage, []));

                        result = notTrusted;
                    }
                    else
                    {
                        entry.State = McpServerState.Starting;

                        pendingEvents.Add(BuildEvent(entry, McpServerState.Starting, null, []));

                        Result startResult = await StartManagedServerWithCancellationHandlingAsync(entry, pendingEvents, cancellationToken)
                            .ConfigureAwait(false);

                        if (startResult.IsFailure)
                        {
                            entry.State = McpServerState.Error;

                            entry.ErrorMessage = startResult.Error.Message;

                            ScheduleRestartBackoff(entry);

                            pendingEvents.Add(BuildEvent(entry, McpServerState.Error, entry.ErrorMessage, []));

                            result = startResult;
                        }
                        else if (!await IsWorkspaceServerVisibleAsync(
                                          entry,
                                          cancellationToken)
                                      .ConfigureAwait(false))
                        {
                            _ = await StopManagedServerCoreAsync(
                                    entry)
                                .ConfigureAwait(false);

                            Error notTrusted = IsCurrentRegistryEntry(entry)
                                ? WorkspaceNotTrustedError()
                                : EntryNotFoundError(entry);

                            entry.State = McpServerState.Error;

                            entry.Tools = [];

                            entry.ErrorMessage = notTrusted.Message;

                            pendingEvents.Add(BuildEvent(
                                entry,
                                McpServerState.Error,
                                entry.ErrorMessage,
                                []));

                            result = notTrusted;
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
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        finally
        {
            // A finally (rather than a plain statement after the try) so a canceled restart still
            // publishes the Error-state event queued by StartManagedServerWithCancellationHandlingAsync
            // before the OperationCanceledException propagates to the caller.
            foreach (McpServerEvent ev in pendingEvents)
            {
                PublishEvent(ev);
            }
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
        Dictionary<string, TrustedMcpWorkspaceSnapshot> snapshots =
            new(StringComparer.Ordinal);

        foreach (ManagedMcpServerEntry entry in _registry.Values
                     .OrderBy(static e => e.ScopeWorkingDirectory ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(static e => e.Name, StringComparer.Ordinal))
        {
            if (!IsCurrentRegistryEntry(entry))
            {
                continue;
            }

            if (entry.ScopeWorkingDirectory is not null)
            {
                if (!snapshots.TryGetValue(
                        entry.ScopeWorkingDirectory,
                        out TrustedMcpWorkspaceSnapshot snapshot))
                {
                    snapshot = await trustedMcpWorkspaces
                        .GetSnapshotAsync(
                            entry.ScopeWorkingDirectory,
                            cancellationToken)
                        .ConfigureAwait(false);
                    snapshots[entry.ScopeWorkingDirectory] = snapshot;
                }

                if (!snapshot.Authorizes(entry.SourceDigest))
                {
                    continue;
                }
            }

            statuses.Add(ToInfo(entry));
        }

        return statuses.ToArray();
    }

    /// <inheritdoc />
    public async Task<AIFunction?> GetToolAsync(
        string serverName,
        string toolName,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        Result<ManagedMcpServerEntry> resolved = ResolveEntry(serverName.Trim(), workingDirectory);

        if (resolved.IsFailure)
        {
            return null;
        }

        ManagedMcpServerEntry entry = resolved.Value;

        if (!await IsWorkspaceServerVisibleAsync(entry, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (entry.State is not McpServerState.Running)
        {
            return null;
        }

        string normalizedTool = toolName.Trim();

        foreach (LoadedMcpToolRow row in entry.LoadedTools)
        {
            if (string.Equals(row.Tool.Name, normalizedTool, StringComparison.Ordinal))
            {
                return row.Tool;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(string? workingDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string workspaceKey = NormalizeWorkspaceKey(workingDirectory);

        while (true)
        {
            InvalidateInternalToolCachesForSettingsChange();
            long generation = Volatile.Read(
                ref _toolSurfaceGeneration);

            if (_mergedToolsByWorkspace.TryGetValue(
                    workspaceKey,
                    out CachedMcpToolSurface? cached)
                && cached.Generation == generation
                && await IsCachedSurfaceCurrentAsync(
                        workspaceKey,
                        cached,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return cached.Tools;
            }

            await EnsureGlobalLoadedAsync(cancellationToken)
                .ConfigureAwait(false);

            SemaphoreSlim workspaceLock = _workspaceInitLocks
                .GetOrAdd(
                    workspaceKey,
                    static _ => new Lazy<SemaphoreSlim>(
                        static () => new SemaphoreSlim(1, 1),
                        LazyThreadSafetyMode.ExecutionAndPublication))
                .Value;

            await workspaceLock.WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (generation != Volatile.Read(
                        ref _toolSurfaceGeneration))
                {
                    continue;
                }

                if (_mergedToolsByWorkspace.TryGetValue(
                        workspaceKey,
                        out cached)
                    && cached.Generation == generation
                    && await IsCachedSurfaceCurrentAsync(
                            workspaceKey,
                            cached,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    return cached.Tools;
                }

                McpPartitionClients partition =
                    GetOrCreatePartition(workspaceKey);
                BuiltMcpToolSurface built;

                try
                {
                    built =
                        await BuildMergedToolsForWorkspaceAsync(
                                partition,
                                workspaceKey,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (McpTransportUnavailableException)
                    when (generation != Volatile.Read(
                        ref _toolSurfaceGeneration))
                {
                    UnregisterPartition(
                        workspaceKey,
                        partition);
                    TrackRetiredPartitionDisposal(
                        partition);
                    continue;
                }

                if (generation != Volatile.Read(
                        ref _toolSurfaceGeneration))
                {
                    UnregisterPartition(workspaceKey, partition);
                    TrackRetiredPartitionDisposal(partition);
                    continue;
                }

                if (built.Cacheable)
                {
                    _mergedToolsByWorkspace[workspaceKey] =
                        new CachedMcpToolSurface(
                            generation,
                            built.SourceDigest,
                            built.Tools);
                }

                return built.Tools;
            }
            finally
            {
                workspaceLock.Release();
            }
        }
    }

    private async Task<bool> IsCachedSurfaceCurrentAsync(
        string workspaceKey,
        CachedMcpToolSurface cached,
        CancellationToken cancellationToken)
    {
        if (workspaceKey == NoWorkspaceKey)
        {
            return true;
        }

        TrustedMcpWorkspaceSnapshot snapshot = await trustedMcpWorkspaces
            .GetSnapshotAsync(workspaceKey, cancellationToken)
            .ConfigureAwait(false);

        bool current = snapshot.Authorizes(cached.SourceDigest);

        if (!current)
        {
            _mergedToolsByWorkspace.TryRemove(workspaceKey, out _);
        }

        return current;
    }

    private void InvalidateInternalToolCachesForSettingsChange()
    {

        string fingerprint =
            InternalCodingToolSettingsFingerprint.Build(
                settings.CurrentValue.ResolveCodingTools());

        List<McpPartitionClients> retired = [];

        lock (_internalSettingsCacheGate)
        {

            if (string.Equals(
                    fingerprint,
                    _cachedInternalToolSettingsFingerprint,
                    StringComparison.Ordinal))
            {

                return;
            }

            _cachedInternalToolSettingsFingerprint = fingerprint;
            _ = Interlocked.Increment(
                ref _toolSurfaceGeneration);
            _mergedToolsByWorkspace.Clear();

            foreach (string key in _partitionClients.Keys.ToArray())
            {
                if (string.Equals(
                        key,
                        GlobalPartitionKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!_partitionClients.TryRemove(
                        key,
                        out Lazy<McpPartitionClients>? removed)
                    || !removed.IsValueCreated)
                {
                    continue;
                }

                retired.Add(removed.Value);
            }
        }

        foreach (McpPartitionClients partition in retired)
        {
            TrackRetiredPartitionDisposal(partition);
        }
    }

    private void TrackRetiredPartitionDisposal(
        McpPartitionClients partition)
    {
        Task disposal = DisposeRetiredPartitionAsync(partition);
        _retiredPartitionDisposals[disposal] = 0;
        _ = disposal.ContinueWith(
            completed =>
            {
                _ = _retiredPartitionDisposals.TryRemove(
                    completed,
                    out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DisposeRetiredPartitionAsync(
        McpPartitionClients partition)
    {
        if (partition.InternalClient is not { } client)
        {
            return;
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to dispose an MCP partition retired after internal coding-tool settings changed.");
        }
    }

    /// <summary>
    /// Disposes the client generations a reload retired, one bounded wait at a time. A generation
    /// only finishes draining when its last in-flight call returns, and the calls
    /// <c>McpBridgeTool.RunsUntilCallerCancellation</c> classifies carry no clock at all, so an
    /// unbounded await here would tie the reload to an operator answering a prompt. Each disposal is
    /// tracked before it is awaited so a wait that times out abandons the task to shutdown rather
    /// than dropping a still-draining generation on the floor.
    /// </summary>
    private async Task DisposeRetiredReloadClientsAsync(
        IReadOnlyList<IMcpClient> retiredClients)
    {
        foreach (IMcpClient client in retiredClients)
        {
            Task disposal;

            try
            {
                disposal = client.DisposeAsync().AsTask();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error disposing MCP client instance during reload.");

                continue;
            }

            TrackPendingWorkspaceRetirement(disposal);

            using CancellationTokenSource cleanupDeadline =
                new(WorkspaceRetirementCleanupTimeout);

            try
            {
                await disposal
                    .WaitAsync(cleanupDeadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cleanupDeadline.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Timed out draining a retired MCP client generation during reload; the drain continues in the background.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error disposing MCP client instance during reload.");
            }
        }
    }

    private async Task AwaitRetiredPartitionDisposalsAsync()
    {
        Task[] pending =
            _retiredPartitionDisposals.Keys.ToArray();

        if (pending.Length == 0)
        {
            return;
        }

        using CancellationTokenSource cleanupDeadline =
            new(WorkspaceRetirementCleanupTimeout);

        try
        {
            await Task.WhenAll(pending)
                .WaitAsync(cleanupDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cleanupDeadline.IsCancellationRequested)
        {
            logger.LogWarning(
                "Timed out awaiting {PendingCount} retired MCP client generation(s); the drain continues in the background.",
                pending.Length);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed while awaiting retired MCP client generations.");
        }
    }

    private async Task AwaitPendingWorkspaceRetirementsAsync()
    {
        using CancellationTokenSource cleanupDeadline =
            new(WorkspaceRetirementCleanupTimeout);

        while (true)
        {
            Task[] pending =
                _pendingWorkspaceRetirements.Keys.ToArray();

            if (pending.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(pending)
                    .WaitAsync(cleanupDeadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cleanupDeadline.IsCancellationRequested)
            {
                logger.LogWarning(
                    "MCP shutdown left {PendingCount} bounded workspace retirement task(s) unfinished.",
                    _pendingWorkspaceRetirements.Count);

                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed while awaiting a retired MCP workspace server.");
            }
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
            foreach (McpServerMetadata meta in globalPartitionLazy.Value.SnapshotServers())
            {
                result.Add(ToStatusDto(meta));
            }
        }

        string workspaceKey = NormalizeWorkspaceKey(workingDirectory);
        _mergedToolsByWorkspace.TryGetValue(
            workspaceKey,
            out CachedMcpToolSurface? currentSurface);

        if (_partitionClients.TryGetValue(workspaceKey, out Lazy<McpPartitionClients>? workspacePartitionLazy)
            && workspacePartitionLazy.IsValueCreated)
        {
            foreach (McpServerMetadata meta in workspacePartitionLazy.Value.SnapshotServers())
            {
                if (string.Equals(
                        meta.ServerName,
                        "arcanum-internal",
                        StringComparison.Ordinal))
                {
                    result.Add(ToStatusDto(meta));

                    continue;
                }

                if (_registry.TryGetValue(
                        (meta.ServerName, workspaceKey),
                        out ManagedMcpServerEntry? entry)
                    && IsCurrentRegistryEntry(entry)
                    && currentSurface is not null
                    && string.Equals(
                        currentSurface.SourceDigest,
                        entry.SourceDigest,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(ToStatusDto(meta));
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<IMcpClient> retiredClients = [];

        await _globalInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _ = Interlocked.Increment(
                ref _toolSurfaceGeneration);

            foreach (ManagedMcpServerEntry entry in _registry.Values.ToArray())
            {
                await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    _ = await StopManagedServerCoreAsync(entry)
                        .ConfigureAwait(false);

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

            // The retired clients are collected here but disposed after the lock is released and
            // after every cached surface has been dropped. Draining a generation can take as long as
            // its longest in-flight call — ask_human and execute_command run with
            // Timeout.InfiniteTimeSpan — so disposing under _globalInitLock, or before the caches
            // are cleared, would leave the whole tool surface pinned to the retired generation (and
            // _globalInitLock held) for the duration of an operator-answered prompt.
            foreach (KeyValuePair<string, Lazy<McpPartitionClients>> partitionEntry in _partitionClients.ToArray())
            {
                if (!_partitionClients.TryRemove(partitionEntry))
                {
                    continue;
                }

                if (!partitionEntry.Value.IsValueCreated)
                {
                    continue;
                }

                retiredClients.AddRange(partitionEntry.Value.Value.DrainClients());
            }

            _mergedToolsByWorkspace.Clear();

            _globalInitialized = false;

            _globalInitOperation = null;

            _globalFirstByToolName = new(StringComparer.Ordinal);

            _globalSurfaceTools = [];
        }
        finally
        {
            _globalInitLock.Release();
        }

        await DisposeRetiredReloadClientsAsync(retiredClients)
            .ConfigureAwait(false);

        await AwaitRetiredPartitionDisposalsAsync()
            .ConfigureAwait(false);

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

        try
        {
            await _hostLifetimeCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        // Awaited before StopAllAsync/gate disposal below: a HandleTransportEnded background task
        // that was already past its own _disposed check when the flag flipped above could otherwise
        // still be mid-flight — acquiring entry.Gate, mutating entry.Client, or publishing an event —
        // concurrently with this method disposing that same gate and clearing the registry.
        Task[] pendingTransportEndedTasks = _pendingTransportEndedTasks.Keys.ToArray();

        if (pendingTransportEndedTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(pendingTransportEndedTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error awaiting in-flight transport-ended handler(s) during shutdown.");
            }
        }

        await AwaitPendingWorkspaceRetirementsAsync()
            .ConfigureAwait(false);

        // Not StopAllAsync: _disposed is already set above, so that entry point short-circuits. A
        // client only reaches a partition when a surface build attaches it, so an entry started
        // through StartAsync and never used for inference would otherwise keep its stdio child
        // alive past shutdown. StopManagedServerCoreAsync also unregisters from the partition, so
        // the drain below stays correct for everything it does cover.
        foreach (ManagedMcpServerEntry entry in _registry.Values)
        {
            if (entry.Client is null)
            {
                continue;
            }

            try
            {
                _ = await StopManagedServerCoreAsync(entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error stopping MCP server {ServerName} during shutdown.", entry.Name);
            }
        }

        foreach (Lazy<McpPartitionClients> partitionLazy in _partitionClients.Values)
        {
            if (!partitionLazy.IsValueCreated)
            {
                continue;
            }

            McpPartitionClients partition = partitionLazy.Value;

            IMcpClient[] drained = partition.DrainClients();

            for (int i = drained.Length - 1; i >= 0; i--)
            {
                try
                {
                    await drained[i].DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error disposing MCP client instance.");
                }
            }
        }

        await AwaitRetiredPartitionDisposalsAsync()
            .ConfigureAwait(false);

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

        _hostLifetimeCts.Dispose();
    }

}
