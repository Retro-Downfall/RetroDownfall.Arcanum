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
        return ArcanumPaths.GlobalMcpConfigFile;
    }

}
