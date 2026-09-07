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

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

public sealed partial class McpConnectionManager
{
    private async Task EnsureGlobalLoadedAsync(
        McpGlobalInitializationAuthority authority,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task operation;

            await _globalInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                ThrowIfGlobalInitializationStopped();

                long generation = Volatile.Read(ref _toolSurfaceGeneration);

                if (Volatile.Read(ref _globalSurfaceRevision) == generation)
                {
                    return;
                }

                if (_globalLifecycleState is GlobalLifecycleState.Reloading)
                {
                    operation = _reloadCompleted.Task;
                }
                else if (_globalInitOperation is { IsCompleted: false } inFlight)
                {
                    operation = inFlight;
                }
                else
                {
                    if (!_lifecycleAdmission.TryEnter(out IAsyncDisposable? admitted))
                    {
                        throw ManagerStoppedException();
                    }

                    _globalInitAuthority = authority;

                    operation = RunGlobalInitOperationAsync(
                        authority,
                        admitted!,
                        _globalInitializationLifetime.Token);

                    _globalInitOperation = operation;
                }
            }
            finally
            {
                _globalInitLock.Release();
            }

            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunGlobalInitOperationAsync(
        McpGlobalInitializationAuthority authority,
        IAsyncDisposable lifecycle,
        CancellationToken cancellationToken)
    {
        try
        {
            if (authority is McpGlobalInitializationAuthority.PreReadinessStartup)
            {
                await RunGlobalInitCoreAsync(cancellationToken).ConfigureAwait(false);

                return;
            }

            await RunOrdinaryGlobalInitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lifecycle.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunOrdinaryGlobalInitAsync(CancellationToken cancellationToken)
    {
        IGrimoireConnectionAdmissionGate admission = Volatile.Read(ref _globalAdmission)
            ?? throw new InvalidOperationException(
                "MCP ordinary initialization requires configured Grimoire admission.");

        while (true)
        {
            long observedGeneration = admission.CurrentGeneration;

            if (!admission.TryAcquireWorkLease(
                    GrimoireWorkKind.McpServerBootstrap,
                    out IGrimoireWorkLease? admitted))
            {
                _ = await admission.WaitForOpenGenerationAfterRefusalAsync(
                        observedGeneration,
                        cancellationToken)
                    .ConfigureAwait(false);

                continue;
            }

            bool retryAfterMaintenance = false;

            await using (IGrimoireWorkLease work = admitted!)
            {
                observedGeneration = work.Generation;

                if (!work.TryBeginExternalEffectGroup(
                        out IGrimoireExternalEffectGroup? admittedGroup))
                {
                    retryAfterMaintenance = true;
                }
                else
                {
                    await using IGrimoireExternalEffectGroup effectGroup = admittedGroup!;

                    await RunGlobalInitCoreAsync(cancellationToken).ConfigureAwait(false);

                    return;
                }
            }

            if (retryAfterMaintenance)
            {
                _ = await admission.WaitForOpenGenerationAfterRefusalAsync(
                        observedGeneration,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RunGlobalInitCoreAsync(CancellationToken cancellationToken)
    {
        await EnsureGlobalRegistryLoadedAsync(cancellationToken).ConfigureAwait(false);

        List<string> bootstrapFailures = [];

        HashSet<ManagedMcpServerEntry> attemptedStarts = [];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<ManagedMcpServerEntry> needStart = [];

            await _globalInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                ThrowIfGlobalInitializationStopped();

                foreach (ManagedMcpServerEntry entry in _registry.Values.Where(
                             static candidate => candidate.ScopeWorkingDirectory is null))
                {
                    if ((!entry.AlwaysOn && entry.State is not McpServerState.Running)
                        || IsRestartBackoffActive(entry))
                    {
                        continue;
                    }

                    if (entry.State is not McpServerState.Running
                        && !attemptedStarts.Contains(entry))
                    {
                        needStart.Add(entry);
                    }
                }
            }
            finally
            {
                _globalInitLock.Release();
            }

            foreach (ManagedMcpServerEntry entry in needStart)
            {
                attemptedStarts.Add(entry);

                Result startResult = await StartCoreAsync(
                        entry.Name,
                        workingDirectory: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (startResult.IsFailure && entry.State is not McpServerState.Running)
                {
                    bootstrapFailures.Add($"{entry.Name}: {startResult.Error.Message}");
                }
            }

            long projectedGeneration = Volatile.Read(ref _toolSurfaceGeneration);

            McpPartitionClients globalPartition = GetOrCreatePartition(GlobalPartitionKey);

            List<LoadedMcpToolRow> tagged = [];

            bool unstartedAlwaysOnFound = false;

            ManagedMcpServerEntry[] globalEntries = _registry.Values
                .Where(static candidate => candidate.ScopeWorkingDirectory is null)
                .ToArray();

            foreach (ManagedMcpServerEntry entry in globalEntries)
            {
                await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    if (!IsCurrentRegistryEntry(entry)
                        || (!entry.AlwaysOn && entry.State is not McpServerState.Running)
                        || IsRestartBackoffActive(entry))
                    {
                        continue;
                    }

                    if (entry.State is not McpServerState.Running)
                    {
                        SyncPartitionServerMetadata(entry);

                        if (!attemptedStarts.Contains(entry))
                        {
                            unstartedAlwaysOnFound = true;
                        }

                        continue;
                    }

                    AttachEntryToPartition(entry, globalPartition, tagged);
                }
                finally
                {
                    entry.Gate.Release();
                }
            }

            if (unstartedAlwaysOnFound)
            {
                continue;
            }

            McpToolMerger.GlobalDedupResult deduped =
                McpToolMerger.DedupeGlobalTaggedTools(tagged);

            bool published = false;

            await _globalInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                ThrowIfGlobalInitializationStopped();

                if (projectedGeneration == Volatile.Read(ref _toolSurfaceGeneration))
                {
                    FinalizeGlobalState(deduped, projectedGeneration);

                    published = true;
                }
            }
            finally
            {
                _globalInitLock.Release();
            }

            if (!published)
            {
                continue;
            }

            if (bootstrapFailures.Count > 0)
            {
                logger.LogWarning(
                    "MCP bootstrap: {FailureCount} always-on server(s) failed to start: {Failures}",
                    bootstrapFailures.Count,
                    string.Join("; ", bootstrapFailures));
            }

            return;
        }
    }

    private void FinalizeGlobalState(
        McpToolMerger.GlobalDedupResult deduped,
        long projectedGeneration)
    {
        _globalFirstByToolName = deduped.FirstByToolName;

        _globalSurfaceTools = deduped.SurfaceTools;

        Volatile.Write(ref _globalSurfaceRevision, projectedGeneration);
    }

    private void InvalidateCachesForServer(ManagedMcpServerEntry entry)
    {
        _ = Interlocked.Increment(
            ref _toolSurfaceGeneration);

        _mergedToolsByWorkspace.Clear();
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

        partition.UpsertServer(metadata);
    }

    private void RemoveServerMetadataFromPartition(ManagedMcpServerEntry entry)
    {
        string partitionKey = entry.ScopeWorkingDirectory is null ? GlobalPartitionKey : entry.ScopeWorkingDirectory;

        if (!_partitionClients.TryGetValue(partitionKey, out Lazy<McpPartitionClients>? partitionLazy)
            || !partitionLazy.IsValueCreated)
        {
            return;
        }

        partitionLazy.Value.RemoveServer(entry.Name);
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

    /// <summary>
    /// Drops a partition that is about to have its in-process <c>arcanum-internal</c> client
    /// disposed, so the caller's rebuild starts a fresh one. Leaving it registered would let
    /// <see cref="EnsurePartitionInternalToolsAsync"/> short-circuit on its cached internal tool
    /// rows and hand every later caller a surface bound to a retired client generation.
    /// </summary>
    private void UnregisterPartition(
        string partitionKey,
        McpPartitionClients partition)
    {
        // Identity-checked so a replacement created concurrently under the same key is never dropped.
        if (!_partitionClients.TryGetValue(
                partitionKey,
                out Lazy<McpPartitionClients>? registered)
            || !registered.IsValueCreated
            || !ReferenceEquals(registered.Value, partition))
        {
            return;
        }

        _ = _partitionClients.TryRemove(
            new KeyValuePair<string, Lazy<McpPartitionClients>>(
                partitionKey,
                registered));
    }
}
