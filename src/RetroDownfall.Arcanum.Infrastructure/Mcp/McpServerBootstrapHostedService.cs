using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Boots managed MCP servers on host start and stops them on shutdown.
/// </summary>
public sealed class McpServerBootstrapHostedService(IMcpConnectionManager manager) : IHostedService
{

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return manager.InitializeAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return manager.StopAllAsync(cancellationToken);
    }

}
