using System.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection(RetroDownfall.Arcanum.Tests.Collections.SqliteConnectionPoolCollection.Name)]
public sealed class GrimoireConnectionAdmissionInterceptorTests
{

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_admission_refuses_before_the_provider_open(bool asynchronous)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(gate, 1);

        RecordingConnectionDrain drain = new();

        await using TrackingSqliteConnection connection = new(ConnectionString);

        DiagnosticCapture capture = new();

        using ILoggerFactory logging = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddFilter(typeof(CovenantConnectionEnrolmentInterceptor).FullName, LogLevel.Debug)
            .AddProvider(capture));

        await using ProbeDbContext context = CreateContext(connection, gate, drain, loggerFactory: logging);

        if (asynchronous)
        {

            _ = await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(
                () => context.Database.OpenConnectionAsync());

        }
        else
        {

            _ = Assert.Throws<GrimoireMaintenanceUnavailableException>(context.Database.OpenConnection);

        }

        Assert.Equal(0, connection.ProviderOpenCount);

        Assert.Equal(0, drain.RegisterCount);

        Assert.Equal(0, drain.DisposeCount);

        AssertSanitizedDiagnostic(capture, maintenance: true);

    }

    [Fact]
    public async Task Ef_open_attempt_while_stage_two_drain_is_blocked_is_refused_before_native_open()
    {

        BlockingStageTwoDrain drain = new();

        await using ServiceProvider provider = CreateProvider(drain);

        GrimoireConnectionAdmissionGate gate = provider
            .GetRequiredService<GrimoireConnectionAdmissionGate>();

        await using IGrimoireClosingOwner closing = Begin(gate, 23);

        Result stageOne = await gate.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None);

        Assert.True(stageOne.IsSuccess, stageOne.IsFailure ? stageOne.Error.Message : null);

        Task<Result<IGrimoireExclusiveClosedLease>> closingAdmission = gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
            .AsTask();

        Task first = await Task.WhenAny(drain.Entered, closingAdmission);

        Assert.Same(drain.Entered, first);

        await using TrackingSqliteConnection connection = new(ConnectionString);

        await using ProbeDbContext context = CreateContext(connection, gate, drain);

        _ = await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(
            () => context.Database.OpenConnectionAsync());

        Assert.Equal(0, connection.ProviderOpenCount);

        drain.Release();

        Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_that_loses_admission_closes_and_clears_its_exact_pool_before_refusal(
        bool asynchronous)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        List<string> releaseOrder = [];

        Task<Result<IGrimoireExclusiveClosedLease>>? closingAdmission = null;

        GatedOpenSqliteConnection connection = new(ConnectionString, releaseOrder);

        RecordingConnectionDrain drain = new(cleared =>
        {

            Assert.Same(connection, cleared);

            Assert.Equal(ConnectionState.Closed, cleared.State);

            Assert.NotNull(closingAdmission);

            Assert.False(closingAdmission!.IsCompleted);

            releaseOrder.Add("clear");

        });

        RecordingConnectionInitializer initializer = new();

        DiagnosticCapture capture = new();

        using ILoggerFactory logging = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddFilter(typeof(CovenantConnectionEnrolmentInterceptor).FullName, LogLevel.Debug)
            .AddProvider(capture));

        await using (connection.ConfigureAwait(false))
        {

            await using ProbeDbContext context = CreateContext(
                connection,
                gate,
                drain,
                initializer,
                logging);

            Task opening = asynchronous
                ? context.Database.OpenConnectionAsync()
                : Task.Run(context.Database.OpenConnection);

            await connection.OpenEntered;

            await using IGrimoireClosingOwner closing = Begin(gate, 2);

            Result requestsDrained = await gate.DrainRequestAndWorkAsync(
                closing,
                CancellationToken.None);

            Assert.True(
                requestsDrained.IsSuccess,
                requestsDrained.IsFailure ? requestsDrained.Error.Message : null);

            closingAdmission = gate
                .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
                .AsTask();

            Assert.False(closingAdmission.IsCompleted);

            connection.AllowOpen();

            _ = await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(() => opening);

            Assert.Equal(ConnectionState.Closed, connection.State);

            Assert.Equal(1, connection.PhysicalCloseCount);

            Assert.Equal(0, initializer.CallCount);

            Assert.Equal(0, drain.RegisterCount);

            Assert.Equal(0, drain.DisposeCount);

            Assert.Equal(1, drain.ExactPoolClearCount);

            Assert.Equal(["close", "clear"], releaseOrder);

            AssertSanitizedDiagnostic(capture, maintenance: true);

            Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

            Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

            await using IGrimoireExclusiveClosedLease lease = closed.Value;

        }

    }

    [Fact]
    public async Task Controlled_refusal_close_keeps_its_exact_lifecycle_registration_until_terminal()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        await using GatedOpenSqliteConnection connection = new(ConnectionString);

        await using ProbeDbContext context = CreateContext(connection, gate, drain);

        Task opening = context.Database.OpenConnectionAsync();

        await connection.OpenEntered;

        await using IGrimoireClosingOwner closing = Begin(gate, 24);

        Result requestsDrained = await gate.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None);

        Assert.True(
            requestsDrained.IsSuccess,
            requestsDrained.IsFailure ? requestsDrained.Error.Message : null);

        Task<Result<IGrimoireExclusiveClosedLease>> closingAdmission = gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
            .AsTask();

        connection.AllowOpen();

        Exception refused = await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(
            () => opening);

        Assert.IsType<GrimoireMaintenanceUnavailableException>(refused);

        Assert.Equal(ConnectionState.Closed, connection.State);

        Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

    }

    [Fact]
    public async Task Closure_during_post_open_initializer_closes_before_ticket_completion()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        BlockingConnectionInitializer initializer = new();

        await using TrackingSqliteConnection connection = new(ConnectionString);

        await using ProbeDbContext context = CreateContext(
            connection,
            gate,
            drain,
            initializer);

        Task opening = context.Database.OpenConnectionAsync();

        await initializer.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        await using IGrimoireClosingOwner closing = Begin(gate, 20);

        Result requestsDrained = await gate.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None);

        Assert.True(
            requestsDrained.IsSuccess,
            requestsDrained.IsFailure ? requestsDrained.Error.Message : null);

        Task<Result<IGrimoireExclusiveClosedLease>> closingAdmission = gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
            .AsTask();

        Assert.False(closingAdmission.IsCompleted);

        initializer.AllowCompletion();

        _ = await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(() => opening);

        Assert.Equal(1, initializer.CallCount);

        Assert.Equal(ConnectionState.Closed, connection.State);

        Assert.Equal(1, connection.PhysicalCloseCount);

        Assert.Equal(1, drain.RegisterCount);

        Assert.Equal(1, drain.DisposeCount);

        Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Initializer_termination_closes_and_clears_exact_pool_before_ticket_completion(
        bool asynchronous,
        bool canceled)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        List<string> releaseOrder = [];

        Task<Result<IGrimoireExclusiveClosedLease>>? closingAdmission = null;

        TrackingSqliteConnection connection = new(ConnectionString, releaseOrder);

        RecordingConnectionDrain drain = new(cleared =>
        {

            Assert.Same(connection, cleared);

            Assert.Equal(ConnectionState.Closed, cleared.State);

            Assert.NotNull(closingAdmission);

            Assert.False(closingAdmission!.IsCompleted);

            releaseOrder.Add("clear");

        });

        TerminatingConnectionInitializer initializer = new();

        ProbeDbContext context = CreateContext(connection, gate, drain, initializer);

        try
        {

            Task opening = asynchronous
                ? context.Database.OpenConnectionAsync()
                : Task.Run(context.Database.OpenConnection);

            await initializer.Entered.WaitAsync(TimeSpan.FromSeconds(30));

            await using IGrimoireClosingOwner closing = Begin(gate, 21);

            Result requestsDrained = await gate.DrainRequestAndWorkAsync(
                closing,
                CancellationToken.None);

            Assert.True(
                requestsDrained.IsSuccess,
                requestsDrained.IsFailure ? requestsDrained.Error.Message : null);

            closingAdmission = gate
                .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
                .AsTask();

            Assert.False(closingAdmission.IsCompleted);

            initializer.Terminate(canceled);

            if (canceled)
            {

                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);

            }
            else
            {

                _ = await Assert.ThrowsAsync<InvalidOperationException>(() => opening);

            }

            Assert.Equal(ConnectionState.Closed, connection.State);

            Assert.Equal(1, connection.PhysicalCloseCount);

            Assert.Equal(1, drain.ExactPoolClearCount);

            Assert.Equal(["close", "clear"], releaseOrder);

            Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

            Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

            await using IGrimoireExclusiveClosedLease lease = closed.Value;

        }
        finally
        {

            await context.DisposeAsync();

            await connection.DisposeAsync();

        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pooled_generation_loss_refusal_releases_the_native_resource_end_to_end(
        bool asynchronous)
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.Connection.CloseAsync();

        string connectionString = PooledConnectionString(database.Connection.ConnectionString);

        await using PooledGatedOpenSqliteConnection connection = new(connectionString);

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        CovenantConnectionDrain drain = new();

        await using ProbeDbContext context = CreateContext(connection, gate, drain);

        try
        {

            Task opening = asynchronous
                ? context.Database.OpenConnectionAsync()
                : Task.Run(context.Database.OpenConnection);

            await connection.OpenEntered;

            await using IGrimoireClosingOwner closing = Begin(gate, 22);

            Result requestsDrained = await gate.DrainRequestAndWorkAsync(
                closing,
                CancellationToken.None);

            Assert.True(
                requestsDrained.IsSuccess,
                requestsDrained.IsFailure ? requestsDrained.Error.Message : null);

            Task<Result<IGrimoireExclusiveClosedLease>> closingAdmission = gate
                .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
                .AsTask();

            Assert.False(closingAdmission.IsCompleted);

            connection.AllowNativeOpen();

            await connection.NativeOpenCompleted;

            Assert.True(new SqliteConnectionStringBuilder(connectionString).Pooling);

            Assert.Contains(
                CovenantResidualArtifactClass.WriteAheadLog,
                CovenantResidualArtifacts.Survivors(database.DatabasePath));

            connection.AllowProviderReturn();

            _ = await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(() => opening);

            Assert.Equal(ConnectionState.Closed, connection.State);

            Assert.Equal(1, connection.PhysicalCloseCount);

            Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

            Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

            await using IGrimoireExclusiveClosedLease lease = closed.Value;

            Assert.Empty(CovenantResidualArtifacts.Survivors(database.DatabasePath));

        }
        finally
        {

            connection.AllowNativeOpen();

            connection.AllowProviderReturn();

            SqliteConnection.ClearPool(connection);

        }

    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Pooled_initializer_termination_releases_the_native_resource_end_to_end(
        bool asynchronous,
        bool canceled)
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.Connection.CloseAsync();

        string connectionString = PooledConnectionString(database.Connection.ConnectionString);

        TrackingSqliteConnection connection = new(connectionString);

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        CovenantConnectionDrain drain = new();

        TerminatingConnectionInitializer initializer = new();

        ProbeDbContext context = CreateContext(connection, gate, drain, initializer);

        try
        {

            Task opening = asynchronous
                ? context.Database.OpenConnectionAsync()
                : Task.Run(context.Database.OpenConnection);

            await initializer.Entered.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(new SqliteConnectionStringBuilder(connectionString).Pooling);

            Assert.Contains(
                CovenantResidualArtifactClass.WriteAheadLog,
                CovenantResidualArtifacts.Survivors(database.DatabasePath));

            initializer.Terminate(canceled);

            if (canceled)
            {

                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);

            }
            else
            {

                _ = await Assert.ThrowsAsync<InvalidOperationException>(() => opening);

            }

            Assert.Equal(ConnectionState.Closed, connection.State);

            Assert.Equal(1, connection.PhysicalCloseCount);

            Assert.Empty(CovenantResidualArtifacts.Survivors(database.DatabasePath));

        }
        finally
        {

            initializer.Terminate(canceled);

            SqliteConnection.ClearPool(connection);

            await context.DisposeAsync();

            await connection.DisposeAsync();

        }

    }

    [Fact]
    public async Task Drain_enrolment_begins_only_after_successful_post_open_revalidation()
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        await using GatedOpenSqliteConnection connection = new(ConnectionString);

        await using ProbeDbContext context = CreateContext(connection, gate, drain);

        Task opening = context.Database.OpenConnectionAsync();

        await connection.OpenEntered;

        Assert.Equal(0, drain.RegisterCount);

        Assert.Equal(0, drain.ActiveCount);

        connection.AllowOpen();

        await opening;

        Assert.Equal(1, drain.RegisterCount);

        Assert.Equal(1, drain.ActiveCount);

        await context.Database.CloseConnectionAsync();

        Assert.Equal(0, drain.ActiveCount);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_failure_callbacks_release_the_open_ticket_exactly_once(bool asynchronous)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        await using FailingOpenSqliteConnection connection = new(ConnectionString);

        ProbeDbContext context = CreateContext(connection, gate, drain);

        try
        {

            if (asynchronous)
            {

                _ = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => context.Database.OpenConnectionAsync());

            }
            else
            {

                _ = Assert.Throws<InvalidOperationException>(
                    context.Database.OpenConnection);

            }

            await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(gate, 3);

            Assert.Equal(ConnectionState.Closed, connection.State);

            Assert.Equal(0, drain.RegisterCount);

            Assert.Equal(0, drain.DisposeCount);

            Assert.Equal(0, drain.ExactPoolClearCount);

        }
        finally
        {

            await context.DisposeAsync();

        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_open_initializer_failure_closes_and_releases_before_context_disposal(
        bool asynchronous)
    {

        await AssertPostOpenTerminationCleansBeforeDisposalAsync(
            asynchronous,
            ownerSeed: 5);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_cancellation_after_native_open_closes_and_releases_before_context_disposal(
        bool asynchronous)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        await using CanceledAfterOpenSqliteConnection connection = new(ConnectionString);

        ProbeDbContext context = CreateContext(connection, gate, drain);

        try
        {

            if (asynchronous)
            {

                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => context.Database.OpenConnectionAsync());

            }
            else
            {

                _ = Assert.ThrowsAny<OperationCanceledException>(
                    context.Database.OpenConnection);

            }

            Assert.Equal(ConnectionState.Closed, connection.State);

            Assert.True(connection.NativeOpenCompleted);

            await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(gate, 6);

            Assert.Equal(0, drain.RegisterCount);

            Assert.Equal(0, drain.DisposeCount);

            Assert.Equal(1, drain.ExactPoolClearCount);

        }
        finally
        {

            await context.DisposeAsync();

        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_cancellation_releases_the_ticket_before_context_disposal(
        bool asynchronous)
    {

        GrimoireConnectionAdmissionGate gate = new(
            TimeProvider.System,
            TimeSpan.FromMilliseconds(100));

        RecordingConnectionDrain drain = new();

        await using CanceledOpenSqliteConnection connection = new(ConnectionString);

        ProbeDbContext context = CreateContext(connection, gate, drain);

        try
        {

            if (asynchronous)
            {

                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => context.Database.OpenConnectionAsync());

            }
            else
            {

                _ = Assert.ThrowsAny<OperationCanceledException>(
                    context.Database.OpenConnection);

            }

            Assert.Equal(ConnectionState.Closed, connection.State);

            await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(gate, 7);

            Assert.Equal(0, drain.RegisterCount);

            Assert.Equal(0, drain.DisposeCount);

            Assert.Equal(0, drain.ExactPoolClearCount);

        }
        finally
        {

            await context.DisposeAsync();

        }

    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Close_and_dispose_callbacks_release_enrolment_exactly_once(
        bool asynchronous,
        bool disposeWhileOpen)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        TrackingSqliteConnection connection = new(ConnectionString);

        ProbeDbContext context = CreateContext(connection, gate, drain);

        try
        {

            if (asynchronous)
            {

                await context.Database.OpenConnectionAsync();

            }
            else
            {

                context.Database.OpenConnection();

            }

            Assert.Equal(1, drain.ActiveCount);

            if (disposeWhileOpen)
            {

                if (asynchronous)
                {

                    await context.DisposeAsync();

                }
                else
                {

                    context.Dispose();

                }

            }
            else if (asynchronous)
            {

                await context.Database.CloseConnectionAsync();

                await context.DisposeAsync();

            }
            else
            {

                context.Database.CloseConnection();

                context.Dispose();

            }

            Assert.Equal(1, drain.RegisterCount);

            Assert.Equal(1, drain.DisposeCount);

            Assert.Equal(0, drain.ActiveCount);

            await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(gate, 4);

        }
        finally
        {

            await context.DisposeAsync();

            await connection.DisposeAsync();

        }

    }

    private const string ConnectionString = "Data Source=:memory:;Pooling=False";

    /// <summary>
    /// The synchronous open arm runs the real initializer, and the policy it applies survives.
    /// </summary>
    /// <remarks>
    /// Every other test here injects a fake initializer, so the synchronous
    /// <c>ConnectionOpened</c> arm - where the branch put SQLCipher and pragma policy after deleting
    /// <c>SqlitePragmaConnectionInterceptor</c> from the serving options - never ran the real
    /// five-statement sequence. That arm also carries a sync-over-async
    /// <c>GetAwaiter().GetResult()</c>, and it has no production caller today, so nothing would have
    /// caught a regression in it. This opens synchronously through the real initializer and reads
    /// the policy back off the connection it applied it to.
    /// </remarks>
    [SkippableFact]
    public async Task Synchronous_open_applies_the_real_connection_policy()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.Connection.CloseAsync();

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        SqliteConnection connection = new(database.Connection.ConnectionString);

        await using ProbeDbContext context = CreateContext(
            connection,
            gate,
            drain,
            CovenantSqliteConnectionInitializer.Instance);

        context.Database.OpenConnection();

        try
        {

            Assert.Equal(ConnectionState.Open, connection.State);

            Assert.Equal("wal", ReadScalar(connection, "PRAGMA journal_mode;"));

            Assert.Equal("1", ReadScalar(connection, "PRAGMA foreign_keys;"));

        }
        finally
        {

            context.Database.CloseConnection();

            SqliteConnection.ClearPool(connection);

        }

    }

    private static string ReadScalar(SqliteConnection connection, string sql)
    {

        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return command.ExecuteScalar()?.ToString() ?? string.Empty;

    }

    private static string PooledConnectionString(string connectionString) =>
        new SqliteConnectionStringBuilder(connectionString)
        {

            Pooling = true,

        }.ToString();

    private static ServiceProvider CreateProvider(ICovenantConnectionDrain drain)
    {

        ServiceCollection services = new();

        services.AddArcanumGrimoireForCli();

        services.AddSingleton<ICovenantConnectionDrain>(drain);

        return services.BuildServiceProvider();

    }

    private static ProbeDbContext CreateContext(
        SqliteConnection connection,
        IGrimoireConnectionAdmissionGate gate,
        ICovenantConnectionDrain drain,
        ICovenantSqliteConnectionInitializer? initializer = null,
        ILoggerFactory? loggerFactory = null)
    {

        DbContextOptionsBuilder<ProbeDbContext> builder = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseSqlite(connection, contextOwnsConnection: true);

        ArcanumDbContextOptionsConfigurator.ConfigureServingEnrolment(
            builder,
            new GrimoireOrdinaryConnectionLifecycle(gate, drain),
            drain,
            initializer ?? NoOpConnectionInitializer.Instance);

        if (loggerFactory is not null)
        {

            builder.UseLoggerFactory(loggerFactory);

        }

        return new ProbeDbContext(builder.Options);

    }

    private static async Task AssertPostOpenTerminationCleansBeforeDisposalAsync(
        bool asynchronous,
        byte ownerSeed)
    {

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        RecordingConnectionDrain drain = new();

        TrackingSqliteConnection connection = new(ConnectionString);

        DiagnosticCapture capture = new();

        using ILoggerFactory logging = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddFilter(typeof(CovenantConnectionEnrolmentInterceptor).FullName, LogLevel.Debug)
            .AddProvider(capture));

        ProbeDbContext context = CreateContext(
            connection,
            gate,
            drain,
            new ThrowingConnectionInitializer(),
            logging);

        try
        {

            if (asynchronous)
            {

                _ = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => context.Database.OpenConnectionAsync());

            }
            else
            {

                _ = Assert.Throws<InvalidOperationException>(
                    context.Database.OpenConnection);

            }

            Assert.Equal(ConnectionState.Closed, connection.State);

            Assert.Equal(1, connection.PhysicalCloseCount);

            Assert.Equal(0, drain.RegisterCount);

            Assert.Equal(0, drain.DisposeCount);

            Assert.Equal(0, drain.ActiveCount);

            AssertSanitizedDiagnostic(capture, maintenance: false);

            await using IGrimoireExclusiveClosedLease closed = await CloseAdmissionAsync(
                gate,
                ownerSeed);

        }
        finally
        {

            await context.DisposeAsync();

            await connection.DisposeAsync();

        }

    }

    private static void AssertSanitizedDiagnostic(DiagnosticCapture capture, bool maintenance)
    {

        var entry = Assert.Single(capture.Entries);

        Assert.Equal(typeof(CovenantConnectionEnrolmentInterceptor).FullName, entry.Category);

        Assert.Equal(maintenance ? LogLevel.Debug : LogLevel.Error, entry.Level);

        Assert.Equal(maintenance
            ? "An ordinary Grimoire connection was deferred for maintenance."
            : "An ordinary Grimoire connection failed to open safely.", entry.Message);

        Assert.Null(entry.Exception);

    }

    private sealed class DiagnosticCapture : ILoggerProvider
    {

        internal System.Collections.Concurrent.ConcurrentQueue<(string Category, LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, this);

        public void Dispose() { }

        private sealed class CaptureLogger(string category, DiagnosticCapture capture) : ILogger
        {

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel level) => true;

            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                capture.Entries.Enqueue((category, level, formatter(state, exception), exception));

        }

    }

    private static async Task<IGrimoireExclusiveClosedLease> CloseAdmissionAsync(
        GrimoireConnectionAdmissionGate gate,
        byte ownerSeed)
    {

        await using IGrimoireClosingOwner closing = Begin(gate, ownerSeed);

        Result requestsDrained = await gate.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None);

        Assert.True(
            requestsDrained.IsSuccess,
            requestsDrained.IsFailure ? requestsDrained.Error.Message : null);

        Result<IGrimoireExclusiveClosedLease> closed = await gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        return closed.Value;

    }

    private static IGrimoireClosingOwner Begin(
        GrimoireConnectionAdmissionGate gate,
        byte ownerSeed)
    {

        CovenantExclusiveRecoveryOwner owner = new(
            Guid.Parse($"00000000-0000-0000-0000-{ownerSeed:D12}"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat(ownerSeed, 32).ToArray()));

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(owner);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        return begun.Value;

    }

    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options)
        : DbContext(options)
    {
    }

    private sealed class TrackingSqliteConnection : SqliteConnection
    {

        internal TrackingSqliteConnection(
            string connectionString,
            List<string>? releaseOrder = null)
            : base(connectionString)
        {

            StateChange += (_, args) =>
            {

                if (args.OriginalState != ConnectionState.Closed
                    && args.CurrentState == ConnectionState.Closed)
                {

                    PhysicalCloseCount++;

                    releaseOrder?.Add("close");

                }

            };

        }

        internal int ProviderOpenCount { get; private set; }

        internal int PhysicalCloseCount { get; private set; }

        public override void Open()
        {

            ProviderOpenCount++;

            base.Open();

        }

        public override Task OpenAsync(CancellationToken cancellationToken)
        {

            ProviderOpenCount++;

            return base.OpenAsync(cancellationToken);

        }

    }

    private sealed class GatedOpenSqliteConnection : SqliteConnection
    {

        private readonly TaskCompletionSource _openEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowOpen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal GatedOpenSqliteConnection(
            string connectionString,
            List<string>? releaseOrder = null)
            : base(connectionString)
        {

            StateChange += (_, args) =>
            {

                if (args.OriginalState != ConnectionState.Closed
                    && args.CurrentState == ConnectionState.Closed)
                {

                    PhysicalCloseCount++;

                    releaseOrder?.Add("close");

                }

            };

        }

        internal Task OpenEntered => _openEntered.Task;

        internal int PhysicalCloseCount { get; private set; }

        internal void AllowOpen() =>
            _allowOpen.TrySetResult();

        public override void Open()
        {

            _openEntered.TrySetResult();

            _allowOpen.Task.GetAwaiter().GetResult();

            base.Open();

        }

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {

            _openEntered.TrySetResult();

            await _allowOpen.Task.WaitAsync(cancellationToken);

            await base.OpenAsync(cancellationToken);

        }

    }

    private sealed class FailingOpenSqliteConnection(string connectionString)
        : SqliteConnection(connectionString)
    {

        public override void Open() =>
            throw new InvalidOperationException("provider open failed");

        public override Task OpenAsync(CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("provider open failed"));

    }

    private sealed class PooledGatedOpenSqliteConnection : SqliteConnection
    {

        private readonly TaskCompletionSource _openEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowNativeOpen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _nativeOpenCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowProviderReturn =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PooledGatedOpenSqliteConnection(string connectionString)
            : base(connectionString)
        {

            StateChange += (_, args) =>
            {

                if (args.OriginalState != ConnectionState.Closed
                    && args.CurrentState == ConnectionState.Closed)
                {

                    PhysicalCloseCount++;

                }

            };

        }

        internal Task OpenEntered => _openEntered.Task;

        internal Task NativeOpenCompleted => _nativeOpenCompleted.Task;

        internal int PhysicalCloseCount { get; private set; }

        internal void AllowNativeOpen() => _allowNativeOpen.TrySetResult();

        internal void AllowProviderReturn() => _allowProviderReturn.TrySetResult();

        public override void Open()
        {

            _openEntered.TrySetResult();

            _allowNativeOpen.Task.GetAwaiter().GetResult();

            base.Open();

            _nativeOpenCompleted.TrySetResult();

            _allowProviderReturn.Task.GetAwaiter().GetResult();

        }

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {

            _openEntered.TrySetResult();

            await _allowNativeOpen.Task.WaitAsync(cancellationToken);

            await base.OpenAsync(cancellationToken);

            _nativeOpenCompleted.TrySetResult();

            await _allowProviderReturn.Task.WaitAsync(cancellationToken);

        }

    }

    private sealed class CanceledOpenSqliteConnection(string connectionString)
        : SqliteConnection(connectionString)
    {

        private readonly CancellationTokenSource _canceled = CreateCanceledSource();

        public override void Open() =>
            throw new OperationCanceledException(_canceled.Token);

        public override Task OpenAsync(CancellationToken cancellationToken) =>
            Task.FromCanceled(_canceled.Token);

        private static CancellationTokenSource CreateCanceledSource()
        {

            CancellationTokenSource source = new();

            source.Cancel();

            return source;

        }

    }

    private sealed class CanceledAfterOpenSqliteConnection(string connectionString)
        : SqliteConnection(connectionString)
    {

        internal bool NativeOpenCompleted { get; private set; }

        public override void Open()
        {

            base.Open();

            NativeOpenCompleted = true;

            throw new OperationCanceledException("provider canceled after native open");

        }

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {

            await base.OpenAsync(cancellationToken);

            NativeOpenCompleted = true;

            throw new OperationCanceledException("provider canceled after native open");

        }

    }

    private sealed class NoOpConnectionInitializer : ICovenantSqliteConnectionInitializer
    {

        internal static NoOpConnectionInitializer Instance { get; } = new();

        public ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingConnectionInitializer : ICovenantSqliteConnectionInitializer
    {

        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            _ = Interlocked.Increment(ref _callCount);

            return ValueTask.CompletedTask;

        }

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class BlockingConnectionInitializer : ICovenantSqliteConnectionInitializer
    {

        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal Task Entered => _entered.Task;

        internal void AllowCompletion() => _allowCompletion.TrySetResult();

        public async ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            _ = Interlocked.Increment(ref _callCount);

            _entered.TrySetResult();

            await _allowCompletion.Task.WaitAsync(cancellationToken);

        }

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class ThrowingConnectionInitializer : ICovenantSqliteConnectionInitializer
    {

        public ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(
                new InvalidOperationException("post-open initializer failed"));

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class TerminatingConnectionInitializer : ICovenantSqliteConnectionInitializer
    {

        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _terminated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Terminate(bool canceled)
        {

            Exception failure = canceled
                ? new OperationCanceledException("post-open initializer canceled")
                : new InvalidOperationException("post-open initializer failed");

            _terminated.TrySetException(failure);

        }

        public async ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            _entered.TrySetResult();

            await _terminated.Task.WaitAsync(TimeSpan.FromSeconds(30));

        }

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingConnectionDrain(
        Action<SqliteConnection>? exactPoolClear = null) : ICovenantConnectionDrain
    {

        private int _activeCount;

        private int _disposeCount;

        private int _registerCount;

        private int _exactPoolClearCount;

        internal int ActiveCount => Volatile.Read(ref _activeCount);

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal int RegisterCount => Volatile.Read(ref _registerCount);

        internal int ExactPoolClearCount => Volatile.Read(ref _exactPoolClearCount);

        public IDisposable Register(SqliteConnection connection)
        {

            ArgumentNullException.ThrowIfNull(connection);

            _ = Interlocked.Increment(ref _registerCount);

            _ = Interlocked.Increment(ref _activeCount);

            return new Registration(this);

        }

        public Task<Result> DrainAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Result ClearExactPoolAfterClose(SqliteConnection connection)
        {

            _ = Interlocked.Increment(ref _exactPoolClearCount);

            exactPoolClear?.Invoke(connection);

            return Result.Success();

        }

        private sealed class Registration(RecordingConnectionDrain owner) : IDisposable
        {

            private int _disposed;

            public void Dispose()
            {

                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {

                    return;

                }

                _ = Interlocked.Increment(ref owner._disposeCount);

                _ = Interlocked.Decrement(ref owner._activeCount);

            }

        }

    }

    private sealed class BlockingStageTwoDrain : ICovenantConnectionDrain
    {

        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _released.TrySetResult();

        public IDisposable Register(SqliteConnection connection) => new NoOpRegistration();

        public Result ClearExactPoolAfterClose(SqliteConnection connection) => Result.Success();

        public async Task<Result> DrainAsync(CancellationToken cancellationToken)
        {

            _entered.TrySetResult();

            await _released.Task.WaitAsync(cancellationToken);

            return Result.Success();

        }

        private sealed class NoOpRegistration : IDisposable
        {

            public void Dispose()
            {
            }

        }

    }

}
