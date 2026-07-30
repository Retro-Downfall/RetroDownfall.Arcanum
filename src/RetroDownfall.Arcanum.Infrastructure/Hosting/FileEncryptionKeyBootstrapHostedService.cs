using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed class FileEncryptionKeyBootstrapHostedService(
    IFileEncryptionKeyProvider keyProvider) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = await keyProvider.GetForWriteAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
