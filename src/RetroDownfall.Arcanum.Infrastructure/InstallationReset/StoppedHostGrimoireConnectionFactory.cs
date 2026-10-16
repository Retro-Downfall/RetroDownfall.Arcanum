using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal interface IStoppedHostGrimoireConnectionFactoryTestSeam
{
    void AfterProviderConstruction();

    ValueTask BeforeNativeOpenAsync(CancellationToken cancellationToken);
}

internal sealed class NoopStoppedHostGrimoireConnectionFactoryTestSeam
    : IStoppedHostGrimoireConnectionFactoryTestSeam
{
    internal static NoopStoppedHostGrimoireConnectionFactoryTestSeam Instance { get; } = new();

    private NoopStoppedHostGrimoireConnectionFactoryTestSeam()
    {
    }

    public void AfterProviderConstruction()
    {
    }

    public ValueTask BeforeNativeOpenAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

internal sealed class StoppedHostGrimoireConnectionFactory
    : IStoppedHostGrimoireConnectionFactory
{
    private readonly ICovenantSqliteConnectionInitializer _initializer;

    private readonly ISqliteNativeRuntime _nativeRuntime;

    private readonly IGrimoireDbPassphraseSource _passphrase;

    private readonly IStoppedHostGrimoireConnectionFactoryTestSeam _testSeam;

    internal StoppedHostGrimoireConnectionFactory(
        IGrimoireDbPassphraseSource passphrase,
        ISqliteNativeRuntime nativeRuntime,
        ICovenantSqliteConnectionInitializer initializer)
        : this(
            passphrase,
            nativeRuntime,
            initializer,
            NoopStoppedHostGrimoireConnectionFactoryTestSeam.Instance)
    {
    }

    internal StoppedHostGrimoireConnectionFactory(
        IGrimoireDbPassphraseSource passphrase,
        ISqliteNativeRuntime nativeRuntime,
        ICovenantSqliteConnectionInitializer initializer,
        IStoppedHostGrimoireConnectionFactoryTestSeam testSeam)
    {
        _passphrase = passphrase ?? throw new ArgumentNullException(nameof(passphrase));

        _nativeRuntime = nativeRuntime ?? throw new ArgumentNullException(nameof(nativeRuntime));

        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));

        _testSeam = testSeam ?? throw new ArgumentNullException(nameof(testSeam));
    }

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetPlanReadAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken) =>
        OpenStoppedHostLeaseAsync(
            authority,
            StoppedHostGrimoireOperation.InstallationResetPlanRead,
            CovenantSqliteConnectionMode.ReadOnly,
            cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetWorkspaceResolutionAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken) =>
        OpenStoppedHostLeaseAsync(
            authority,
            StoppedHostGrimoireOperation.InstallationResetWorkspaceResolution,
            CovenantSqliteConnectionMode.ReadOnly,
            cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetIdentityReadAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken) =>
        OpenStoppedHostLeaseAsync(
            authority,
            StoppedHostGrimoireOperation.InstallationResetIdentityRead,
            CovenantSqliteConnectionMode.ReadOnly,
            cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken) =>
        OpenStoppedHostLeaseAsync(
            authority,
            StoppedHostGrimoireOperation.InstallationResetHostToolsEvidenceRead,
            CovenantSqliteConnectionMode.ReadOnly,
            cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetApplyAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken) =>
        OpenStoppedHostLeaseAsync(
            authority,
            StoppedHostGrimoireOperation.InstallationResetApply,
            CovenantSqliteConnectionMode.ReadWrite,
            cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostMarkerPairResetAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken) =>
        OpenStoppedHostLeaseAsync(
            authority,
            StoppedHostGrimoireOperation.MarkerPairReset,
            CovenantSqliteConnectionMode.ReadWrite,
            cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    private async Task<Result<IStoppedHostGrimoireConnectionLease>> OpenStoppedHostLeaseAsync(
        IStoppedHostGrimoireConnectionAuthority authority,
        StoppedHostGrimoireOperation operation,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);

        string canonicalDatabasePath = Path.GetFullPath(
            ArcanumPaths.GrimoireDatabaseFile);

        Result consumed = StoppedHostGrimoireAuthorityIssuer.ConsumeAuthority(
            authority,
            operation,
            mode,
            canonicalDatabasePath);

        if (consumed.IsFailure)
        {
            return Result<IStoppedHostGrimoireConnectionLease>.Failure(
                consumed.Error);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _nativeRuntime.Initialize();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable();
        }

        Result beforeConstruction = StoppedHostGrimoireAuthorityIssuer
            .RevalidateAuthority(authority, canonicalDatabasePath);

        if (beforeConstruction.IsFailure)
        {
            return Result<IStoppedHostGrimoireConnectionLease>.Failure(
                beforeConstruction.Error);
        }

        SqliteConnection? connection = null;

        // What was beside the database before this lease opened it. A read-only connection to a
        // write-ahead-logged database cannot checkpoint or clean up when it closes, so whatever
        // sidecars it creates would outlive it; recording what already existed is what lets the lease
        // remove exactly what it made and nothing a previous process left.
        bool writeAheadLogExisted = File.Exists(canonicalDatabasePath + WriteAheadLogSuffix);

        bool sharedMemoryExisted = File.Exists(canonicalDatabasePath + SharedMemorySuffix);

        try
        {
            connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = canonicalDatabasePath,

                    Password = _passphrase.Passphrase,

                    Pooling = false,

                    Mode = mode is CovenantSqliteConnectionMode.ReadOnly
                        ? SqliteOpenMode.ReadOnly
                        : SqliteOpenMode.ReadWrite,

                    Cache = SqliteCacheMode.Private,
                }.ToString());

            _testSeam.AfterProviderConstruction();

            Result beforeOpen = StoppedHostGrimoireAuthorityIssuer
                .RevalidateAuthority(authority, canonicalDatabasePath);

            if (beforeOpen.IsFailure)
            {
                await connection.DisposeAsync().ConfigureAwait(false);

                return Result<IStoppedHostGrimoireConnectionLease>.Failure(
                    beforeOpen.Error);
            }

            await _testSeam.BeforeNativeOpenAsync(cancellationToken)
                .ConfigureAwait(false);

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await _initializer.InitializeAsync(
                connection,
                mode,
                cancellationToken).ConfigureAwait(false);

            // Counted from the moment it exists, so a lease that overlaps another one never removes
            // files the other may still be using: only the lease that closes with none left open takes
            // away what was made.
            _ = Interlocked.Increment(ref s_openLeases);

            return Result<IStoppedHostGrimoireConnectionLease>.Success(
                new StoppedHostGrimoireConnectionLease(
                    connection,
                    mode is CovenantSqliteConnectionMode.ReadOnly
                        ? new CreatedSidecars(
                            canonicalDatabasePath,
                            writeAheadLogExisted,
                            sharedMemoryExisted)
                        : null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            return Unavailable();
        }
    }

    private static int s_openLeases;

    private const string WriteAheadLogSuffix = "-wal";

    private const string SharedMemorySuffix = "-shm";

    private static Result<IStoppedHostGrimoireConnectionLease> Unavailable() =>
        Result<IStoppedHostGrimoireConnectionLease>.Failure(new Error(
            ErrorCodes.Covenant.MaintenanceFailed,
            "The stopped-host Grimoire connection is unavailable."));

    /// <summary>
    /// The write-ahead-log sidecars a read-only lease created, and the means to take them away again.
    /// </summary>
    /// <remarks>
    /// Only what the lease itself made. A shared-memory index it created is always removable, because
    /// the next open rebuilds it from the log. A log it created is removable only while it is empty,
    /// which a read-only connection leaves it, since a log with frames in it holds committed work the
    /// database file does not yet and is never this lease's to delete. Removal is best effort and
    /// never fails the operation: the host is stopped and the installation lock is held, so nothing
    /// else has the files open, and a file that cannot be deleted is merely left where it was made.
    /// </remarks>
    private sealed class CreatedSidecars(
        string databasePath,
        bool writeAheadLogExisted,
        bool sharedMemoryExisted)
    {
        internal void Remove()
        {
            try
            {
                string log = databasePath + WriteAheadLogSuffix;

                if (!writeAheadLogExisted && File.Exists(log) && new FileInfo(log).Length == 0)
                {
                    File.Delete(log);
                }

                string index = databasePath + SharedMemorySuffix;

                if (!sharedMemoryExisted && File.Exists(index))
                {
                    File.Delete(index);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left where it was made. See the remarks.
            }
        }
    }

    private sealed class StoppedHostGrimoireConnectionLease(
        SqliteConnection connection,
        CreatedSidecars? createdSidecars) : IStoppedHostGrimoireConnectionLease
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        private bool _disposed;

        public SqliteConnection Connection
        {
            get
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed),
                    this);

                return connection;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                if (_disposed)
                {
                    return;
                }

                if (connection.State is not ConnectionState.Closed)
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }

                await connection.DisposeAsync().ConfigureAwait(false);

                if (Interlocked.Decrement(ref s_openLeases) == 0)
                {
                    createdSidecars?.Remove();
                }

                _disposed = true;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
