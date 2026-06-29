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

    /// <inheritdoc />
    public async Task<Result> TrustWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {

            return new Error(ErrorCodes.Mcp.MissingWorkspace, "workingDirectory is required to trust a workspace-local mcp.json.");

        }

        string normalized;

        try
        {

            normalized = TrustedMcpWorkspaceStore.NormalizeWorkspaceRoot(workingDirectory);

        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {

            return new Error("Mcp.InvalidWorkspace", "workingDirectory is not a valid path.");

        }

        string mcpPath = Path.Combine(normalized, "mcp.json");

        if (!File.Exists(mcpPath))
        {

            return new Error("Mcp.MissingConfig", "Workspace mcp.json was not found.");

        }

        try
        {

            await trustedMcpWorkspaces.TrustAsync(normalized, cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Failed to trust workspace MCP config at {Workspace}.", normalized);

            return new Error("Mcp.TrustFailed", "Could not record workspace MCP approval.");

        }

        logger.LogInformation("Workspace MCP config trusted at {Workspace}.", normalized);

        return Result.Success();

    }
    private async Task<bool> IsWorkspaceServerVisibleAsync(
        ManagedMcpServerEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.ScopeWorkingDirectory is null)
        {
            return true;
        }

        return await trustedMcpWorkspaces
            .IsTrustedAsync(entry.ScopeWorkingDirectory, cancellationToken)
            .ConfigureAwait(false);
    }
}
