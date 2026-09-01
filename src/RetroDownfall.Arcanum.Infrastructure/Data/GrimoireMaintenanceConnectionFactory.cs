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

    [GrimoireConnectionAcquisitionRoute]
    public async Task<Result<IGrimoireMaintenanceConnectionLease>>
        OpenJournalCanonicalErasureAsync(
            IGrimoireMaintenanceConnectionCapability capability,
            IGrimoireMaintenanceIoLane lane,
            CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(capability);

        ArgumentNullException.ThrowIfNull(lane);

        Result<IGrimoireTrackedMaintenanceHandle> consumed = capability.Consume(
            lane.Owner,
            lane.Generation,
            ArcanumPaths.GrimoireDatabaseFile,
            CovenantMaintenanceConnectionMode.ReadWrite,
            CovenantMaintenanceConnectionPurpose.CanonicalErasure,
            lane);

        if (consumed.IsFailure)
        {

            return Refused(
                "The journal maintenance capability does not authorize this canonical Grimoire open.");

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
                DataSource = ArcanumPaths.GrimoireDatabaseFile,

                Password = _passphraseSource.Passphrase,

                Pooling = false,

                Mode = SqliteOpenMode.ReadWriteCreate,
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
                    CovenantSqliteConnectionMode.ExclusiveMaintenance,
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

        return Result<IGrimoireMaintenanceConnectionLease>.Success(
            new Lease(connection, handle));

    }

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
