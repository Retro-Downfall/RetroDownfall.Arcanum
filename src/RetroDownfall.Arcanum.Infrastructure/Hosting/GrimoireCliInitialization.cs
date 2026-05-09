using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

public sealed class GrimoireCliInitialization(
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource,
    IServiceScopeFactory scopeFactory) : IGrimoireCliInitialization
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private volatile bool _completed;

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_completed)
            {
                return;
            }

            await GrimoireDatabaseBootstrapper
                .EnsureInitializedAsync(secretStore, passphraseSource, scopeFactory, cancellationToken)
                .ConfigureAwait(false);

            _completed = true;
        }
        finally
        {
            _mutex.Release();
        }
    }
}
