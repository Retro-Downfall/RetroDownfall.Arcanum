using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed class HostToolsMarkerPairResetDatabase
    : IHostToolsMarkerPairResetDatabase
{

    private static TimeSpan ProductionCheckpointTimeout => TimeSpan.FromSeconds(5);

    private readonly ICovenantMaintenanceConnectionFactory _connections;

    private readonly ICovenantSqliteConnectionInitializer _initializer;

    private readonly IHostToolsMarkerPairResetDatabaseTestSeam _testSeam;

    private readonly TimeSpan _checkpointTimeout;

    internal HostToolsMarkerPairResetDatabase(
        ICovenantMaintenanceConnectionFactory connections,
        ICovenantSqliteConnectionInitializer initializer)
        : this(
            connections,
            initializer,
            NoopHostToolsMarkerPairResetDatabaseTestSeam.Instance,
            ProductionCheckpointTimeout)
    {
    }

    internal HostToolsMarkerPairResetDatabase(
        ICovenantMaintenanceConnectionFactory connections,
        ICovenantSqliteConnectionInitializer initializer,
        IHostToolsMarkerPairResetDatabaseTestSeam testSeam)
        : this(
            connections,
            initializer,
            testSeam,
            ProductionCheckpointTimeout)
    {
    }

    internal HostToolsMarkerPairResetDatabase(
        ICovenantMaintenanceConnectionFactory connections,
        ICovenantSqliteConnectionInitializer initializer,
        IHostToolsMarkerPairResetDatabaseTestSeam testSeam,
        TimeSpan checkpointTimeout)
    {

        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));

        _testSeam = testSeam ?? throw new ArgumentNullException(nameof(testSeam));

        _checkpointTimeout = checkpointTimeout > TimeSpan.Zero
            && checkpointTimeout <= ProductionCheckpointTimeout
            ? checkpointTimeout
            : throw new ArgumentOutOfRangeException(nameof(checkpointTimeout));

    }

    public async Task<Result<HostToolsMarkerPairResetDatabaseSession>> OpenAsync(
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        SqliteConnection? connection = null;

        try
        {

            connection = await _connections.OpenAsync(cancellationToken)
                .ConfigureAwait(false);

            await _initializer.InitializeAsync(
                connection,
                CovenantSqliteConnectionMode.ReadWrite,
                cancellationToken).ConfigureAwait(false);

            return Result<HostToolsMarkerPairResetDatabaseSession>.Success(
                CreateSession(connection));

        }
        catch (OperationCanceledException)
        {

            if (connection is not null)
            {

                await connection.DisposeAsync().ConfigureAwait(false);

            }

            throw;

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            if (connection is not null)
            {

                await connection.DisposeAsync().ConfigureAwait(false);

            }

            return Result<HostToolsMarkerPairResetDatabaseSession>.Failure(new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The host-tools database marker evidence is unavailable."));

        }

    }

    internal bool TryConsumeSessionCreationTicket(
        SqliteConnection connection,
        object creationTicket)
    {

        return creationTicket is SessionCreationTicket ticket
            && ReferenceEquals(ticket.Connection, connection)
            && Interlocked.CompareExchange(ref ticket.Consumed, 1, 0) == 0;

    }

    private HostToolsMarkerPairResetDatabaseSession CreateSession(
        SqliteConnection connection)
    {

        object creationTicket = new SessionCreationTicket(connection);

        return new HostToolsMarkerPairResetDatabaseSession(
            connection,
            this,
            _testSeam,
            _checkpointTimeout,
            creationTicket);

    }

    private sealed class SessionCreationTicket(SqliteConnection connection)
    {

        internal SqliteConnection Connection { get; } = connection;

        internal int Consumed;

    }

}
