using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class GrimoireMaintenanceConnectionFactory
    : IGrimoireMaintenanceConnectionFactory
{

    private readonly IGrimoireDbPassphraseSource _passphraseSource;

    private readonly ICovenantSqliteConnectionInitializer _initializer;

    private readonly ISqliteNativeRuntime _nativeRuntime;

    public GrimoireMaintenanceConnectionFactory(
        IGrimoireDbPassphraseSource passphraseSource,
        ICovenantSqliteConnectionInitializer initializer,
        ISqliteNativeRuntime nativeRuntime)
    {

        ArgumentNullException.ThrowIfNull(passphraseSource);

        ArgumentNullException.ThrowIfNull(initializer);

        ArgumentNullException.ThrowIfNull(nativeRuntime);

        _passphraseSource = passphraseSource;

        _initializer = initializer;

        _nativeRuntime = nativeRuntime;

    }

    /// <summary>Opens the closed period's connection for the one transaction that empties the Covenant family.</summary>
    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCanonicalErasureAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        OpenJournalAsync(
            capability,
            CovenantMaintenanceConnectionPurpose.CanonicalErasure,
            lane,
            cancellationToken);

    /// <summary>Opens the closed period's connection for a checked write-ahead-log truncation and the sidecar settle before it.</summary>
    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalWalTruncationAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        OpenJournalAsync(
            capability,
            CovenantMaintenanceConnectionPurpose.WalTruncation,
            lane,
            cancellationToken);

    /// <summary>Opens the closed period's connection for vacuuming, exporting, and restoring the journal mode after a replace.</summary>
    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCompactionAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        OpenJournalAsync(
            capability,
            CovenantMaintenanceConnectionPurpose.Compaction,
            lane,
            cancellationToken);

    /// <summary>Opens the closed period's connection for proving an exported candidate before the destination is touched.</summary>
    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalExportVerificationAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        OpenJournalAsync(
            capability,
            CovenantMaintenanceConnectionPurpose.IntegrityVerification,
            lane,
            cancellationToken);

    /// <summary>Opens the closed period's connection for preparing the empty search accelerator a fresh install would have.</summary>
    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalAcceleratorInitializationAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        OpenJournalAsync(
            capability,
            CovenantMaintenanceConnectionPurpose.AcceleratorInitialization,
            lane,
            cancellationToken);

    /// <summary>Opens the closed period's connection for the immutable read that verifies the candidate without writing a sidecar.</summary>
    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCandidateReopenAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        OpenJournalAsync(
            capability,
            CovenantMaintenanceConnectionPurpose.ReopenVerification,
            lane,
            cancellationToken);

    /// <summary>Opens the closed period's connection for the bounded read-only snapshot every closed-period inventory page comes from.</summary>
    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalInventorySnapshotAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        OpenJournalAsync(
            capability,
            CovenantMaintenanceConnectionPurpose.InventorySnapshot,
            lane,
            cancellationToken);

    /// <summary>
    /// The one physical open every journal-era maintenance purpose goes through.
    /// </summary>
    /// <remarks>
    /// One body behind narrow per-purpose methods rather than one method taking a purpose, because
    /// the acquisition inventory catalogues an open by the member that performs it: a single shared
    /// entry point would collapse seven distinguishable routes into one line a reviewer cannot tell
    /// apart, and the whole value of that inventory is that each acquisition is nameable.
    ///
    /// <para>Nothing here is chosen by the caller. The path and the mode come off the capability the
    /// gate issued for this exact purpose, and the initializer mode and the immutable form of the
    /// data source are decided from the purpose here - so a caller holding a read-only capability
    /// cannot ask for an exclusive maintenance connection by passing different arguments.</para>
    /// </remarks>
    [GrimoireConnectionAcquisitionRoute]
    private async Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        CovenantMaintenanceConnectionPurpose purpose,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(capability);

        ArgumentNullException.ThrowIfNull(lane);

        Result<IGrimoireTrackedMaintenanceHandle> consumed = capability.Consume(
            lane.Owner,
            lane.Generation,
            purpose,
            lane);

        if (consumed.IsFailure)
        {

            return Refused(
                "The journal maintenance capability does not authorize this Grimoire open.");

        }

        IGrimoireTrackedMaintenanceHandle handle = consumed.Value;

        try
        {

            _nativeRuntime.Initialize();

        }
        catch (Exception)
        {

            ReportNotOpened(handle);

            return Refused("The journal maintenance native runtime is unavailable.");

        }

        SqliteConnection? connection = null;

        try
        {

            SqliteConnectionStringBuilder builder = new()
            {
                DataSource = purpose is CovenantMaintenanceConnectionPurpose.ReopenVerification
                    ? $"file:{capability.CanonicalPath}?immutable=1"
                    : capability.CanonicalPath,

                Password = _passphraseSource.Passphrase,

                Pooling = false,

                Mode = capability.Mode is CovenantMaintenanceConnectionMode.ReadOnly
                    ? SqliteOpenMode.ReadOnly
                    : SqliteOpenMode.ReadWriteCreate,
            };

            connection = new SqliteConnection(builder.ToString());

        }
        catch (OperationCanceledException)
        {

            if (connection is not null)
            {

                await connection.DisposeAsync().ConfigureAwait(false);

            }

            ReportNotOpened(handle);

            throw;

        }
        catch (Exception)
        {

            if (connection is not null)
            {

                await connection.DisposeAsync().ConfigureAwait(false);

            }

            ReportNotOpened(handle);

            return Refused("The journal maintenance provider could not be constructed.");

        }

        Result started = handle.ReportOpenStarted();

        if (started.IsFailure)
        {

            await connection.DisposeAsync().ConfigureAwait(false);

            ReportNotOpened(handle);

            return Refused("The journal maintenance open could not start.");

        }

        try
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await _initializer.InitializeAsync(
                    connection,
                    InitializerModeOf(purpose),
                    cancellationToken)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            await CloseAndDisposeAsync(connection).ConfigureAwait(false);

            ReportPhysicallyClosed(handle);

            throw;

        }
        catch (Exception)
        {

            await CloseAndDisposeAsync(connection).ConfigureAwait(false);

            ReportPhysicallyClosed(handle);

            return Refused("The journal maintenance connection could not be opened and initialized.");

        }

        return Result<IGrimoireMaintenanceConnectionLease>.Success(new Lease(connection, handle));

    }

    /// <summary>
    /// How the shared initializer must configure the connection each purpose opens.
    /// </summary>
    /// <remarks>
    /// Separate from the capability's read-only or read-write mode because they answer different
    /// questions. The mode is what the provider is allowed to do with the file; this is what the
    /// engine has to be configured for, and an exclusive maintenance connection is a read-write one
    /// that additionally refuses to share the database with anybody.
    /// </remarks>
    private static CovenantSqliteConnectionMode InitializerModeOf(
        CovenantMaintenanceConnectionPurpose purpose) =>
        purpose switch
        {

            CovenantMaintenanceConnectionPurpose.IntegrityVerification
                or CovenantMaintenanceConnectionPurpose.ReopenVerification
                or CovenantMaintenanceConnectionPurpose.InventorySnapshot =>
                CovenantSqliteConnectionMode.ReadOnly,

            CovenantMaintenanceConnectionPurpose.SidecarProof =>
                CovenantSqliteConnectionMode.ReadWrite,

            _ => CovenantSqliteConnectionMode.ExclusiveMaintenance,

        };

    private static async ValueTask CloseAndDisposeAsync(SqliteConnection connection)
    {

        if (connection.State != ConnectionState.Closed)
        {

            await connection.CloseAsync().ConfigureAwait(false);

        }

        await connection.DisposeAsync().ConfigureAwait(false);

    }

    private static void ReportNotOpened(IGrimoireTrackedMaintenanceHandle handle)
    {

        Result reported = handle.ReportNotOpened();

        if (reported.IsFailure)
        {

            throw new InvalidOperationException(reported.Error.Message);

        }

    }

    private static void ReportPhysicallyClosed(IGrimoireTrackedMaintenanceHandle handle)
    {

        Result reported = handle.ReportPhysicallyClosed();

        if (reported.IsFailure)
        {

            throw new InvalidOperationException(reported.Error.Message);

        }

    }

    private static Error Refused(string message) =>
        new(ErrorCodes.Covenant.Unavailable, message);

    private sealed class Lease(
        SqliteConnection connection,
        IGrimoireTrackedMaintenanceHandle handle) : IGrimoireMaintenanceConnectionLease
    {

        private int _disposed;

        public SqliteConnection Connection { get; } = connection;

        public async ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {

                return;

            }

            await CloseAndDisposeAsync(Connection).ConfigureAwait(false);

            ReportPhysicallyClosed(handle);

            GC.SuppressFinalize(this);

        }

    }

}
