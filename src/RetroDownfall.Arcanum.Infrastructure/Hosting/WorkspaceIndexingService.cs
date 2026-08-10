using System.Buffers;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Indexes workspace files into The Weave for semantic codebase retrieval. Maintains a
/// thread-safe set of "known" workspace paths (populated by <see cref="RegisterWorkspace"/>, called by
/// <c>WizardIntelligenceProvider</c> on every inference turn) and re-indexes each of them on a
/// background interval, plus supports an immediate on-demand re-index via <see cref="IndexNowAsync"/>
/// (used by the manual <c>POST /api/workspaces/{id}/files/index</c> endpoint).
///
/// Idles unless <c>Arcanum:Features:CodebaseRetrieval</c> is enabled; the polling cadence is a
/// code-owned invariant. This is therefore a no-op on the hot path until an operator opts in, using
/// the same idle-when-disabled pattern as <see cref="EntryWeavingService"/>.
///
/// Change detection: a file is only re-chunked/re-embedded when its <c>LastWriteTimeUtc</c> differs from
/// the <c>FileLastWriteTime</c> recorded on its existing <c>workspace_file_chunks</c> rows — unchanged
/// files are skipped every tick, keeping steady-state re-index cost proportional to the number of edited
/// files, not the size of the workspace. Existing RelativePath → FileLastWriteTime pairs are loaded in
/// one query per workspace tick (not a per-file SELECT).
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService workspace indexing scheduler; covered via WorkspaceIndexingServiceTests exercising the indexing logic directly.
internal sealed class WorkspaceIndexingService(
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IWeaveService weaveService,
    WeaveIndexAvailability weaveIndexAvailability,
    IServiceScopeFactory scopeFactory,
    IWorkspaceFileWatcherFactory watcherFactory,
    ILogger<WorkspaceIndexingService> logger) :
    BackgroundService,
    IWorkspaceIndexingService,
    IWorkspaceIndexRuntimeStatusProvider
{

    private static readonly HashSet<string> IgnoredDirectorySegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        ".git",
        "node_modules",
        ".vs",
        ".nuget",
        "packages",
        "dist",
        "build",
    };

    private const int MaxPendingPathsPerWorkspace = 4_096;

    private readonly ConcurrentDictionary<string, byte> _knownWorkspaces = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, IWorkspaceFileWatcher> _watchers = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, PendingWorkspaceChanges> _pendingChanges = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, RuntimeStatusState> _runtimeStatuses = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _reconciliationGates = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _watcherSignal = new(0, 1);

    private readonly object _watcherRegistryGate = new();

    internal int ActiveWatcherCount => _watchers.Count;

    public WorkspaceIndexRuntimeStatus GetRuntimeStatus(string workspacePath)
    {

        string normalized;

        try
        {

            normalized = Path.GetFullPath(workspacePath);

        }
        catch (Exception)
        {

            return WorkspaceIndexRuntimeStatus.NotWatching;

        }

        return _runtimeStatuses.TryGetValue(normalized, out RuntimeStatusState? state)
            ? state.Snapshot()
            : WorkspaceIndexRuntimeStatus.NotWatching;

    }

    public void RegisterWorkspace(string workspacePath)
    {

        if (string.IsNullOrWhiteSpace(workspacePath))
        {

            return;

        }

        Result<string> validated = TryValidateWorkspacePath(workspacePath);

        if (validated.IsFailure)
        {

            logger.LogDebug(
                "Workspace registration skipped for {WorkspacePath}: {Reason}",
                workspacePath,
                validated.Error.Message);

            return;

        }

        _knownWorkspaces[validated.Value] = 0;

        _runtimeStatuses.GetOrAdd(validated.Value, static _ => new RuntimeStatusState());

        EnsureWatcher(validated.Value);

    }

    public void UnregisterWorkspace(string workspacePath)
    {

        if (string.IsNullOrWhiteSpace(workspacePath))
        {

            return;

        }

        string normalized;

        try
        {

            normalized = Path.GetFullPath(workspacePath);

        }
        catch (Exception)
        {

            return;

        }

        _knownWorkspaces.TryRemove(normalized, out _);

        _pendingChanges.TryRemove(normalized, out _);

        lock (_watcherRegistryGate)
        {

            if (_watchers.TryRemove(normalized, out IWorkspaceFileWatcher? watcher))
            {

                watcher.Dispose();

            }

        }

        if (_runtimeStatuses.TryGetValue(normalized, out RuntimeStatusState? state))
        {

            state.SetWatching(false);

        }

    }

    public async Task IndexNowAsync(string workspacePath, CancellationToken cancellationToken)
    {

        Result<string> validated = TryValidateWorkspacePath(workspacePath);

        if (validated.IsFailure)
        {

            logger.LogWarning(
                "On-demand workspace re-index rejected for {WorkspacePath}: {Reason}",
                workspacePath,
                validated.Error.Message);

            return;

        }

        string normalized = validated.Value;

        _knownWorkspaces[normalized] = 0;

        _runtimeStatuses.GetOrAdd(normalized, static _ => new RuntimeStatusState());

        EnsureWatcher(normalized);

        try
        {

            EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

            if (!embeddings.Enabled || !embeddings.CodebaseRetrievalEnabled)
            {

                logger.LogDebug("Workspace re-index skipped for {WorkspacePath}: codebase retrieval is disabled.", normalized);

                return;

            }

            await ReconcileWorkspaceAsync(normalized, embeddings, cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "On-demand workspace re-index failed for {WorkspacePath}; continuing.", workspacePath);

        }

    }

    /// <summary>
    /// Applies the same allowlist enforced at campaign-creation time
    /// (<see cref="CampaignPathPolicy.ValidateAndNormalizePath"/>, gated on
    /// <see cref="SecuritySettings.CampaignRoots"/>) to workspace registration/indexing. Without this,
    /// any caller supplying an arbitrary <c>WorkingDirectory</c> on an inference request could get
    /// unrelated, non-campaign directories (for example a user's home directory or other system paths)
    /// background-indexed and persisted into The Weave, then retrieved via semantic search — bypassing
    /// every other workspace-touching feature's path containment (Spells, Perception, Campaigns).
    /// </summary>
    private Result<string> TryValidateWorkspacePath(string workspacePath) =>
        CampaignPathPolicy.ValidateAndNormalizePath(workspacePath, optionsMonitor.CurrentValue);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        await Task.Yield();

        bool wasEnabled = false;

        DateTimeOffset nextReconciliation = DateTimeOffset.UtcNow;

        try
        {

            while (!stoppingToken.IsCancellationRequested)
            {

                try
                {

                    EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

                    bool enabled = embeddings.Enabled && embeddings.CodebaseRetrievalEnabled;

                    if (!enabled)
                    {

                        wasEnabled = false;

                        DisposeAllWatchers();

                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);

                        continue;

                    }

                    if (!wasEnabled)
                    {

                        logger.LogInformation("Workspace Indexing started tracking known workspaces for semantic codebase retrieval.");

                        wasEnabled = true;

                    }

                    foreach (string workspacePath in _knownWorkspaces.Keys.ToArray())
                    {

                        EnsureWatcher(workspacePath);

                    }

                    DateTimeOffset now = DateTimeOffset.UtcNow;

                    if (now >= nextReconciliation)
                    {

                        foreach (string workspacePath in _knownWorkspaces.Keys.ToArray())
                        {

                            stoppingToken.ThrowIfCancellationRequested();

                            await ReconcileWorkspaceAsync(workspacePath, embeddings, stoppingToken).ConfigureAwait(false);

                        }

                        int intervalMinutes = ArcanumSettingClamps.EmbeddingsCodebaseReconciliationIntervalMinutes(
                            embeddings.Codebase.ReconciliationIntervalMinutes);

                        nextReconciliation = DateTimeOffset.UtcNow.AddMinutes(intervalMinutes);

                    }

                    int debounceMilliseconds = ArcanumSettingClamps.EmbeddingsCodebaseWatcherDebounceMilliseconds(
                        embeddings.Codebase.WatcherDebounceMilliseconds);

                    TimeSpan untilReconciliation = nextReconciliation - DateTimeOffset.UtcNow;

                    TimeSpan wait = untilReconciliation <= TimeSpan.Zero
                        ? TimeSpan.Zero
                        : untilReconciliation;

                    bool signaled = await _watcherSignal.WaitAsync(wait, stoppingToken).ConfigureAwait(false);

                    if (signaled)
                    {

                        await Task.Delay(TimeSpan.FromMilliseconds(debounceMilliseconds), stoppingToken).ConfigureAwait(false);

                    }

                    foreach (string workspacePath in _pendingChanges.Keys.ToArray())
                    {

                        await ProcessPendingWatcherEventsAsync(workspacePath, stoppingToken).ConfigureAwait(false);

                    }

                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {

                    break;

                }
                catch (Exception ex)
                {

                    logger.LogError(ex, "Workspace Indexing tick failed; continuing.");

                    // nextReconciliation is only stamped after the reconciliation loop completes, so a
                    // tick that throws (e.g. a locked or corrupt Grimoire) leaves the due time in the
                    // past and the next iteration would reconcile again immediately. Back off before
                    // retrying — otherwise a persistent failure spins, flooding logs and burning CPU
                    // until the underlying problem is fixed. Mirrors EntryWeavingService.
                    try
                    {

                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);

                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {

                        break;

                    }

                }

            }

        }
        finally
        {

            DisposeAllWatchers();

        }

    }

    internal async Task ProcessPendingWatcherEventsAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (!_pendingChanges.TryGetValue(workspacePath, out PendingWorkspaceChanges? pending))
        {

            return;

        }

        PendingWorkspaceSnapshot snapshot = pending.TakeSnapshot();

        if (!snapshot.ReconciliationRequested && snapshot.Actions.Count == 0)
        {

            return;

        }

        EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

        if (!embeddings.Enabled || !embeddings.CodebaseRetrievalEnabled)
        {

            return;

        }

        if (snapshot.ReconciliationRequested)
        {

            await ReconcileWorkspaceAsync(workspacePath, embeddings, cancellationToken).ConfigureAwait(false);

            return;

        }

        RuntimeStatusState status = _runtimeStatuses.GetOrAdd(
            workspacePath,
            static _ => new RuntimeStatusState());

        bool succeeded = await ProcessIncrementalChangesAsync(
            workspacePath,
            snapshot.Actions,
            embeddings,
            cancellationToken).ConfigureAwait(false);

        if (succeeded)
        {

            status.MarkSuccessfulIndex();

        }
        else
        {

            status.MarkDegraded(overflowed: false);

            pending.RequestReconciliation();

            SignalWatcherWork();

        }

    }

    private async Task<bool> ProcessIncrementalChangesAsync(
        string workspacePath,
        IReadOnlyDictionary<string, PendingPathAction> actions,
        EmbeddingSettings embeddings,
        CancellationToken cancellationToken)
    {

        if (!weaveService.IsAvailable || !Directory.Exists(workspacePath))
        {

            return false;

        }

        CodebaseEmbeddingSettings codebase = embeddings.Codebase ?? new CodebaseEmbeddingSettings();

        HashSet<string> extensions = new(codebase.FileExtensions, StringComparer.OrdinalIgnoreCase);

        int maxFilesToIndex = ArcanumSettingClamps.EmbeddingsCodebaseMaxFilesToIndex(codebase.MaxFilesToIndex);

        int filesIndexed = 0;

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        foreach ((string fullPath, PendingPathAction action) in actions
                     .OrderBy(static pair => pair.Value == PendingPathAction.Delete ? 0 : 1)
                     .ThenBy(static pair => pair.Key, StringComparer.Ordinal))
        {

            cancellationToken.ThrowIfCancellationRequested();

            string normalizedPath;

            try
            {

                normalizedPath = Path.GetFullPath(fullPath);

            }
            catch (Exception)
            {

                continue;

            }

            if (!WorkspacePathPolicy.IsPathUnderWorkspace(workspacePath, normalizedPath))
            {

                continue;

            }

            string relativePath = Path.GetRelativePath(workspacePath, normalizedPath);

            if (ContainsIgnoredDirectorySegment(relativePath))
            {

                await DeleteExistingChunksAsync(db, workspacePath, relativePath, cancellationToken).ConfigureAwait(false);

                continue;

            }

            if (action == PendingPathAction.Delete)
            {

                if (Directory.Exists(normalizedPath))
                {

                    return false;

                }

                if (!File.Exists(normalizedPath))
                {

                    await DeleteExistingChunksAsync(db, workspacePath, relativePath, cancellationToken).ConfigureAwait(false);

                    continue;

                }

            }

            if (Directory.Exists(normalizedPath))
            {

                return false;

            }

            bool ignored = !extensions.Contains(Path.GetExtension(normalizedPath));

            if (ignored || !File.Exists(normalizedPath))
            {

                await DeleteExistingChunksAsync(db, workspacePath, relativePath, cancellationToken).ConfigureAwait(false);

                continue;

            }

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                    workspacePath,
                    normalizedPath,
                    out string? resolvedFinalPath))
            {

                await DeleteExistingChunksAsync(db, workspacePath, relativePath, cancellationToken).ConfigureAwait(false);

                continue;

            }

            string identityPath = Path.GetFullPath(resolvedFinalPath ?? normalizedPath);

            if (!FileHandleIdentityInterop.TryGetPathIdentity(identityPath, out FileHandleIdentity expectedIdentity))
            {

                return false;

            }

            FileInfo info = new(normalizedPath);

            if (filesIndexed >= maxFilesToIndex)
            {

                return false;

            }

            bool indexed = await IndexFileAsync(
                db,
                workspacePath,
                relativePath,
                normalizedPath,
                expectedIdentity,
                info.LastWriteTimeUtc,
                cancellationToken).ConfigureAwait(false);

            if (!indexed)
            {

                return false;

            }

            filesIndexed++;

        }

        return true;

    }

    /// <summary>
    /// Single-flight per workspace: a reconciliation requested while that same workspace is already
    /// reconciling coalesces onto the in-flight run instead of starting a duplicate full scan. The
    /// timer loop, the watcher drain, and <see cref="IndexNowAsync"/> (therefore
    /// <c>POST /api/workspaces/{id}/files/index</c>) all pass through this gate, so repeated manual
    /// re-index requests cannot multiply workspace walks or embedding-provider spend, and two runs can
    /// never race on the same deterministic <c>ChunkId</c> rows.
    /// </summary>
    private async Task<bool> ReconcileWorkspaceAsync(
        string workspacePath,
        EmbeddingSettings embeddings,
        CancellationToken cancellationToken)
    {

        SemaphoreSlim gate = _reconciliationGates.GetOrAdd(
            workspacePath,
            static _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {

            logger.LogDebug(
                "Workspace reconciliation for {WorkspacePath} is already in flight; coalescing this request onto it.",
                workspacePath);

            return false;

        }

        RuntimeStatusState status = _runtimeStatuses.GetOrAdd(
            workspacePath,
            static _ => new RuntimeStatusState());

        status.SetReconciling(true);

        try
        {

            bool completed = await IndexWorkspaceAsync(workspacePath, embeddings, cancellationToken).ConfigureAwait(false);

            if (completed)
            {

                status.MarkReconciled();

                EnsureWatcher(workspacePath);

            }
            else
            {

                status.MarkDegraded(overflowed: status.Snapshot().Overflowed);

            }

            return completed;

        }
        finally
        {

            status.SetReconciling(false);

            gate.Release();

        }

    }

    private void EnsureWatcher(string workspacePath)
    {

        EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

        if (!embeddings.Enabled || !embeddings.CodebaseRetrievalEnabled)
        {

            return;

        }

        RuntimeStatusState status = _runtimeStatuses.GetOrAdd(
            workspacePath,
            static _ => new RuntimeStatusState());

        int maxWatchers = ArcanumSettingClamps.EmbeddingsCodebaseMaxWatchers(
            embeddings.Codebase.MaxWatchers);

        lock (_watcherRegistryGate)
        {

            if (_watchers.ContainsKey(workspacePath))
            {

                status.SetWatching(true);

                return;

            }

            if (maxWatchers == 0 || _watchers.Count >= maxWatchers)
            {

                status.SetWatching(false);

                status.MarkDegraded(overflowed: false);

                return;

            }

            try
            {

                IWorkspaceFileWatcher watcher = watcherFactory.Create(
                    workspacePath,
                    QueueWatcherChange,
                    exception => HandleWatcherError(workspacePath, exception));

                if (_watchers.TryAdd(workspacePath, watcher))
                {

                    status.SetWatching(true);

                }
                else
                {

                    watcher.Dispose();

                }

            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {

                logger.LogWarning(
                    ex,
                    "Workspace watcher is unavailable for {WorkspacePath}; periodic reconciliation remains active.",
                    workspacePath);

                status.SetWatching(false);

                status.MarkDegraded(overflowed: false);

            }

        }

    }

    private void QueueWatcherChange(WorkspaceFileChange change)
    {

        if (!_knownWorkspaces.ContainsKey(change.WorkspacePath))
        {

            return;

        }

        RuntimeStatusState status = _runtimeStatuses.GetOrAdd(
            change.WorkspacePath,
            static _ => new RuntimeStatusState());

        status.MarkEvent();

        PendingWorkspaceChanges pending = _pendingChanges.GetOrAdd(
            change.WorkspacePath,
            static _ => new PendingWorkspaceChanges());

        bool overflowed = false;

        if (change.Kind == WorkspaceFileChangeKind.Renamed && change.OldFullPath is not null)
        {

            overflowed |= pending.Add(change.OldFullPath, PendingPathAction.Delete);

            overflowed |= pending.Add(change.FullPath, PendingPathAction.Upsert);

        }
        else
        {

            PendingPathAction action = change.Kind == WorkspaceFileChangeKind.Deleted
                ? PendingPathAction.Delete
                : PendingPathAction.Upsert;

            overflowed = pending.Add(change.FullPath, action);

        }

        if (overflowed)
        {

            status.MarkDegraded(overflowed: true);

        }

        SignalWatcherWork();

    }

    private void HandleWatcherError(string workspacePath, Exception exception)
    {

        bool overflowed = exception is InternalBufferOverflowException;

        logger.LogWarning(
            exception,
            "Workspace watcher failed for {WorkspacePath}; marking the index stale and scheduling reconciliation.",
            workspacePath);

        lock (_watcherRegistryGate)
        {

            if (_watchers.TryRemove(workspacePath, out IWorkspaceFileWatcher? watcher))
            {

                watcher.Dispose();

            }

        }

        RuntimeStatusState status = _runtimeStatuses.GetOrAdd(
            workspacePath,
            static _ => new RuntimeStatusState());

        status.SetWatching(false);

        status.MarkDegraded(overflowed);

        _pendingChanges.GetOrAdd(workspacePath, static _ => new PendingWorkspaceChanges())
            .RequestReconciliation();

        SignalWatcherWork();

    }

    /// <summary>
    /// Raises the "there is watcher work pending" edge, coalescing concurrent signals into the single
    /// permit <c>_watcherSignal</c> can hold.
    /// </summary>
    /// <remarks>
    /// The <c>CurrentCount</c> probe is only a cheap fast path, never a guard: this runs on the pump
    /// thread (<see cref="ProcessPendingWatcherEventsAsync"/>) and on every independent
    /// <c>FileSystemWatcher</c> dispatch thread at once, so two callers can both observe <c>0</c> and both
    /// call <c>Release</c> on a <c>SemaphoreSlim(0, 1)</c>. The loser gets
    /// <see cref="SemaphoreFullException"/>, which — because the watcher callback chain has no exception
    /// guard of its own — would escape onto a watcher-dispatch thread and take down indexing (or the
    /// host). Swallowing it is exactly right: it means the signal the caller wanted to raise is already
    /// raised.
    /// </remarks>
    private void SignalWatcherWork()
    {

        if (_watcherSignal.CurrentCount != 0)
        {

            return;

        }

        try
        {

            _watcherSignal.Release();

        }
        catch (SemaphoreFullException)
        {

            // A concurrent signaller won the race; the pending-work edge is already raised.

        }
        catch (ObjectDisposedException)
        {

            // Host shutdown raced the callback; the watcher is being disposed.

        }

    }

    private void DisposeAllWatchers()
    {

        lock (_watcherRegistryGate)
        {

            foreach ((string workspacePath, IWorkspaceFileWatcher watcher) in _watchers.ToArray())
            {

                watcher.Dispose();

                _watchers.TryRemove(workspacePath, out _);

                if (_runtimeStatuses.TryGetValue(workspacePath, out RuntimeStatusState? status))
                {

                    status.SetWatching(false);

                }

            }

        }

    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {

        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        DisposeAllWatchers();

    }

    public override void Dispose()
    {

        DisposeAllWatchers();

        _watcherSignal.Dispose();

        base.Dispose();

    }

    internal ValueTask DisposeAsync()
    {

        Dispose();

        return ValueTask.CompletedTask;

    }

    internal async Task<bool> IndexWorkspaceAsync(string workspacePath, EmbeddingSettings embeddings, CancellationToken cancellationToken)
    {

        if (!weaveService.IsAvailable)
        {

            logger.LogDebug(
                "Workspace indexing tick skipped for {WorkspacePath}: The Weave is unavailable (enable an embedding-backed Arcanum:Features option and configure Arcanum:Integrations:Embeddings:Provider and Arcanum:Integrations:Embeddings:Model).",
                workspacePath);

            return false;

        }

        CodebaseEmbeddingSettings codebase = embeddings.Codebase ?? new CodebaseEmbeddingSettings();

        if (codebase.FileExtensions.Length == 0)
        {

            logger.LogDebug("Workspace indexing tick skipped for {WorkspacePath}: no file extensions configured.", workspacePath);

            return false;

        }

        if (!Directory.Exists(workspacePath))
        {

            logger.LogWarning("Workspace indexing skipped: {WorkspacePath} does not exist or is not a directory.", workspacePath);

            return false;

        }

        int maxFilesToIndex = ArcanumSettingClamps.EmbeddingsCodebaseMaxFilesToIndex(codebase.MaxFilesToIndex);

        HashSet<string> extensions = new(codebase.FileExtensions, StringComparer.OrdinalIgnoreCase);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        int filesIndexed = 0;

        IEnumerable<string> candidates =
            EnumerateCandidateFiles(workspacePath, extensions);

        // Every candidate that reaches this point still exists at its relative path, regardless of
        // whether this tick actually gets to re-embed it (see the maxFilesToIndex check below) — so
        // this set drives orphaned-chunk cleanup after the loop, not just the re-index decision.
        HashSet<string> seenRelativePaths = new(StringComparer.Ordinal);

        // One query per workspace tick: RelativePath → FileLastWriteTime for change detection,
        // instead of a per-file SELECT (N+1 against workspace_file_chunks).
        Dictionary<string, DateTime> existingLastWriteByRelativePath =
            await LoadExistingFileLastWriteTimesAsync(db, workspacePath, cancellationToken).ConfigureAwait(false);

        foreach (string fullPath in candidates)
        {

            cancellationToken.ThrowIfCancellationRequested();

            try
            {

                if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspacePath, fullPath, out string? resolvedFinalPath))
                {

                    // Escaping symlink — skipped, never followed.
                    continue;

                }

                string relativePath = Path.GetRelativePath(workspacePath, fullPath);

                seenRelativePaths.Add(relativePath);

                if (filesIndexed >= maxFilesToIndex)
                {

                    // Per-tick re-embed budget exhausted — the file is still "seen" (above) so a
                    // later orphan-cleanup pass never mistakes it for deleted, but re-indexing it is
                    // deferred to a future tick.
                    continue;

                }

                // Captures a stable file identity (dev/ino on Unix, volume+file index on Windows) at
                // the point of the lexical/symlink check above. IndexFileAsync re-checks this
                // identity immediately after opening the file, closing the TOCTOU window between this
                // check and the actual read (see SandboxedFileIo.TryOpenForRead for the same pattern).
                string identityPath = Path.GetFullPath(resolvedFinalPath ?? fullPath);

                if (!FileHandleIdentityInterop.TryGetPathIdentity(identityPath, out FileHandleIdentity expectedIdentity))
                {

                    // Could not resolve a stable identity (e.g. a race with a delete) — skip rather
                    // than risk reading through a path that may have been swapped.
                    continue;

                }

                FileInfo info = new(fullPath);

                DateTime lastWriteUtc = info.LastWriteTimeUtc;

                if (existingLastWriteByRelativePath.TryGetValue(relativePath, out DateTime existing)
                    && existing == lastWriteUtc)
                {

                    // Unchanged since last index — skip without consuming the per-tick file budget.
                    continue;

                }

                bool indexed = await IndexFileAsync(
                    db,
                    workspacePath,
                    relativePath,
                    fullPath,
                    expectedIdentity,
                    lastWriteUtc,
                    cancellationToken).ConfigureAwait(false);

                if (indexed)
                {

                    filesIndexed++;

                }

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {

                throw;

            }
            catch (Exception ex)
            {

                logger.LogWarning(ex, "Workspace indexing failed for file {FullPath}; continuing with the next file.", fullPath);

            }

        }

        await DeleteOrphanedChunksAsync(db, workspacePath, seenRelativePaths, cancellationToken).ConfigureAwait(false);

        return true;

    }

    /// <summary>
    /// Manually walks <paramref name="workspacePath"/> breadth-first (mirroring
    /// <c>PhysicalFileSystemBrowser.ListAsync</c>'s recursive-listing walk), pruning
    /// <see cref="IgnoredDirectorySegments"/> and symlink-escaping subdirectories <b>before</b>
    /// descending into them — unlike <see cref="Directory.EnumerateFiles(string, string, EnumerationOptions)"/>
    /// with <c>RecurseSubdirectories = true</c>, which would still visit every entry under a huge
    /// ignored directory (for example <c>node_modules</c>) only to discard them one by one. The walk
    /// continues until every reachable candidate has been visited or the caller cancels.
    /// </summary>
    private static IEnumerable<string> EnumerateCandidateFiles(
        string workspacePath,
        HashSet<string> extensions)
    {
        Queue<string> pendingDirectories = new();

        pendingDirectories.Enqueue(workspacePath);

        while (pendingDirectories.Count > 0)
        {

            string directory = pendingDirectories.Dequeue();

            foreach (string fullPath in EnumerateAccessibleEntries(directory))
            {

                FileAttributes attributes;

                try
                {

                    attributes = File.GetAttributes(fullPath);

                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {

                    continue;

                }

                if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                {

                    continue;

                }

                if ((attributes & FileAttributes.Directory) != 0)
                {

                    string name = Path.GetFileName(fullPath);

                    if (IgnoredDirectorySegments.Contains(name))
                    {

                        // Pruned before recursion — its contents are never visited at all, rather
                        // than being walked and discarded one entry at a time.
                        continue;

                    }

                    if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspacePath, fullPath, out _))
                    {

                        // Escaping symlinked directory — never descended into.
                        continue;

                    }

                    pendingDirectories.Enqueue(fullPath);

                }
                else if (extensions.Contains(Path.GetExtension(fullPath)))
                {

                    yield return fullPath;

                }

            }

        }

    }

    private static IEnumerable<string> EnumerateAccessibleEntries(
        string directory)
    {

        IEnumerator<string>? enumerator = null;

        try
        {

            enumerator = Directory
                .EnumerateFileSystemEntries(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .GetEnumerator();

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            yield break;

        }

        using (enumerator)
        {

            while (true)
            {

                string fullPath;

                try
                {

                    if (!enumerator.MoveNext())
                    {

                        yield break;

                    }

                    fullPath = enumerator.Current;

                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {

                    yield break;

                }

                yield return fullPath;

            }

        }

    }

    /// <summary>
    /// Deletes chunks for any previously-indexed file under <paramref name="workspacePath"/> whose
    /// relative path is not in <paramref name="seenRelativePaths"/> — i.e. a file that was deleted (or
    /// renamed, or moved outside every configured file extension) since the last successful full walk.
    /// Without this, a removed file's stale chunks/embeddings persist forever and keep surfacing in
    /// semantic search results for content that no longer exists. It runs only after the complete
    /// cancellable workspace walk, so an unvisited-but-still-present file is never misclassified.
    /// </summary>
    private async Task DeleteOrphanedChunksAsync(
        ArcanumDbContext db,
        string workspacePath,
        HashSet<string> seenRelativePaths,
        CancellationToken cancellationToken)
    {

        List<string> indexedRelativePaths = await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT DISTINCT "RelativePath"
                    FROM "workspace_file_chunks"
                    WHERE "WorkspacePath" = @workspacePath
                    """;

                AddParameter(cmd, "@workspacePath", workspacePath);

                List<string> paths = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    paths.Add(reader.GetString(0));

                }

                return paths;

            },
            cancellationToken).ConfigureAwait(false);

        foreach (string relativePath in indexedRelativePaths)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (seenRelativePaths.Contains(relativePath))
            {

                continue;

            }

            try
            {

                await DeleteExistingChunksAsync(db, workspacePath, relativePath, cancellationToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {

                throw;

            }
            catch (Exception ex)
            {

                logger.LogWarning(
                    ex,
                    "Failed to delete orphaned chunks for removed file {RelativePath} in {WorkspacePath}; will retry next tick.",
                    relativePath,
                    workspacePath);

            }

        }

    }

    /// <summary>
    /// Chunks, embeds, and persists a single changed/new file. Returns <c>false</c> (without throwing)
    /// when the file is empty or embedding fails — all graceful-degradation
    /// outcomes that simply do not consume the per-tick file budget.
    /// </summary>
    private async Task<bool> IndexFileAsync(
        ArcanumDbContext db,
        string workspacePath,
        string relativePath,
        string fullPath,
        FileHandleIdentity expectedIdentity,
        DateTime lastWriteUtc,
        CancellationToken cancellationToken)
    {

        int maxChunkChars = ArcanumSettingClamps.EmbeddingsChunkSizeChars(
            optionsMonitor.CurrentValue.ResolveEmbeddings().ChunkSizeChars);

        int readPageCharacters = checked(maxChunkChars * 8);

        char[] buffer = ArrayPool<char>.Shared.Rent(readPageCharacters);

        List<string> insertedIds = [];

        try
        {

            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            // Mirrors SandboxedFileIo.TryRevalidateOpenedHandle: the file could have been swapped
            // (e.g. to a symlink pointing outside the workspace) between the containment check in
            // IndexWorkspaceAsync and this open, so the opened handle's identity — and its path's
            // containment — must be re-verified before any content is read.
            if (!FileHandleIdentityInterop.TryGetHandleIdentity(stream.SafeFileHandle, out FileHandleIdentity actualIdentity)
                || !FileHandleIdentity.IdentitiesMatch(expectedIdentity, actualIdentity)
                || !WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspacePath, Path.GetFullPath(stream.Name), out _))
            {

                logger.LogWarning(
                    "Workspace indexing rejected {FullPath}: file identity changed between the containment check and open (possible symlink swap); skipping.",
                    fullPath);

                return false;

            }

            using StreamReader reader = new(stream);

            string extension = Path.GetExtension(relativePath);

            DateTimeOffset indexedAt = DateTimeOffset.UtcNow;

            HashSet<string> existingIds = await LoadExistingChunkIdsAsync(
                db,
                workspacePath,
                relativePath,
                cancellationToken).ConfigureAwait(false);

            HashSet<string> nextIds = new(StringComparer.Ordinal);

            Dictionary<string, int> occurrences = new(StringComparer.Ordinal);

            List<IndexedChunkMetadata> metadata = [];

            int globalCharacterOffset = 0;

            int globalLineOffset = 0;

            int globalChunkIndex = 0;

            // A high surrogate held back from the previous page, already sitting at buffer[0].
            int carriedChars = 0;

            while (true)
            {

                int requested = readPageCharacters - carriedChars;

                int read = await reader
                    .ReadBlockAsync(
                        buffer.AsMemory(carriedChars, requested),
                        cancellationToken)
                    .ConfigureAwait(false);

                int available = carriedChars + read;

                if (available == 0)
                {

                    break;

                }

                // ReadBlockAsync fills to a character count and knows nothing about UTF-16 pairs, so a
                // non-BMP character straddling the page boundary would leave this page ending in a lone
                // high surrogate and the next beginning with the orphaned low half. Both fragments would
                // then be embedded and persisted with the character replaced by U+FFFD, so the stored
                // chunk would no longer be the exact source slice. Holding the high surrogate back joins
                // it to its partner on the next page. A short read means end of stream: a trailing high
                // surrogate there is genuinely unpaired and is emitted rather than silently dropped.
                bool endOfStream = read < requested;

                int pageLength = !endOfStream && char.IsHighSurrogate(buffer[available - 1])
                    ? available - 1
                    : available;

                string page = new(buffer, 0, pageLength);

                WorkspaceCodeChunker.Chunk[] pageChunks =
                    WorkspaceCodeChunker.ChunkText(
                        page,
                        extension,
                        maxChunkChars);

                if (pageChunks.Length > 0)
                {

                    IndexedChunk[] indexedChunks = new IndexedChunk[pageChunks.Length];

                    for (int index = 0; index < pageChunks.Length; index++)
                    {

                        WorkspaceCodeChunker.Chunk chunk = pageChunks[index];

                        int occurrence = occurrences.GetValueOrDefault(chunk.Content);

                        occurrences[chunk.Content] = occurrence + 1;

                        indexedChunks[index] = new IndexedChunk(
                            CreateStableChunkId(
                                workspacePath,
                                relativePath,
                                chunk.Content,
                                occurrence),
                            chunk);

                    }

                    IndexedChunk[] missing = indexedChunks
                        .Where(chunk => !existingIds.Contains(chunk.ChunkId))
                        .ToArray();

                    Embedding<float>[] generated = [];

                    if (missing.Length > 0)
                    {

                        Result<Embedding<float>[]> embedResult = await weaveService
                            .EmbedBatchAsync(
                                missing
                                    .Select(static chunk => chunk.Chunk.Content)
                                    .ToList(),
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (embedResult.IsFailure
                            || embedResult.Value.Length != missing.Length)
                        {

                            logger.LogWarning(
                                "Workspace indexing embed page failed for {FullPath} ({Code}): {Message}",
                                fullPath,
                                embedResult.IsFailure
                                    ? embedResult.Error.Code
                                    : ErrorCodes.Embeddings.ProviderUnavailable,
                                embedResult.IsFailure
                                    ? embedResult.Error.Message
                                    : "Embedding provider returned an unexpected result count.");

                            await DeleteInsertedChunksAsync(db, insertedIds).ConfigureAwait(false);

                            return false;

                        }

                        generated = embedResult.Value;

                    }

                    Dictionary<string, float[]> vectorsByChunkId = new(StringComparer.Ordinal);

                    for (int index = 0; index < missing.Length; index++)
                    {

                        vectorsByChunkId[missing[index].ChunkId] = generated[index].Vector.ToArray();

                    }

                    for (int index = 0; index < indexedChunks.Length; index++)
                    {

                        IndexedChunk indexedChunk = indexedChunks[index];

                        WorkspaceCodeChunker.Chunk chunk = indexedChunk.Chunk;

                        int chunkIndex = globalChunkIndex++;

                        nextIds.Add(indexedChunk.ChunkId);

                        metadata.Add(new IndexedChunkMetadata(
                            indexedChunk.ChunkId,
                            chunkIndex,
                            globalCharacterOffset + chunk.CharOffset,
                            chunk.Content.Length,
                            globalLineOffset + chunk.StartLine,
                            globalLineOffset + chunk.EndLine));

                        if (existingIds.Contains(indexedChunk.ChunkId))
                        {

                            continue;

                        }

                        insertedIds.Add(indexedChunk.ChunkId);

                        await InsertChunkAsync(
                                db,
                                indexedChunk.ChunkId,
                                workspacePath,
                                relativePath,
                                chunkIndex,
                                chunk.Content,
                                globalCharacterOffset + chunk.CharOffset,
                                chunk.Content.Length,
                                globalLineOffset + chunk.StartLine,
                                globalLineOffset + chunk.EndLine,
                                DateTime.MinValue,
                                indexedAt,
                                vectorsByChunkId[indexedChunk.ChunkId],
                                cancellationToken)
                            .ConfigureAwait(false);

                    }

                }

                globalCharacterOffset += pageLength;

                globalLineOffset += page.Count(static character => character == '\n');

                carriedChars = available - pageLength;

                if (carriedChars == 1)
                {

                    buffer[0] = buffer[available - 1];

                }

            }

            foreach (string obsoleteId in existingIds.Where(id => !nextIds.Contains(id)))
            {

                await DeleteChunkByIdAsync(db, obsoleteId, cancellationToken).ConfigureAwait(false);

            }

            foreach (IndexedChunkMetadata chunk in metadata)
            {

                await UpdateChunkMetadataAsync(
                    db,
                    chunk.ChunkId,
                    chunk.ChunkIndex,
                    chunk.CharOffset,
                    chunk.CharLength,
                    chunk.StartLine,
                    chunk.EndLine,
                    lastWriteUtc,
                    cancellationToken).ConfigureAwait(false);

            }

            return true;

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            await DeleteInsertedChunksAsync(db, insertedIds).ConfigureAwait(false);

            throw;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            await DeleteInsertedChunksAsync(db, insertedIds).ConfigureAwait(false);

            logger.LogWarning(ex, "Workspace indexing could not read {FullPath}; skipping.", fullPath);

            return false;

        }
        catch
        {

            await DeleteInsertedChunksAsync(db, insertedIds).ConfigureAwait(false);

            throw;

        }
        finally
        {

            ArrayPool<char>.Shared.Return(buffer);

        }

    }

    private async Task DeleteInsertedChunksAsync(
        ArcanumDbContext db,
        IEnumerable<string> insertedIds)
    {

        foreach (string chunkId in insertedIds)
        {

            try
            {

                await DeleteChunkByIdAsync(
                    db,
                    chunkId,
                    CancellationToken.None).ConfigureAwait(false);

            }
            catch (Exception ex)
            {

                logger.LogWarning(
                    ex,
                    "Workspace indexing could not clean up incomplete chunk {ChunkId}; reconciliation will retry it.",
                    chunkId);

            }

        }

    }

    private static string CreateStableChunkId(
        string workspacePath,
        string relativePath,
        string content,
        int occurrence)
    {

        string identity = string.Concat(
            workspacePath,
            "\0",
            relativePath,
            "\0",
            occurrence.ToString(CultureInfo.InvariantCulture),
            "\0",
            content);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();

    }

    private static Task<HashSet<string>> LoadExistingChunkIdsAsync(
        ArcanumDbContext db,
        string workspacePath,
        string relativePath,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "ChunkId"
                    FROM "workspace_file_chunks"
                    WHERE "WorkspacePath" = @workspacePath AND "RelativePath" = @relativePath
                    """;

                AddParameter(cmd, "@workspacePath", workspacePath);

                AddParameter(cmd, "@relativePath", relativePath);

                HashSet<string> ids = new(StringComparer.Ordinal);

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    ids.Add(reader.GetString(0));

                }

                return ids;

            },
            cancellationToken);

    }

    private Task UpdateChunkMetadataAsync(
        ArcanumDbContext db,
        string chunkId,
        int chunkIndex,
        int charOffset,
        int charLength,
        int startLine,
        int endLine,
        DateTime fileLastWriteTimeUtc,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "workspace_file_chunks"
                    SET "ChunkIndex" = @chunkIndex,
                        "CharOffset" = @charOffset,
                        "CharLength" = @charLength,
                        "StartLine" = @startLine,
                        "EndLine" = @endLine,
                        "FileLastWriteTime" = @fileLastWriteTime
                    WHERE "ChunkId" = @chunkId
                    """;

                AddParameter(cmd, "@chunkIndex", chunkIndex);

                AddParameter(cmd, "@charOffset", charOffset);

                AddParameter(cmd, "@charLength", charLength);

                AddParameter(cmd, "@startLine", startLine);

                AddParameter(cmd, "@endLine", endLine);

                AddParameter(cmd, "@fileLastWriteTime", fileLastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(cmd, "@chunkId", chunkId);

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    private Task DeleteChunkByIdAsync(
        ArcanumDbContext db,
        string chunkId,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                if (weaveIndexAvailability.IsVecAvailable)
                {

                    await using DbCommand vecCmd = connection.CreateCommand();

                    vecCmd.CommandText = """DELETE FROM "workspace_file_embeddings_vec" WHERE "ChunkId" = @chunkId""";

                    AddParameter(vecCmd, "@chunkId", chunkId);

                    _ = await vecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await using DbCommand embeddingCmd = connection.CreateCommand();

                embeddingCmd.CommandText = """DELETE FROM "workspace_file_embeddings" WHERE "ChunkId" = @chunkId""";

                AddParameter(embeddingCmd, "@chunkId", chunkId);

                _ = await embeddingCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand chunkCmd = connection.CreateCommand();

                chunkCmd.CommandText = """DELETE FROM "workspace_file_chunks" WHERE "ChunkId" = @chunkId""";

                AddParameter(chunkCmd, "@chunkId", chunkId);

                _ = await chunkCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    /// <summary>
    /// Loads every indexed file's recorded <c>FileLastWriteTime</c> for <paramref name="workspacePath"/>
    /// in a single query. Used for change detection so a workspace tick does not issue one SELECT per
    /// candidate file.
    /// </summary>
    private static Task<Dictionary<string, DateTime>> LoadExistingFileLastWriteTimesAsync(
        ArcanumDbContext db,
        string workspacePath,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                // All chunks for a given RelativePath share the same FileLastWriteTime (written together
                // in IndexFileAsync). DISTINCT is enough; if rows somehow diverge, the last value wins.
                cmd.CommandText =
                    """
                    SELECT "RelativePath", MIN("FileLastWriteTime")
                    FROM "workspace_file_chunks"
                    WHERE "WorkspacePath" = @workspacePath
                    GROUP BY "RelativePath"
                    """;

                AddParameter(cmd, "@workspacePath", workspacePath);

                Dictionary<string, DateTime> map = new(StringComparer.Ordinal);

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    string relativePath = reader.GetString(0);

                    DateTime lastWrite = DateTime.Parse(
                        reader.GetString(1),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind);

                    map[relativePath] = lastWrite;

                }

                return map;

            },
            cancellationToken);

    }

    private Task DeleteExistingChunksAsync(
        ArcanumDbContext db,
        string workspacePath,
        string relativePath,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                if (weaveIndexAvailability.IsVecAvailable)
                {

                    await using DbCommand deleteVecCmd = connection.CreateCommand();

                    deleteVecCmd.CommandText =
                        """
                        DELETE FROM "workspace_file_embeddings_vec"
                        WHERE "ChunkId" IN (
                            SELECT "ChunkId" FROM "workspace_file_chunks"
                            WHERE "WorkspacePath" = @workspacePath AND "RelativePath" = @relativePath
                        )
                        """;

                    AddParameter(deleteVecCmd, "@workspacePath", workspacePath);

                    AddParameter(deleteVecCmd, "@relativePath", relativePath);

                    _ = await deleteVecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await using DbCommand deleteBlobCmd = connection.CreateCommand();

                deleteBlobCmd.CommandText =
                    """
                    DELETE FROM "workspace_file_embeddings"
                    WHERE "ChunkId" IN (
                        SELECT "ChunkId" FROM "workspace_file_chunks"
                        WHERE "WorkspacePath" = @workspacePath AND "RelativePath" = @relativePath
                    )
                    """;

                AddParameter(deleteBlobCmd, "@workspacePath", workspacePath);

                AddParameter(deleteBlobCmd, "@relativePath", relativePath);

                _ = await deleteBlobCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand deleteChunksCmd = connection.CreateCommand();

                deleteChunksCmd.CommandText =
                    """
                    DELETE FROM "workspace_file_chunks"
                    WHERE "WorkspacePath" = @workspacePath AND "RelativePath" = @relativePath
                    """;

                AddParameter(deleteChunksCmd, "@workspacePath", workspacePath);

                AddParameter(deleteChunksCmd, "@relativePath", relativePath);

                _ = await deleteChunksCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    private Task InsertChunkAsync(
        ArcanumDbContext db,
        string chunkId,
        string workspacePath,
        string relativePath,
        int chunkIndex,
        string content,
        int charOffset,
        int charLength,
        int startLine,
        int endLine,
        DateTime fileLastWriteTimeUtc,
        DateTimeOffset indexedAt,
        float[] vector,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                // One transaction over the chunk row, its embedding, and the optional vec0 mirror — the
                // shape TapestryStore.AppendNodesAsync already uses, for the same two reasons. A torn
                // write can never leave a chunk row without its embedding: LoadExistingChunkIdsAsync
                // reads only workspace_file_chunks, so such a row would count as already indexed forever
                // while DivinationService, which joins outward from the embeddings table, could never
                // return it — permanently unsearchable yet still counted by the index status endpoint.
                // And a SQLITE_BUSY retry restarts from a clean transaction instead of replaying the
                // chunk INSERT into a PRIMARY KEY violation, which is not retryable and aborts the file.
                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                await using DbCommand chunkCmd = connection.CreateCommand();

                chunkCmd.Transaction = transaction;

                chunkCmd.CommandText =
                    """
                    INSERT INTO "workspace_file_chunks"
                        ("ChunkId", "WorkspacePath", "RelativePath", "ChunkIndex", "Content", "CharOffset", "CharLength", "StartLine", "EndLine", "FileLastWriteTime", "IndexedAt")
                    VALUES
                        (@chunkId, @workspacePath, @relativePath, @chunkIndex, @content, @charOffset, @charLength, @startLine, @endLine, @fileLastWriteTime, @indexedAt)
                    """;

                AddParameter(chunkCmd, "@chunkId", chunkId);

                AddParameter(chunkCmd, "@workspacePath", workspacePath);

                AddParameter(chunkCmd, "@relativePath", relativePath);

                AddParameter(chunkCmd, "@chunkIndex", chunkIndex);

                AddParameter(chunkCmd, "@content", content);

                AddParameter(chunkCmd, "@charOffset", charOffset);

                AddParameter(chunkCmd, "@charLength", charLength);

                AddParameter(chunkCmd, "@startLine", startLine);

                AddParameter(chunkCmd, "@endLine", endLine);

                AddParameter(chunkCmd, "@fileLastWriteTime", fileLastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(chunkCmd, "@indexedAt", indexedAt.ToString("o", CultureInfo.InvariantCulture));

                _ = await chunkCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                byte[] encoded = EmbeddingBlobCodec.Encode(vector);

                await using DbCommand embeddingCmd = connection.CreateCommand();

                embeddingCmd.Transaction = transaction;

                embeddingCmd.CommandText =
                    """
                    INSERT INTO "workspace_file_embeddings" ("ChunkId", "Embedding", "Dim")
                    VALUES (@chunkId, @embedding, @dim)
                    """;

                AddParameter(embeddingCmd, "@chunkId", chunkId);

                AddParameter(embeddingCmd, "@embedding", encoded);

                AddParameter(embeddingCmd, "@dim", vector.Length);

                _ = await embeddingCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (weaveIndexAvailability.IsVecAvailable)
                {

                    await using DbCommand vecCmd = connection.CreateCommand();

                    vecCmd.Transaction = transaction;

                    vecCmd.CommandText =
                        """
                        INSERT OR REPLACE INTO "workspace_file_embeddings_vec" ("ChunkId", "Embedding")
                        VALUES (@chunkId, @embedding)
                        """;

                    AddParameter(vecCmd, "@chunkId", chunkId);

                    AddParameter(vecCmd, "@embedding", encoded);

                    _ = await vecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    private static async Task<DbConnection> OpenConnectionAsync(ArcanumDbContext db, CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        }

        return connection;

    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {

        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

    private static bool ContainsIgnoredDirectorySegment(string relativePath)
    {

        foreach (string segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {

            if (IgnoredDirectorySegments.Contains(segment))
            {

                return true;

            }

        }

        return false;

    }

    private enum PendingPathAction
    {

        Upsert,
        Delete,

    }

    private sealed class PendingWorkspaceChanges
    {

        private readonly object _gate = new();

        private readonly Dictionary<string, PendingPathAction> _actions = new(StringComparer.Ordinal);

        private bool _reconciliationRequested;

        public bool Add(string fullPath, PendingPathAction action)
        {

            lock (_gate)
            {

                if (_reconciliationRequested)
                {

                    return false;

                }

                if (!_actions.ContainsKey(fullPath) && _actions.Count >= MaxPendingPathsPerWorkspace)
                {

                    _actions.Clear();

                    _reconciliationRequested = true;

                    return true;

                }

                _actions[fullPath] = action;

                return false;

            }

        }

        public void RequestReconciliation()
        {

            lock (_gate)
            {

                _actions.Clear();

                _reconciliationRequested = true;

            }

        }

        public PendingWorkspaceSnapshot TakeSnapshot()
        {

            lock (_gate)
            {

                Dictionary<string, PendingPathAction> actions = new(_actions, StringComparer.Ordinal);

                bool reconciliationRequested = _reconciliationRequested;

                _actions.Clear();

                _reconciliationRequested = false;

                return new PendingWorkspaceSnapshot(actions, reconciliationRequested);

            }

        }

    }

    private sealed record PendingWorkspaceSnapshot(
        IReadOnlyDictionary<string, PendingPathAction> Actions,
        bool ReconciliationRequested);

    private sealed class RuntimeStatusState
    {

        private readonly object _gate = new();

        private bool _watching;

        private bool _degraded;

        private bool _overflowed;

        private bool _reconciling;

        private DateTimeOffset? _lastEventAt;

        private DateTimeOffset? _lastSuccessfulIndexAt;

        public WorkspaceIndexRuntimeStatus Snapshot()
        {

            lock (_gate)
            {

                return new WorkspaceIndexRuntimeStatus(
                    _watching,
                    _degraded,
                    _overflowed,
                    _reconciling,
                    _lastEventAt,
                    _lastSuccessfulIndexAt);

            }

        }

        public void SetWatching(bool watching)
        {

            lock (_gate)
            {

                _watching = watching;

            }

        }

        public void MarkEvent()
        {

            lock (_gate)
            {

                _lastEventAt = DateTimeOffset.UtcNow;

            }

        }

        public void MarkDegraded(bool overflowed)
        {

            lock (_gate)
            {

                _degraded = true;

                _overflowed |= overflowed;

            }

        }

        public void SetReconciling(bool reconciling)
        {

            lock (_gate)
            {

                _reconciling = reconciling;

            }

        }

        public void MarkSuccessfulIndex()
        {

            lock (_gate)
            {

                _lastSuccessfulIndexAt = DateTimeOffset.UtcNow;

            }

        }

        public void MarkReconciled()
        {

            lock (_gate)
            {

                _degraded = false;

                _overflowed = false;

                _lastSuccessfulIndexAt = DateTimeOffset.UtcNow;

            }

        }

    }

    private sealed record IndexedChunk(
        string ChunkId,
        WorkspaceCodeChunker.Chunk Chunk);

    private sealed record IndexedChunkMetadata(
        string ChunkId,
        int ChunkIndex,
        int CharOffset,
        int CharLength,
        int StartLine,
        int EndLine);

}
