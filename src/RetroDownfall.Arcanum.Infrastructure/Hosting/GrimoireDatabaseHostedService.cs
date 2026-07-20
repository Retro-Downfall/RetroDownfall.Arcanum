using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

[ExcludeFromCodeCoverage] // Reason: IHostedService DB bootstrap
public sealed class GrimoireDatabaseHostedService(
    IServiceScopeFactory scopeFactory,
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GrimoireDatabaseBootstrapper
                .EnsureInitializedAsync(secretStore, passphraseSource, scopeFactory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Ensure WaitUntilReadyAsync cannot hang if bootstrap throws before MarkReady.
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IGrimoireDbReadiness readiness = scope.ServiceProvider.GetRequiredService<IGrimoireDbReadiness>();
            readiness.MarkFailed(ex);
            throw;
        }
    }

    // W3.4 Group D #9: checkpoint the WAL on graceful shutdown so the -wal/-shm sidecar files
    // do not persist across restarts. Best-effort: failures are logged inside the helper and
    // never block shutdown.
    public Task StopAsync(CancellationToken cancellationToken) =>
        GrimoireDatabaseBootstrapper.CheckpointOnShutdownAsync(passphraseSource, cancellationToken);
}
