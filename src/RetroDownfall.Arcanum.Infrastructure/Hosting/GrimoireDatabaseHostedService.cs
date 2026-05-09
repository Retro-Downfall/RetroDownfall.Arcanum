using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

public sealed class GrimoireDatabaseHostedService(
    IServiceScopeFactory scopeFactory,
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        GrimoireDatabaseBootstrapper.EnsureInitializedAsync(secretStore, passphraseSource, scopeFactory, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
