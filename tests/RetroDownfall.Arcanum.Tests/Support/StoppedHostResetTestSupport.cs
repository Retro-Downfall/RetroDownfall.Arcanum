using System.Globalization;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Seams for observing, and interrupting, the protected erasure a stopped-host global reset runs, over
/// the same command composition the executable uses.
/// </summary>
/// <remarks>
/// Everything here substitutes a collaborator at its own seam and nothing else: the coordinator's
/// constructor takes its fault seams as optional trailing arguments, and the offline cleanup is a
/// registered port. The rest of the composition (the lock, the active store, the journal, the real
/// SQLCipher catalog) is production.
/// </remarks>
internal static class StoppedHostResetTestSupport
{
    /// <summary>
    /// Replaces the coordinator registration with one that carries the given fault seams, built from
    /// exactly the arguments the production registration passes.
    /// </summary>
    internal static void UseFaultSeams(
        IServiceCollection services,
        CovenantErasureFaultSeam? faultSeam,
        GrimoireOfflineTransitionTerminalSuffixFaultSeam? terminalSuffixFaultSeam = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<CovenantErasureCoordinator>();

        services.AddScoped(
            sp => new CovenantErasureCoordinator(
                sp.GetRequiredService<ILongRunningOperationCoordinator>(),
                sp.GetRequiredService<ILongRunningOperationStore>(),
                sp.GetRequiredService<ICovenantOperationGate>(),
                sp.GetRequiredService<ICovenantProtectedArtifactErasureKernel>(),
                sp.GetRequiredService<ICovenantManagedFileErasureKernel>(),
                sp.GetRequiredService<ICovenantErasureInventorySource>(),
                sp.GetRequiredService<ICovenantErasureTransition>(),
                sp.GetRequiredService<ICovenantDisclosureWriterLifecycle>(),
                sp.GetRequiredService<IGrimoireOfflineTransitionPhaseAuthority>(),
                sp.GetRequiredService<GrimoireOfflineTransitionEffectHandlerRegistry>(),
                sp.GetRequiredService<IGrimoireConnectionAdmissionGate>(),
                sp.GetRequiredService<IGrimoireMaintenanceConnectionFactory>(),
                sp.GetRequiredService<IGrimoireMaintenancePathAuthority>(),
                sp.GetRequiredService<IGrimoireDbPassphraseSource>(),
                sp.GetRequiredService<ICovenantClosedPeriodLedgerConnection>(),
                sp.GetRequiredService<GrimoireRequestAdmissionScope>(),
                sp.GetRequiredService<ICovenantConnectionDrain>(),
                sp.GetRequiredService<GrimoireOfflineTransitionDatabaseReconciler>(),
                sp.GetRequiredService<LongRunningOperationOwnership>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<CovenantErasureCoordinator>>(),
                faultSeam,
                terminalSuffixFaultSeam));
    }

    /// <summary>
    /// Wraps the offline cleanup so a callback sees the Grimoire exactly as the data phase left it,
    /// immediately before the cleanup deletes it.
    /// </summary>
    internal static void BeforeOfflineCleanup(
        IServiceCollection services,
        Func<Task> observe)
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(observe);

        services.RemoveAll<IInstallationResetOfflineCleanup>();

        services.AddScoped<IInstallationResetOfflineCleanup>(
            sp => new ObservingOfflineCleanup(
                ActivatorUtilities.CreateInstance<InstallationResetOfflineCleanup>(sp),
                observe));
    }

    /// <summary>The passphrase the fixture's template database is keyed from.</summary>
    internal static string FixturePassphrase { get; } =
        GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
            GrimoireFixture.TestGrimoireSecret,
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

    /// <summary>Reads one scalar from the Grimoire on its own read-only connection.</summary>
    internal static async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = ArcanumPaths.GrimoireDatabaseFile,
            Password = FixturePassphrase,
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());

        await connection.OpenAsync(CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);
    }

    /// <summary>Adds one ordinary Session, which only the ordinary cleanup of a factory reset deletes.</summary>
    internal static async Task<Guid> SeedOrdinarySessionAsync(GrimoireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using ArcanumDbContext context = fixture.CreateContext(ArcanumPaths.GrimoireDatabaseFile);

        Guid sessionId = Guid.NewGuid();

        context.Sessions.Add(new Session
        {
            Id = sessionId,
            Status = "archived",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        await context.SaveChangesAsync();

        await context.Database.CloseConnectionAsync();

        return sessionId;
    }

    /// <summary>
    /// Puts the Grimoire in write-ahead logging, the journal mode every installation really has, so a
    /// case about sidecars is about the shape of a real database.
    /// </summary>
    internal static async Task UseWriteAheadLoggingAsync(GrimoireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using (ArcanumDbContext context = fixture.CreateContext(ArcanumPaths.GrimoireDatabaseFile))
        {
            _ = await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

            await context.Database.CloseConnectionAsync();
        }

        SqliteConnection.ClearAllPools();

        File.Delete(ArcanumPaths.GrimoireDatabaseFile + "-wal");

        File.Delete(ArcanumPaths.GrimoireDatabaseFile + "-shm");
    }

    private sealed class ObservingOfflineCleanup(
        IInstallationResetOfflineCleanup inner,
        Func<Task> observe) : IInstallationResetOfflineCleanup
    {
        public Task<Result<InstallationResetFileSystemInventory>> PlanAsync(
            string[] selectedRoots,
            string[] excludedRoots,
            CancellationToken cancellationToken) =>
            inner.PlanAsync(selectedRoots, excludedRoots, cancellationToken);

        public async Task<Result<InstallationResetOfflineCleanupResult>> ExecuteAsync(
            InstallationResetPlan plan,
            CancellationToken cancellationToken)
        {
            await observe().ConfigureAwait(false);

            return await inner.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        }
    }
}
