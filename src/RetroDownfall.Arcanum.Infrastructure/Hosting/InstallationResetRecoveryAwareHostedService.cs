using Microsoft.Extensions.Hosting;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Prevents an ordinary application hosted service from starting in a process admitted solely to
/// replay an installation factory reset. The lock-first database host publishes recovery admission
/// before Generic Host advances to these wrappers.
/// </summary>
internal sealed class InstallationResetRecoveryAwareHostedService<TService>(
    TService service,
    InstallationResetApiAdmission? admission)
    : IHostedService,
      IDisposable
    where TService : class, IHostedService
{

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private bool _started;

    public async Task StartAsync(CancellationToken cancellationToken)
    {

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            if (admission?.ActiveRecovery is not null)
            {

                return;

            }

            await service.StartAsync(cancellationToken).ConfigureAwait(false);

            _started = true;

        }
        finally
        {

            _lifecycleGate.Release();

        }

    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {

        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {

            if (!_started)
            {

                return;

            }

            _started = false;

            await service.StopAsync(cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _lifecycleGate.Release();

        }

    }

    public void Dispose()
    {

        // SemaphoreSlim owns no unmanaged resource. Some Generic Host/TestServer disposal paths
        // dispose service-provider singletons before their final StopAsync pass; disposing this
        // gate there would make the required stop of an already-started inner writer impossible.
        // Leave the managed gate to GC so Stop remains serialized and idempotent in either order.

    }

}
