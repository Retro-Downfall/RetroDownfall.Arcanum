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

            return Result<IStoppedHostGrimoireConnectionLease>.Success(
                new StoppedHostGrimoireConnectionLease(connection));

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

    private static Result<IStoppedHostGrimoireConnectionLease> Unavailable() =>
        Result<IStoppedHostGrimoireConnectionLease>.Failure(new Error(
            ErrorCodes.Covenant.MaintenanceFailed,
            "The stopped-host Grimoire connection is unavailable."));

    private sealed class StoppedHostGrimoireConnectionLease(
        SqliteConnection connection) : IStoppedHostGrimoireConnectionLease
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

                _disposed = true;

            }
            finally
            {

                _gate.Release();

            }

        }

    }

}
