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

    private async Task<BuiltMcpToolSurface> BuildMergedToolsForWorkspaceAsync(
        McpPartitionClients partition,
        string workspaceKey,
        CancellationToken cancellationToken)
    {
        List<LoadedMcpToolRow> internalTagged = await EnsurePartitionInternalToolsAsync(partition, workspaceKey, cancellationToken).ConfigureAwait(false);

        List<LoadedMcpToolRow> workspaceLocalTagged = [];
        string? workspaceSourceDigest = null;
        bool cacheable = workspaceKey == NoWorkspaceKey;

        if (workspaceKey != NoWorkspaceKey)
        {
            string localPath = Path.Combine(workspaceKey, "mcp.json");

            McpConfigSnapshot? localSnapshot =
                await ReadMcpConfigAsync(localPath, cancellationToken).ConfigureAwait(false);

            if (localSnapshot is null)
            {
                await RetireWorkspaceEntriesAsync(workspaceKey, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                bool workspaceTrusted = await trustedMcpWorkspaces
                    .IsApprovedDigestAsync(
                        workspaceKey,
                        localSnapshot.SourceDigest,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!workspaceTrusted)
                {
                    await RetireWorkspaceEntriesAsync(workspaceKey, cancellationToken).ConfigureAwait(false);

                    return new BuiltMcpToolSurface(
                        McpToolMerger.MergeWorkspaceSurface(
                            internalTagged,
                            _globalFirstByToolName,
                            workspaceLocalTagged,
                            logger),
                        SourceDigest: null,
                        Cacheable: false);
                }

                TrustedMcpWorkspaceSnapshot trustSnapshot =
                    new(localSnapshot.SourceDigest, IsApproved: true);
                workspaceSourceDigest = localSnapshot.SourceDigest;
                cacheable = true;

                await ReconcileWorkspaceConfigAsync(
                        localSnapshot.Config,
                        workspaceKey,
                        localSnapshot.SourceDigest,
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (ManagedMcpServerEntry entry in _registry.Values.Where(
                             entry =>
                                 entry.ScopeWorkingDirectory == workspaceKey
                                 && trustSnapshot.Authorizes(entry.SourceDigest)))
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
                        Result startResult = await StartAsync(
                                entry.Name,
                                workspaceKey,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (startResult.IsFailure && entry.State is not McpServerState.Running)
                        {
                            SyncPartitionServerMetadata(entry);

                            continue;
                        }
                    }

                    if (!trustSnapshot.Authorizes(entry.SourceDigest))
                    {
                        continue;
                    }

                    AttachEntryToPartition(entry, partition, workspaceLocalTagged);
                }
            }
        }

        return new BuiltMcpToolSurface(
            McpToolMerger.MergeWorkspaceSurface(
                internalTagged,
                _globalFirstByToolName,
                workspaceLocalTagged,
                logger),
            workspaceSourceDigest,
            cacheable);
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
