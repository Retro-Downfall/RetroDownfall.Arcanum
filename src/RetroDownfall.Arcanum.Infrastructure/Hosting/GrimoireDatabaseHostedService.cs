using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

[ExcludeFromCodeCoverage] // Reason: IHostedService DB bootstrap
public sealed class GrimoireDatabaseHostedService(
    IServiceScopeFactory scopeFactory,
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource,
    IInstallationStartupProbe? startupProbe = null)
    : IHostedService, IDisposable
{

    private readonly IInstallationStartupProbe _startupProbe =
        startupProbe ?? InstallationStartupProbe.CreateDefault();

    private ArcanumMaintenanceLock? _maintenanceLock;

    private string _maintenanceDirectory = ArcanumPaths.GrimoireDirectory;

    internal GrimoireDatabaseHostedService(
        IServiceScopeFactory scopeFactory,
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        string maintenanceDirectory,
        IInstallationStartupProbe? startupProbe = null)
        : this(scopeFactory, secretStore, passphraseSource, startupProbe)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(maintenanceDirectory);

        _maintenanceDirectory = maintenanceDirectory;

    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Result<ActiveInstallationReset?> activeRead = await _startupProbe
            .ReadActiveResetAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeRead.IsFailure)
        {

            InvalidOperationException exception = new(
                "Installation reset recovery state could not be read safely. "
                + activeRead.Error.Message);

            await MarkFailedAsync(exception).ConfigureAwait(false);

            throw exception;

        }

        if (activeRead.Value is not null)
        {

            InvalidOperationException exception = new(
                "An installation factory reset is active. Resume it before starting the host.");

            await MarkFailedAsync(exception).ConfigureAwait(false);

            throw exception;

        }

        // Held for the host's lifetime so installation-wide maintenance cannot overlap hosted
        // writers. Startup fails closed when another process owns the lock.
        _maintenanceLock = ArcanumMaintenanceLock.TryAcquire(_maintenanceDirectory);

        if (_maintenanceLock is null)
        {

            InvalidOperationException exception = new(
                "The Arcanum maintenance lock is unavailable; a reset, restore, or another host may be running.");

            await MarkFailedAsync(exception).ConfigureAwait(false);

            throw exception;

        }

        ResolveInterruptedRestores();

        try
        {
            await GrimoireDatabaseBootstrapper
                .EnsureInitializedAsync(secretStore, passphraseSource, scopeFactory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Ensure WaitUntilReadyAsync cannot hang if bootstrap throws before MarkReady.
            await MarkFailedAsync(ex).ConfigureAwait(false);
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

    private async Task MarkFailedAsync(Exception exception)
    {

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IGrimoireDbReadiness readiness =
            scope.ServiceProvider.GetRequiredService<IGrimoireDbReadiness>();

        readiness.MarkFailed(exception);

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
