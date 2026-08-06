using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

[ExcludeFromCodeCoverage] // Reason: IHostedService DB bootstrap
public sealed class GrimoireDatabaseHostedService(
    IServiceScopeFactory scopeFactory,
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource)
    : IHostedService, IDisposable
{

    private ArcanumMaintenanceLock? _maintenanceLock;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ResolveInterruptedRestores();

        // Held for the host's lifetime so `arcanum backup restore` can tell that a live process owns
        // this installation. Best-effort on purpose: failing to take it must never stop the host,
        // and a restore refuses on its own when it cannot take the lock itself.
        _maintenanceLock = ArcanumMaintenanceLock.TryAcquire(ArcanumPaths.GrimoireDirectory);

        if (_maintenanceLock is null)
        {

            Log.Warning(
                "The Arcanum maintenance lock is already held; a restore or another host may be running.");

        }

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
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GrimoireDatabaseBootstrapper
                .CheckpointOnShutdownAsync(passphraseSource, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        _maintenanceLock?.Dispose();

        _maintenanceLock = null;
    }

    /// <summary>
    /// Finishes or reverses a restore that a previous process death left mid-commit (issue #38).
    /// This runs before the database is opened so the host never bootstraps against a half-swapped
    /// tree; each resolution is logged so an operator can see what happened.
    /// </summary>
    private static void ResolveInterruptedRestores()
    {
        try
        {
            foreach (BackupRestoreRecoveryReport report in
                     BackupRestoreRecovery.Resolve(ArcanumPaths.GrimoireDirectory))
            {
                Log.Warning(
                    "Interrupted Arcanum restore resolved as {Outcome} at phase {Phase}: {Detail}",
                    report.Outcome,
                    report.Phase,
                    report.Detail);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Interrupted-restore recovery could not run; continuing startup.");
        }
    }
}
