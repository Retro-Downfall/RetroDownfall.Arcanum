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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return new Error("Mcp.MissingConfig", "Workspace mcp.json was not found.");
        }
        catch (TrustedMcpWorkspaceStoreException ex)
        {
            logger.LogWarning(ex, "Failed to trust workspace MCP config at {Workspace}.", normalized);

            return new Error("Mcp.TrustFailed", ex.Message);
        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Failed to trust workspace MCP config at {Workspace}.", normalized);

            return new Error(
                "Mcp.TrustFailed",
                "Could not record workspace MCP approval. Verify storage permissions and retry.");

        }

        logger.LogInformation("Workspace MCP config trusted at {Workspace}.", normalized);

        return Result.Success();

    }
    private async Task<bool> IsWorkspaceServerVisibleAsync(
        ManagedMcpServerEntry entry,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentRegistryEntry(entry))
        {
            return false;
        }

        if (entry.ScopeWorkingDirectory is null)
        {
            return true;
        }

        if (entry.SourceDigest is null)
        {
            return false;
        }

        TrustedMcpWorkspaceSnapshot snapshot = await trustedMcpWorkspaces
            .GetSnapshotAsync(
                entry.ScopeWorkingDirectory,
                cancellationToken)
            .ConfigureAwait(false);

        return snapshot.Authorizes(entry.SourceDigest)
            && IsCurrentRegistryEntry(entry);
    }

    private bool IsCurrentRegistryEntry(ManagedMcpServerEntry entry)
    {
        if (entry.IsRetired)
        {
            return false;
        }

        return _registry.TryGetValue(
                   (entry.Name, entry.ScopeWorkingDirectory),
                   out ManagedMcpServerEntry? current)
            && ReferenceEquals(current, entry);
    }

    private static Error WorkspaceNotTrustedError() =>
        new(
            ErrorCodes.Mcp.WorkspaceNotTrusted,
            "Workspace-local MCP servers require operator approval. "
            + "POST /api/mcp/trust-workspace for this workspace before starting.");

    private static Error EntryNotFoundError(ManagedMcpServerEntry entry) =>
        new(ErrorCodes.Mcp.ServerNotFound, $"MCP server '{entry.Name}' was not found.");
}
