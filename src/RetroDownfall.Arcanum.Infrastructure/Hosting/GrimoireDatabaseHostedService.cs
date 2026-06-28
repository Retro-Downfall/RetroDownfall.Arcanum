using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

[ExcludeFromCodeCoverage] // Reason: IHostedService DB bootstrap
public sealed class GrimoireDatabaseHostedService(
    IServiceScopeFactory scopeFactory,
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        GrimoireDatabaseBootstrapper.EnsureInitializedAsync(secretStore, passphraseSource, scopeFactory, cancellationToken);

    // W3.4 Group D #9: checkpoint the WAL on graceful shutdown so the -wal/-shm sidecar files
    // do not persist across restarts. Best-effort: failures are logged inside the helper and
    // never block shutdown.
    public Task StopAsync(CancellationToken cancellationToken) =>
        GrimoireDatabaseBootstrapper.CheckpointOnShutdownAsync(passphraseSource, cancellationToken);
}
