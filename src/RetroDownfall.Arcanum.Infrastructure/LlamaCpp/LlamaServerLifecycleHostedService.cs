using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;

namespace RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

/// <summary>
/// Stops all managed <c>llama-server</c> processes when the host shuts down.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService llama-server lifecycle
public sealed class LlamaServerLifecycleHostedService(ILlamaServerManager manager) : IHostedService
{

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) =>
        manager.StopAllAsync(cancellationToken);

}
