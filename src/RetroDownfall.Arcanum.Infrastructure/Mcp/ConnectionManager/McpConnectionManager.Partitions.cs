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
        McpToolMerger.GlobalDedupResult deduped = McpToolMerger.DedupeGlobalTaggedTools(tagged);

        _globalFirstByToolName = deduped.FirstByToolName;

        _globalSurfaceTools = deduped.SurfaceTools;

        _globalInitialized = true;
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
}
