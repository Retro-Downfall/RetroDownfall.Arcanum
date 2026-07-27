using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Boots managed MCP servers on host start and stops them on shutdown.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService MCP server bootstrap
public sealed class McpServerBootstrapHostedService(
    IMcpConnectionManager manager,
    IOptionsMonitor<ArcanumSettings> options,
    IHostApplicationLifetime lifetime,
    ILogger<McpServerBootstrapHostedService> logger) : IHostedService
{

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        bool blocksStartup = options.CurrentValue.ResolveMcp().BootstrapBlocksStartup;

        if (!blocksStartup)
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await manager.InitializeAsync(lifetime.ApplicationStopping).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Background MCP server bootstrap failed. Tools will be unavailable until the host is restarted.");
                    }
                },
                CancellationToken.None);

            return Task.CompletedTask;
        }

        return manager.InitializeAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return manager.StopAllAsync(cancellationToken);
    }

}
