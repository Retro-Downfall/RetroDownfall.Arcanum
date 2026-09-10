using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed class FileEncryptionKeyBootstrapHostedService(
    IFileEncryptionKeyStartupValidator startupValidator) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await startupValidator
            .ValidateStartupStateAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
