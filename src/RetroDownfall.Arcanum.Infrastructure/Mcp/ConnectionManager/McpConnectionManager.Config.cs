using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
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
                McpConfigSnapshot? snapshot =
                    await ReadMcpConfigAsync(globalPath, cancellationToken).ConfigureAwait(false);

                if (snapshot?.Config.McpServers is { Count: > 0 })
                {
                    RegisterFromConfigCore(
                        snapshot.Config,
                        scopeWorkingDirectory: null,
                        sourceDigest: null);
                }
            }

            _globalRegistryLoaded = true;
        }
        finally
        {
            _registryLock.Release();
        }
    }

    private void RegisterFromConfigCore(
        McpConfig config,
        string? scopeWorkingDirectory,
        string? sourceDigest)
    {
        foreach (KeyValuePair<string, McpServerConfig> pair in config.McpServers!)
        {
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
                cfg.AlwaysOn,
                sourceDigest);

            _registry[key] = entry;
        }
    }

    // W3.3 Fix 2: the count-check + TryAdd must be serialized across concurrent
    // registrations. The global path already holds _registryLock (see
    // EnsureGlobalRegistryLoadedAsync); the workspace-build path did not, so
    // parallel workspace loads could both pass the count check and overshoot
    // Registry updates are serialized. This wrapper acquires _registryLock for the entire register+
    // count-check so the cap is enforced atomically. Registration is synchronous
    // (no awaits inside RegisterFromConfigCore), so the lock is never held across
    // async work. Callers already holding _registryLock call RegisterFromConfigCore
    // directly to avoid a non-re-entrant SemaphoreSlim deadlock.
    internal async Task RegisterFromConfigAsync(
        McpConfig config,
        string? scopeWorkingDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (scopeWorkingDirectory is not null)
        {
            throw new InvalidOperationException(
                "Workspace MCP registration requires an exact source digest.");
        }

        await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            RegisterFromConfigCore(
                config,
                scopeWorkingDirectory: null,
                sourceDigest: null);
        }
        finally
        {
            _registryLock.Release();
        }
    }

    private async Task ReconcileWorkspaceConfigAsync(
        McpConfig config,
        string scopeWorkingDirectory,
        string sourceDigest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await AwaitExistingWorkspaceRetirementAsync(
                scopeWorkingDirectory,
                cancellationToken)
            .ConfigureAwait(false);

        await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        WorkspaceRetirementState? retirementState = null;

        try
        {
            HashSet<string> configuredNames = config.McpServers is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(config.McpServers.Keys, StringComparer.Ordinal);

            ManagedMcpServerEntry[] staleEntries = _registry.Values
                .Where(entry =>
                    string.Equals(
                        entry.ScopeWorkingDirectory,
                        scopeWorkingDirectory,
                        StringComparison.Ordinal)
                    && (!string.Equals(
                            entry.SourceDigest,
                            sourceDigest,
                            StringComparison.OrdinalIgnoreCase)
                        || !configuredNames.Contains(entry.Name)))
                .ToArray();

            foreach (ManagedMcpServerEntry staleEntry in staleEntries)
            {
                staleEntry.MarkRetired();

                (string Name, string? WorkingDirectory) key =
                    (staleEntry.Name, staleEntry.ScopeWorkingDirectory);

                if (_registry.TryGetValue(key, out ManagedMcpServerEntry? current)
                    && ReferenceEquals(current, staleEntry))
                {
                    _registry.TryRemove(key, out _);
                }
            }

            _mergedToolsByWorkspace.TryRemove(scopeWorkingDirectory, out _);

            if (staleEntries.Length == 0)
            {
                if (config.McpServers is { Count: > 0 })
                {
                    RegisterFromConfigCore(
                        config,
                        scopeWorkingDirectory,
                        sourceDigest);
                }

                return;
            }

            retirementState = new WorkspaceRetirementState(
                staleEntries);
            _workspaceRetirementStates[scopeWorkingDirectory] =
                retirementState;
        }
        finally
        {
            _registryLock.Release();
        }

        if (retirementState is null)
        {
            return;
        }

        StartWorkspaceRetirement(retirementState);

        await AwaitWorkspaceRetirementStateAsync(
                scopeWorkingDirectory,
                retirementState,
                cancellationToken)
            .ConfigureAwait(false);

        await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (config.McpServers is { Count: > 0 })
            {
                RegisterFromConfigCore(
                    config,
                    scopeWorkingDirectory,
                    sourceDigest);
            }
        }
        finally
        {
            _registryLock.Release();
        }
    }

    private async Task RetireWorkspaceEntriesAsync(
        string scopeWorkingDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await AwaitExistingWorkspaceRetirementAsync(
                scopeWorkingDirectory,
                cancellationToken)
            .ConfigureAwait(false);

        await _registryLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        WorkspaceRetirementState? retirementState = null;

        try
        {
            ManagedMcpServerEntry[] staleEntries = _registry.Values
                .Where(entry => string.Equals(
                    entry.ScopeWorkingDirectory,
                    scopeWorkingDirectory,
                    StringComparison.Ordinal))
                .ToArray();

            foreach (ManagedMcpServerEntry staleEntry in staleEntries)
            {
                staleEntry.MarkRetired();

                (string Name, string? WorkingDirectory) key =
                    (staleEntry.Name, staleEntry.ScopeWorkingDirectory);

                if (_registry.TryGetValue(key, out ManagedMcpServerEntry? current)
                    && ReferenceEquals(current, staleEntry))
                {
                    _registry.TryRemove(key, out _);
                }
            }

            _mergedToolsByWorkspace.TryRemove(scopeWorkingDirectory, out _);

            if (staleEntries.Length > 0)
            {
                retirementState = new WorkspaceRetirementState(
                    staleEntries);
                _workspaceRetirementStates[scopeWorkingDirectory] =
                    retirementState;
            }
        }
        finally
        {
            _registryLock.Release();
        }

        if (retirementState is null)
        {
            return;
        }

        StartWorkspaceRetirement(retirementState);

        await AwaitWorkspaceRetirementStateAsync(
                scopeWorkingDirectory,
                retirementState,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task AwaitExistingWorkspaceRetirementAsync(
        string scopeWorkingDirectory,
        CancellationToken cancellationToken)
    {
        if (!_workspaceRetirementStates.TryGetValue(
                scopeWorkingDirectory,
                out WorkspaceRetirementState? retirementState))
        {
            return;
        }

        if (retirementState.Completion.IsCompleted
            && !await retirementState.Completion.ConfigureAwait(false))
        {
            WorkspaceRetirementState retryState =
                new(retirementState.Entries);
            _workspaceRetirementStates[scopeWorkingDirectory] =
                retryState;
            retirementState = retryState;

            StartWorkspaceRetirement(retryState);
        }

        await AwaitWorkspaceRetirementStateAsync(
                scopeWorkingDirectory,
                retirementState,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task AwaitWorkspaceRetirementStateAsync(
        string scopeWorkingDirectory,
        WorkspaceRetirementState retirementState,
        CancellationToken cancellationToken)
    {
        bool retired = await retirementState.Completion
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!retired)
        {
            throw new McpTransportUnavailableException(
                "Previous MCP workspace servers could not finish shutting down. Retry the request.",
                McpRequestDispatchState.NotDispatched);
        }

        if (_workspaceRetirementStates.TryGetValue(
                scopeWorkingDirectory,
                out WorkspaceRetirementState? current)
            && ReferenceEquals(
                current,
                retirementState))
        {
            _workspaceRetirementStates.TryRemove(
                scopeWorkingDirectory,
                out _);
        }
    }

    private void StartWorkspaceRetirement(
        WorkspaceRetirementState retirementState)
    {
        Task retirement =
            CompleteWorkspaceRetirementsAsync(
                retirementState);

        TrackPendingWorkspaceRetirement(retirement);
    }

    private async Task CompleteWorkspaceRetirementsAsync(
        WorkspaceRetirementState retirementState)
    {
        bool allRetired = false;

        try
        {
            Task<bool>[] retirements =
                new Task<bool>[retirementState.Entries.Count];

            for (int index = 0;
                 index < retirementState.Entries.Count;
                 index++)
            {
                Task<bool> retirement =
                    RetireWorkspaceEntryWithDeadlineAsync(
                        retirementState.Entries[index]);

                TrackPendingWorkspaceRetirement(retirement);
                retirements[index] = retirement;
            }

            bool[] results = await Task.WhenAll(retirements)
                .ConfigureAwait(false);
            allRetired = results.All(
                static retired => retired);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed while retiring previous MCP workspace servers.");
        }
        finally
        {
            retirementState.TryComplete(
                allRetired);
        }
    }

    private async Task<bool> RetireWorkspaceEntryWithDeadlineAsync(
        ManagedMcpServerEntry entry)
    {
        using CancellationTokenSource cleanupDeadline =
            new(WorkspaceRetirementCleanupTimeout);

        try
        {
            return await RetireWorkspaceEntryAsync(
                    entry,
                    cleanupDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cleanupDeadline.IsCancellationRequested)
        {
            logger.LogWarning(
                "Timed out retiring an MCP workspace server after it was removed from the registry.");

            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to retire an MCP workspace server after it was removed from the registry.");

            return false;
        }
    }

    private async Task<bool> RetireWorkspaceEntryAsync(
        ManagedMcpServerEntry entry,
        CancellationToken cancellationToken)
    {
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await StopManagedServerCoreAsync(
                    entry)
                .ConfigureAwait(false);
        }
        finally
        {
            entry.State = McpServerState.Stopped;
            entry.Tools = [];
            entry.ErrorMessage = null;

            RemoveServerMetadataFromPartition(entry);
            entry.Gate.Release();
        }
    }

    private void TrackPendingWorkspaceRetirement(
        Task retirement)
    {
        _pendingWorkspaceRetirements[retirement] = 0;

        _ = retirement.ContinueWith(
            completed =>
            {
                _ = _pendingWorkspaceRetirements.TryRemove(
                    completed,
                    out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<McpConfigSnapshot?> ReadMcpConfigAsync(
        string configPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using SecureFileReadResult readResult = await SecureFileReader
                .ReadBytesAsync(
                    configPath,
                    MaxMcpConfigBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            if (readResult.Status is SecureFileReadStatus.TooLarge)
            {
                logger.LogError(
                    "MCP config at {ConfigPath} exceeds the maximum size of {MaxBytes} bytes.",
                    configPath,
                    MaxMcpConfigBytes);

                return null;
            }

            if (readResult.Status is SecureFileReadStatus.NotFound)
            {
                return null;
            }

            if (readResult.Status is not SecureFileReadStatus.Success)
            {
                logger.LogError(
                    "MCP config was rejected by secure file validation.");

                return null;
            }

            McpConfig? config = JsonSerializer.Deserialize(
                readResult.Bytes.Span,
                McpConfigJsonSerializerContext.Default.McpConfig);

            if (config is null)
            {
                logger.LogError(
                    "MCP config at {ConfigPath} did not contain a JSON object.",
                    configPath);

                return null;
            }

            string digest = Convert.ToHexString(
                SHA256.HashData(readResult.Bytes.Span));

            return new McpConfigSnapshot(config, digest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to read or parse MCP config at {ConfigPath}.",
                configPath);

            return null;
        }
    }

    private sealed class WorkspaceRetirementState(
        IReadOnlyList<ManagedMcpServerEntry> entries)
    {
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ManagedMcpServerEntry> Entries { get; } =
            entries;

        public Task<bool> Completion =>
            _completion.Task;

        public void TryComplete(bool retired) =>
            _completion.TrySetResult(retired);
    }

    private sealed record McpConfigSnapshot(
        McpConfig Config,
        string SourceDigest);

}
