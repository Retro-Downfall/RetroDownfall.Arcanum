using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class GrimoireOrdinaryConnectionFactory : IGrimoireOrdinaryConnectionFactory
{

    private readonly IGrimoireOrdinaryConnectionLifecycle _lifecycle;

    private readonly ICovenantConnectionDrain _drain;

    private readonly IGrimoireDbPassphraseSource _passphraseSource;

    private readonly ICovenantSqliteConnectionInitializer _initializer;

    private readonly ISqliteNativeRuntime _nativeRuntime;

    private readonly IGrimoireOrdinaryConnectionFactoryTestSeam _testSeam;

    public GrimoireOrdinaryConnectionFactory(
        IGrimoireOrdinaryConnectionLifecycle lifecycle,
        ICovenantConnectionDrain drain,
        IGrimoireDbPassphraseSource passphraseSource,
        ICovenantSqliteConnectionInitializer initializer,
        ISqliteNativeRuntime nativeRuntime,
        IGrimoireOrdinaryConnectionFactoryTestSeam testSeam)
    {

        ArgumentNullException.ThrowIfNull(lifecycle);

        ArgumentNullException.ThrowIfNull(drain);

        ArgumentNullException.ThrowIfNull(passphraseSource);

        ArgumentNullException.ThrowIfNull(initializer);

        ArgumentNullException.ThrowIfNull(nativeRuntime);

        ArgumentNullException.ThrowIfNull(testSeam);

        _lifecycle = lifecycle;

        _drain = drain;

        _passphraseSource = passphraseSource;

        _initializer = initializer;

        _nativeRuntime = nativeRuntime;

        _testSeam = testSeam;

    }

    [GrimoireConnectionAcquisitionRoute]
    public async Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        if (mode is not CovenantSqliteConnectionMode.ReadOnly
            and not CovenantSqliteConnectionMode.ReadWrite)
        {

            return Refused("The ordinary Grimoire connection mode is invalid.");

        }

        Result canonicalTarget = ValidateCanonicalTarget(connection);

        if (canonicalTarget.IsFailure)
        {

            return Result<IGrimoireOrdinaryConnectionLease>.Failure(canonicalTarget.Error);

        }

        Result<IGrimoireOrdinaryConnectionRegistration> borrowed =
            _lifecycle.BorrowCurrentOpen(connection);

        if (borrowed.IsSuccess)
        {

            return Result<IGrimoireOrdinaryConnectionLease>.Success(
                new Lease(connection, borrowed.Value, ownsPhysicalOpen: false, disposeConnection: false));

        }

        if (connection.State != ConnectionState.Closed)
        {

            return Refused(
                "The scoped Grimoire connection has no current admitted-open provenance.");

        }

        try
        {

            _nativeRuntime.Initialize();

        }
        catch (Exception failed) when (failed is not OperationCanceledException)
        {

            return Refused("The ordinary Grimoire native runtime is unavailable.");

        }

        IGrimoireOrdinaryConnectionRegistration registration;

        try
        {

            registration = _lifecycle.BeginOpen(connection);

        }
        catch (Exception failed) when (failed is GrimoireMaintenanceUnavailableException
            or InvalidOperationException)
        {

            return Refused("Ordinary Grimoire connection admission is unavailable.");

        }

        try
        {

            await _testSeam.BeforeNativeOpenAsync(cancellationToken).ConfigureAwait(false);

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            await TerminalizeOpenFailureAsync(connection, registration).ConfigureAwait(false);

            throw;

        }
        catch (Exception)
        {

            await TerminalizeOpenFailureAsync(connection, registration).ConfigureAwait(false);

            return Refused("The ordinary Grimoire connection could not be opened.");

        }

        Result admitted = await CompleteNativeOpenAsync(
            connection,
            registration,
            mode,
            cancellationToken).ConfigureAwait(false);

        if (admitted.IsFailure)
        {

            return Result<IGrimoireOrdinaryConnectionLease>.Failure(admitted.Error);

        }

        return Result<IGrimoireOrdinaryConnectionLease>.Success(
            new Lease(connection, registration, ownsPhysicalOpen: true, disposeConnection: false));

    }

    [GrimoireConnectionAcquisitionRoute]
    public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
        GrimoireOrdinaryFreshConnectionKind kind,
        CancellationToken cancellationToken)
    {

        CovenantSqliteConnectionMode mode;

        SqliteConnectionStringBuilder builder;

        switch (kind)
        {
            case GrimoireOrdinaryFreshConnectionKind.ReadOnly:
                mode = CovenantSqliteConnectionMode.ReadOnly;

                builder = new()
                {
                    DataSource = ArcanumPaths.GrimoireDatabaseFile,

                    Password = _passphraseSource.Passphrase,

                    Pooling = false,

                    Mode = SqliteOpenMode.ReadOnly,

                    Cache = SqliteCacheMode.Private,
                };

                break;

            case GrimoireOrdinaryFreshConnectionKind.ReadWrite:
            case GrimoireOrdinaryFreshConnectionKind.IsolatedHeartbeat:
                mode = CovenantSqliteConnectionMode.ReadWrite;

                builder = new()
                {
                    DataSource = ArcanumPaths.GrimoireDatabaseFile,

                    Password = _passphraseSource.Passphrase,

                    Pooling = false,

                    Mode = SqliteOpenMode.ReadWriteCreate,
                };

                break;

            default:
                return Refused("The ordinary Grimoire fresh-connection kind is invalid.");
        }

        try
        {

            _nativeRuntime.Initialize();

        }
        catch (Exception failed) when (failed is not OperationCanceledException)
        {

            return Refused("The ordinary Grimoire native runtime is unavailable.");

        }

        _testSeam.BeforeProviderConstruction();

        SqliteConnection connection = new SqliteConnection(builder.ToString());

        IGrimoireOrdinaryConnectionRegistration registration;

        try
        {

            registration = _lifecycle.BeginOpen(connection);

        }
        catch (Exception failed) when (failed is GrimoireMaintenanceUnavailableException
            or InvalidOperationException)
        {

            await connection.DisposeAsync().ConfigureAwait(false);

            return Refused("Ordinary Grimoire connection admission is unavailable.");

        }

        try
        {

            await _testSeam.BeforeNativeOpenAsync(cancellationToken).ConfigureAwait(false);

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            await TerminalizeOpenFailureAsync(connection, registration).ConfigureAwait(false);

            await connection.DisposeAsync().ConfigureAwait(false);

            throw;

        }
        catch (Exception)
        {

            await TerminalizeOpenFailureAsync(connection, registration).ConfigureAwait(false);

            await connection.DisposeAsync().ConfigureAwait(false);

            return Refused("The ordinary Grimoire connection could not be opened.");

        }

        Result admitted;

        try
        {

            admitted = await CompleteNativeOpenAsync(
                connection,
                registration,
                mode,
                cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            await connection.DisposeAsync().ConfigureAwait(false);

            throw;

        }

        if (admitted.IsFailure)
        {

            await connection.DisposeAsync().ConfigureAwait(false);

            return Result<IGrimoireOrdinaryConnectionLease>.Failure(admitted.Error);

        }

        return Result<IGrimoireOrdinaryConnectionLease>.Success(
            new Lease(connection, registration, ownsPhysicalOpen: true, disposeConnection: true));

    }

    private async Task<Result> CompleteNativeOpenAsync(
        SqliteConnection connection,
        IGrimoireOrdinaryConnectionRegistration registration,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken)
    {

        Result revalidated = registration.RevalidateAfterNativeOpen();

        if (revalidated.IsFailure)
        {

            await CloseClearAndRefuseAsync(connection, registration).ConfigureAwait(false);

            registration.Dispose();

            return Result.Failure(revalidated.Error);

        }

        try
        {

            await _initializer.InitializeAsync(connection, mode, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            await CloseClearAndRefuseAsync(connection, registration).ConfigureAwait(false);

            registration.Dispose();

            throw;

        }
        catch (Exception)
        {

            await CloseClearAndRefuseAsync(connection, registration).ConfigureAwait(false);

            registration.Dispose();

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The ordinary Grimoire connection could not be initialized.");

        }

        Result opened = registration.MarkOpened();

        if (opened.IsFailure)
        {

            await CloseClearAndRefuseAsync(connection, registration).ConfigureAwait(false);

            registration.Dispose();

            return Result.Failure(opened.Error);

        }

        return Result.Success();

    }

    private async Task TerminalizeOpenFailureAsync(
        SqliteConnection connection,
        IGrimoireOrdinaryConnectionRegistration registration)
    {

        if (connection.State == ConnectionState.Open)
        {

            _ = registration.RevalidateAfterNativeOpen();

            await CloseClearAndRefuseAsync(connection, registration).ConfigureAwait(false);

        }
        else
        {

            registration.MarkFailed();

        }

        registration.Dispose();

    }

    private async Task CloseClearAndRefuseAsync(
        SqliteConnection connection,
        IGrimoireOrdinaryConnectionRegistration registration)
    {

        await connection.CloseAsync().ConfigureAwait(false);

        Result cleared = _drain.ClearExactPoolAfterClose(connection);

        if (cleared.IsFailure)
        {

            throw new InvalidOperationException(cleared.Error.Message);

        }

        if (connection.State != ConnectionState.Closed)
        {

            throw new InvalidOperationException(
                "A refused ordinary Grimoire open remained physically open.");

        }

        _testSeam.AfterExactPoolClear(connection);

        registration.MarkRefusedAfterOpen();

    }

    private static Result ValidateCanonicalTarget(SqliteConnection connection)
    {

        string dataSource;

        try
        {

            dataSource = new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource;

        }
        catch (Exception failed) when (failed is ArgumentException or FormatException)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The scoped Grimoire connection string is invalid.");

        }

        if (string.IsNullOrWhiteSpace(dataSource))
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The scoped Grimoire connection target is missing.");

        }

        string normalizedTarget;

        string normalizedCanonical;

        try
        {

            normalizedTarget = Path.GetFullPath(dataSource);

            normalizedCanonical = Path.GetFullPath(ArcanumPaths.GrimoireDatabaseFile);

        }
        catch (Exception failed) when (failed is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The scoped Grimoire connection target is invalid.");

        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(normalizedTarget, normalizedCanonical, comparison)
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.Unavailable,
                "The scoped connection does not target the canonical live Grimoire.");

    }

    private static Result<IGrimoireOrdinaryConnectionLease> Refused(string message) =>
        Result<IGrimoireOrdinaryConnectionLease>.Failure(
            new Error(ErrorCodes.Covenant.Unavailable, message));

    private sealed class Lease(
        SqliteConnection connection,
        IGrimoireOrdinaryConnectionRegistration registration,
        bool ownsPhysicalOpen,
        bool disposeConnection) : IGrimoireOrdinaryConnectionLease
    {

        private int _disposed;

        public SqliteConnection Connection { get; } = connection;

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {

                return;

            }

            if (ownsPhysicalOpen)
            {

                if (Connection.State != ConnectionState.Closed)
                {

                    Connection.Close();

                }

                if (disposeConnection)
                {

                    Connection.Dispose();

                }

            }

            registration.Dispose();

        }

        public async ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {

                return;

            }

            if (ownsPhysicalOpen)
            {

                if (Connection.State != ConnectionState.Closed)
                {

                    await Connection.CloseAsync().ConfigureAwait(false);

                }

                if (disposeConnection)
                {

                    await Connection.DisposeAsync().ConfigureAwait(false);

                }

            }

            registration.Dispose();

        }

    }

}

internal sealed class NoOpGrimoireOrdinaryConnectionFactoryTestSeam
    : IGrimoireOrdinaryConnectionFactoryTestSeam
{

    public void BeforeProviderConstruction()
    {
    }

    public ValueTask BeforeNativeOpenAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public void AfterExactPoolClear(SqliteConnection connection)
    {
    }

}
