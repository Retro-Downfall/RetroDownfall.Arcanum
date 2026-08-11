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
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

public sealed partial class McpConnectionManager
{

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

            return new Error(ErrorCodes.Mcp.ServerNotFound, $"MCP server '{name}' was not found.");
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
            return new Error(ErrorCodes.Mcp.ServerNotFound, $"MCP server '{name}' was not found.");
        }

        return new Error(ErrorCodes.Mcp.AmbiguousServer, $"Multiple MCP servers named '{name}' exist; specify workingDirectory.");
    }

    internal ManagedMcpServerEntry? GetManagedEntryForTests(
        string name,
        string? workingDirectory)
    {
        Result<ManagedMcpServerEntry> resolved =
            ResolveEntry(name, workingDirectory);

        return resolved.IsSuccess ? resolved.Value : null;
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


    /// <summary>
    /// Per-partition registry of live MCP clients and their advertised server metadata. Both
    /// collections are mutated under three disjoint locks — the per-workspace merge lock, the global
    /// init lock, and a per-entry gate on the fire-and-forget transport-ended path — and were read
    /// with no lock at all by the status surface. They therefore own their own gate instead of
    /// relying on any caller's, and every reader takes a snapshot inside it.
    /// </summary>
    private sealed class McpPartitionClients
    {

        private readonly object _gate = new();

        private readonly List<IMcpClient> _clients = [];

        private readonly List<McpServerMetadata> _servers = [];

        public IMcpClient? InternalClient { get; set; }

        public bool InternalServerStarted { get; set; }

        public IReadOnlyList<LoadedMcpToolRow>? CachedInternalTools { get; set; }

        public bool AddClientIfAbsent(IMcpClient client)
        {

            lock (_gate)
            {

                if (_clients.Contains(client))
                {

                    return false;

                }

                _clients.Add(client);

                return true;

            }

        }

        public bool RemoveClient(IMcpClient client)
        {

            lock (_gate)
            {

                return _clients.Remove(client);

            }

        }

        public IMcpClient[] SnapshotClients()
        {

            lock (_gate)
            {

                return [.. _clients];

            }

        }

        /// <summary>
        /// Removes and returns every client so shutdown can dispose them outside the gate.
        /// </summary>
        public IMcpClient[] DrainClients()
        {

            lock (_gate)
            {

                IMcpClient[] snapshot = [.. _clients];

                _clients.Clear();

                return snapshot;

            }

        }

        public void UpsertServer(McpServerMetadata metadata)
        {

            lock (_gate)
            {

                int existingIndex = _servers.FindIndex(
                    m => string.Equals(m.ServerName, metadata.ServerName, StringComparison.Ordinal));

                if (existingIndex >= 0)
                {

                    _servers[existingIndex] = metadata;

                }
                else
                {

                    _servers.Add(metadata);

                }

            }

        }

        public void RemoveServer(string serverName)
        {

            lock (_gate)
            {

                _ = _servers.RemoveAll(m => string.Equals(m.ServerName, serverName, StringComparison.Ordinal));

            }

        }

        public McpServerMetadata[] SnapshotServers()
        {

            lock (_gate)
            {

                return [.. _servers];

            }

        }

    }

    private sealed record McpServerMetadata(
        string ServerName,
        string Status,
        List<string> ToolNames,
        string? ErrorMessage);

    private sealed record CachedMcpToolSurface(
        long Generation,
        string? SourceDigest,
        IReadOnlyList<AITool> Tools);

    private sealed record BuiltMcpToolSurface(
        IReadOnlyList<AITool> Tools,
        string? SourceDigest,
        bool Cacheable);

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
        return ArcanumPaths.GlobalMcpConfigFile;
    }

}
