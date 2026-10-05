using System.Data.Common;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The interceptor is the one place an EF-opened Grimoire connection is admitted, so it, and not whatever
/// started the host first, has to select the native SQLCipher provider before anything is registered or opened.
/// </summary>
[Collection(RetroDownfall.Arcanum.Tests.Collections.SqliteConnectionPoolCollection.Name)]
public sealed class CovenantConnectionEnrolmentInterceptorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_runtime_is_initialized_before_the_ordinary_open_begins_and_before_the_connection_opens(
        bool useAsyncOpen)
    {
        List<string> events = [];

        CovenantConnectionDrain drain = new();

        RecordingLifecycle lifecycle = new(
            new GrimoireOrdinaryConnectionLifecycle(
                new GrimoireConnectionAdmissionGate(TimeProvider.System, drain),
                drain),
            events);

        CovenantConnectionEnrolmentInterceptor interceptor = new(
            lifecycle,
            drain,
            new RecordingInitializer(events),
            new RecordingRuntime(events));

        await using SqliteConnection connection = new("Data Source=:memory:");

        connection.StateChange += (_, change) =>
        {
            if (change.CurrentState == System.Data.ConnectionState.Open)
            {
                events.Add("physically-open");
            }
        };

        DbContextOptions<ProbeDbContext> options = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseSqlite(connection, contextOwnsConnection: false)
            .AddInterceptors(interceptor)
            .Options;

        await using ProbeDbContext context = new(options);

        if (useAsyncOpen)
        {
            await context.Database.OpenConnectionAsync();

            await context.Database.CloseConnectionAsync();
        }
        else
        {
            context.Database.OpenConnection();

            context.Database.CloseConnection();
        }

        int initialize = events.IndexOf("native-runtime-initialize");

        int beginOpen = events.IndexOf("begin-open");

        int physicallyOpen = events.IndexOf("physically-open");

        int initializeConnection = events.IndexOf("initialize-connection");

        Assert.True(initialize >= 0, "The native runtime was never initialized: " + string.Join(", ", events));

        Assert.True(beginOpen > initialize, "begin-open preceded the native runtime: " + string.Join(", ", events));

        Assert.True(physicallyOpen > beginOpen, "The open preceded its ticket: " + string.Join(", ", events));

        Assert.True(
            initializeConnection > physicallyOpen,
            "Connection initialization preceded the open: " + string.Join(", ", events));
    }

    [Fact]
    public async Task A_native_runtime_that_cannot_initialize_refuses_the_open_before_any_registration_exists()
    {
        List<string> events = [];

        CovenantConnectionDrain drain = new();

        RecordingLifecycle lifecycle = new(
            new GrimoireOrdinaryConnectionLifecycle(
                new GrimoireConnectionAdmissionGate(TimeProvider.System, drain),
                drain),
            events);

        CovenantConnectionEnrolmentInterceptor interceptor = new(
            lifecycle,
            drain,
            new RecordingInitializer(events),
            new RecordingRuntime(events)
            {
                Failure = new SqliteNativeRuntimeUnavailableException(
                    "test-rid",
                    "libsqlcipher-test",
                    new DllNotFoundException("not found")),
            });

        await using SqliteConnection connection = new("Data Source=:memory:");

        DbContextOptions<ProbeDbContext> options = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseSqlite(connection, contextOwnsConnection: false)
            .AddInterceptors(interceptor)
            .Options;

        await using ProbeDbContext context = new(options);

        _ = await Assert.ThrowsAsync<SqliteNativeRuntimeUnavailableException>(
            () => context.Database.OpenConnectionAsync());

        Assert.Equal(["native-runtime-initialize"], events);

        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options);

    private sealed class RecordingRuntime(List<string> events) : ISqliteNativeRuntime
    {
        internal Exception? Failure { get; init; }

        public void Initialize()
        {
            events.Add("native-runtime-initialize");

            if (Failure is not null)
            {
                throw Failure;
            }

            // The provider has to be the real one for the connection to open at all.
            SqliteNativeRuntime.Instance.Initialize();
        }
    }

    private sealed class RecordingLifecycle(
        IGrimoireOrdinaryConnectionLifecycle inner,
        List<string> events) : IGrimoireOrdinaryConnectionLifecycle
    {
        public IGrimoireOrdinaryConnectionRegistration BeginOpen(DbConnection connection)
        {
            events.Add("begin-open");

            return inner.BeginOpen(connection);
        }

        public Result<IGrimoireOrdinaryConnectionRegistration> BorrowCurrentOpen(DbConnection connection) =>
            inner.BorrowCurrentOpen(connection);

        public void ReleaseAfterExternalClose(DbConnection connection) =>
            inner.ReleaseAfterExternalClose(connection);
    }

    private sealed class RecordingInitializer(List<string> events) : ICovenantSqliteConnectionInitializer
    {
        public ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {
            events.Add("initialize-connection");

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
}
