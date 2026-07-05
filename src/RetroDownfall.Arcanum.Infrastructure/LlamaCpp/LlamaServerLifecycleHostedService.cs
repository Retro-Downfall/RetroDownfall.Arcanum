using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

/// <summary>
/// Sweeps orphaned <c>llama-server</c> processes left running by a previous, uncleanly-terminated
/// Arcanum run on startup (see <see cref="LlamaProcessRegistry"/>), then stops all managed
/// <c>llama-server</c> processes when this host shuts down normally.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService llama-server lifecycle
public sealed class LlamaServerLifecycleHostedService(
    ILlamaServerManager manager,
    ILogger<LlamaServerLifecycleHostedService> logger) : IHostedService
{

    public Task StartAsync(CancellationToken cancellationToken)
    {

        LlamaProcessRegistry.SweepOrphans(logger);

        return Task.CompletedTask;

    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        manager.StopAllAsync(cancellationToken);

}
