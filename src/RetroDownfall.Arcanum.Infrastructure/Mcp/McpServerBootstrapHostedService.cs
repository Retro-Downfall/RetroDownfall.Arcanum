using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
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
    IOptionsMonitor<ArcanumSettings> options) : IHostedService
{

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        bool blocksStartup = options.CurrentValue.Mcp?.BootstrapBlocksStartup ?? true;

        if (!blocksStartup)
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await manager.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Background bootstrap failures surface on first MCP tool use; host startup must not block.
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
