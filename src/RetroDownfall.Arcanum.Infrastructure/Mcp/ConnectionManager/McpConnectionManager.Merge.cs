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

                        return McpToolMerger.MergeWorkspaceSurface(internalTagged, _globalFirstByToolName, workspaceLocalTagged, logger);

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

        return McpToolMerger.MergeWorkspaceSurface(internalTagged, _globalFirstByToolName, workspaceLocalTagged, logger);
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


}
