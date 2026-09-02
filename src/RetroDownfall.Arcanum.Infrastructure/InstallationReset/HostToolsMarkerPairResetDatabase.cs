using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed class HostToolsMarkerPairResetDatabase
    : IHostToolsMarkerPairResetDatabase
{

    private static TimeSpan ProductionCheckpointTimeout => TimeSpan.FromSeconds(5);

    private readonly IStoppedHostGrimoireConnectionFactory _connections;

    private readonly IHostToolsMarkerPairResetDatabaseTestSeam _testSeam;

    private readonly TimeSpan _checkpointTimeout;

    internal HostToolsMarkerPairResetDatabase(
        IStoppedHostGrimoireConnectionFactory connections)
        : this(
            connections,
            NoopHostToolsMarkerPairResetDatabaseTestSeam.Instance,
            ProductionCheckpointTimeout)
    {
    }

    internal HostToolsMarkerPairResetDatabase(
        IStoppedHostGrimoireConnectionFactory connections,
        ICovenantSqliteConnectionInitializer initializer)
        : this(connections)
    {

        ArgumentNullException.ThrowIfNull(initializer);

    }

    internal HostToolsMarkerPairResetDatabase(
        IStoppedHostGrimoireConnectionFactory connections,
        IHostToolsMarkerPairResetDatabaseTestSeam testSeam)
        : this(
            connections,
            testSeam,
            ProductionCheckpointTimeout)
    {
    }

    internal HostToolsMarkerPairResetDatabase(
        IStoppedHostGrimoireConnectionFactory connections,
        ICovenantSqliteConnectionInitializer initializer,
        IHostToolsMarkerPairResetDatabaseTestSeam testSeam)
        : this(connections, testSeam)
    {

        ArgumentNullException.ThrowIfNull(initializer);

    }

    internal HostToolsMarkerPairResetDatabase(
        IStoppedHostGrimoireConnectionFactory connections,
        IHostToolsMarkerPairResetDatabaseTestSeam testSeam,
        TimeSpan checkpointTimeout)
    {

        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

        _testSeam = testSeam ?? throw new ArgumentNullException(nameof(testSeam));

        _checkpointTimeout = checkpointTimeout > TimeSpan.Zero
            && checkpointTimeout <= ProductionCheckpointTimeout
            ? checkpointTimeout
            : throw new ArgumentOutOfRangeException(nameof(checkpointTimeout));

    }

    internal HostToolsMarkerPairResetDatabase(
        IStoppedHostGrimoireConnectionFactory connections,
        ICovenantSqliteConnectionInitializer initializer,
        IHostToolsMarkerPairResetDatabaseTestSeam testSeam,
        TimeSpan checkpointTimeout)
        : this(connections, testSeam, checkpointTimeout)
    {

        ArgumentNullException.ThrowIfNull(initializer);

    }

    [GrimoireConnectionAcquisitionRoute]
    public async Task<Result<HostToolsMarkerPairResetDatabaseSession>>
        OpenHostToolsMarkerPairResetDatabaseSessionAsync(
        IStoppedHostGrimoireConnectionAuthority authority,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(authority);

        cancellationToken.ThrowIfCancellationRequested();

        IStoppedHostGrimoireConnectionLease? lease = null;

        try
        {

            Result<IStoppedHostGrimoireConnectionLease> opened =
                await _connections.OpenStoppedHostMarkerPairResetAsync(
                    authority,
                    cancellationToken).ConfigureAwait(false);

            if (opened.IsFailure)
            {

                return Result<HostToolsMarkerPairResetDatabaseSession>.Failure(
                    opened.Error);

            }

            lease = opened.Value;

            return Result<HostToolsMarkerPairResetDatabaseSession>.Success(
                CreateSession(lease));

        }
        catch (OperationCanceledException)
        {

            if (lease is not null)
            {

                await lease.DisposeAsync().ConfigureAwait(false);

            }

            throw;

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            if (lease is not null)
            {

                await lease.DisposeAsync().ConfigureAwait(false);

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

    [GrimoireConnectionAcquisitionRoute]
    private HostToolsMarkerPairResetDatabaseSession CreateSession(
        IStoppedHostGrimoireConnectionLease lease)
    {

        SqliteConnection connection = lease.Connection;

        object creationTicket = new SessionCreationTicket(connection);

        return new HostToolsMarkerPairResetDatabaseSession(
            lease,
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
