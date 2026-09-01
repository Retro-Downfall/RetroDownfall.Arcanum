using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("ProcessEnvironment")]
public sealed class GrimoireOrdinaryConnectionFactoryTests : IDisposable
{

    private readonly string? _originalDotnetEnvironment;

    private readonly string? _originalAspNetCoreEnvironment;

    private readonly string? _originalTestHome;

    private readonly string _testHome = Path.Combine(
        Path.GetTempPath(),
        "arcanum-ordinary-factory-tests",
        Guid.NewGuid().ToString("N"));

    public GrimoireOrdinaryConnectionFactoryTests()
    {

        _originalDotnetEnvironment = global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        _originalAspNetCoreEnvironment = global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        _originalTestHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _testHome);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

    }

    [Fact]
    public async Task Invalid_fresh_kind_is_refused_before_provider_construction()
    {

        RecordingLifecycle lifecycle = new();

        RecordingRuntime runtime = new(initializeProvider: false);

        RecordingTestSeam seam = new();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(lifecycle, runtime, seam: seam);

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.OpenFreshAsync(
            (GrimoireOrdinaryFreshConnectionKind)byte.MaxValue,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, runtime.InitializeCount);

        Assert.Equal(0, seam.BeforeProviderConstructionCount);

        Assert.Equal(0, seam.BeforeNativeOpenCount);

        Assert.Equal(0, lifecycle.BeginOpenCount);

    }

    [Fact]
    public async Task Fresh_native_runtime_precedes_provider_construction_ticket_and_open()
    {

        List<string> events = [];

        RecordingLifecycle lifecycle = new(events);

        RecordingTestSeam seam = new(events);

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: true, events),
            seam: seam,
            initializer: new RecordingInitializer(events));

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind.ReadWrite,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        AssertOrdered(events, "runtime", "provider-construction", "ticket", "native-open");

        await result.Value.DisposeAsync();

    }

    [Fact]
    public async Task Fresh_native_runtime_failure_constructs_and_opens_no_provider()
    {

        RecordingLifecycle lifecycle = new();

        RecordingRuntime runtime = new(initializeProvider: false)
        {
            Failure = new InvalidOperationException("native runtime unavailable"),
        };

        RecordingTestSeam seam = new();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(lifecycle, runtime, seam: seam);

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind.ReadWrite,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, runtime.InitializeCount);

        Assert.Equal(0, seam.BeforeProviderConstructionCount);

        Assert.Equal(0, seam.BeforeNativeOpenCount);

        Assert.Equal(0, lifecycle.BeginOpenCount);

    }

    [Fact]
    public async Task Closed_scoped_runtime_failure_follows_validation_and_precedes_ticket_and_native_open()
    {

        RecordingLifecycle lifecycle = new();

        RecordingRuntime runtime = new(initializeProvider: false)
        {
            Failure = new InvalidOperationException("native runtime unavailable"),
        };

        RecordingTestSeam seam = new();

        await using SqliteConnection connection = CanonicalScopedConnection();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(lifecycle, runtime, seam: seam);

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, runtime.InitializeCount);

        Assert.Equal(0, lifecycle.BeginOpenCount);

        Assert.Equal(0, seam.BeforeNativeOpenCount);

        Assert.Equal(ConnectionState.Closed, connection.State);

    }

    public static TheoryData<string> NonCanonicalTargets() =>
        new()
        {
            "",
            "not-a-valid-connection-string",
            Path.Combine(Path.GetTempPath(), "foreign-grimoire.db"),
            ArcanumPaths.GrimoireDatabaseFile + ".archive",
            ArcanumPaths.GrimoireDatabaseFile + "-wal",
            Path.Combine(ArcanumPaths.GrimoireDirectory, "staging", "arcanum.db"),
        };

    [Theory]
    [MemberData(nameof(NonCanonicalTargets))]
    public async Task Closed_scoped_noncanonical_target_is_refused_before_ticket_or_native_open(
        string target)
    {

        RecordingLifecycle lifecycle = new();

        RecordingRuntime runtime = new(initializeProvider: false);

        RecordingTestSeam seam = new();

        string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = target,

                Mode = SqliteOpenMode.Memory,

                Pooling = false,
            }.ToString();

        await using SqliteConnection connection = target == "not-a-valid-connection-string"
            ? new MalformedConnection()
            : new SqliteConnection(connectionString);

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(lifecycle, runtime, seam: seam);

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, runtime.InitializeCount);

        Assert.Equal(0, lifecycle.BorrowCount);

        Assert.Equal(0, lifecycle.BeginOpenCount);

        Assert.Equal(0, seam.BeforeNativeOpenCount);

    }

    [Fact]
    public async Task Closed_scoped_ticket_is_acquired_before_native_open()
    {

        List<string> events = [];

        RecordingLifecycle lifecycle = new(events);

        RecordingTestSeam seam = new(events);

        await using SqliteConnection connection = CanonicalScopedConnection();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: true, events),
            seam: seam,
            initializer: new RecordingInitializer(events));

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.True(events.IndexOf("ticket") < events.IndexOf("native-open"));

        await result.Value.DisposeAsync();

    }

    [Fact]
    public async Task Already_admitted_current_generation_open_is_borrowed_without_second_open()
    {

        await using SqliteConnection connection = CanonicalScopedConnection();

        connection.Open();

        RecordingRegistration borrowed = new(connection);

        RecordingLifecycle lifecycle = new()
        {
            BorrowResult = Result<IGrimoireOrdinaryConnectionRegistration>.Success(borrowed),
        };

        RecordingTestSeam seam = new();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: false),
            seam: seam);

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(1, lifecycle.BorrowCount);

        Assert.Equal(0, lifecycle.BeginOpenCount);

        Assert.Equal(0, seam.BeforeNativeOpenCount);

        await result.Value.DisposeAsync();

        Assert.Equal(ConnectionState.Open, connection.State);

        Assert.Equal(1, borrowed.DisposeCount);

    }

    [Fact]
    public async Task Synchronously_disposed_borrowed_lease_leaves_connection_open_and_releases_registration_once()
    {

        await using SqliteConnection connection = CanonicalScopedConnection();

        connection.Open();

        RecordingRegistration borrowed = new(connection);

        RecordingLifecycle lifecycle = new()
        {
            BorrowResult = Result<IGrimoireOrdinaryConnectionRegistration>.Success(borrowed),
        };

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: false));

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        result.Value.Dispose();

        Assert.Equal(ConnectionState.Open, connection.State);

        Assert.Equal(1, borrowed.DisposeCount);

    }

    [Fact]
    public async Task Already_open_unproven_or_stale_scoped_connection_is_refused()
    {

        await using SqliteConnection connection = CanonicalScopedConnection();

        connection.Open();

        RecordingLifecycle lifecycle = new();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: false));

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);

        Assert.Equal(1, lifecycle.BorrowCount);

        Assert.Equal(0, lifecycle.BeginOpenCount);

        Assert.Equal(ConnectionState.Open, connection.State);

    }

    [Fact]
    public async Task Generation_advance_while_native_open_is_blocked_loses_revalidation_and_drains()
    {

        RecordingDrain drain = new();

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System, drain);

        GrimoireOrdinaryConnectionLifecycle lifecycle = new(gate, drain);

        RecordingTestSeam seam = new()
        {
            BlockNativeOpen = true,
        };

        await using SqliteConnection connection = CanonicalScopedConnection();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: true),
            drain,
            seam);

        Task<Result<IGrimoireOrdinaryConnectionLease>> acquiring = factory.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        await seam.NativeOpenEntered;

        await using IGrimoireClosingOwner closing = BeginClosing(gate);

        Result requestDrain = await gate.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None);

        Assert.True(requestDrain.IsSuccess, requestDrain.IsFailure ? requestDrain.Error.Message : null);

        Task<Result<IGrimoireExclusiveClosedLease>> closingAdmission = gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
            .AsTask();

        Assert.False(closingAdmission.IsCompleted);

        seam.AllowNativeOpen();

        Result<IGrimoireOrdinaryConnectionLease> result = await acquiring;

        Assert.True(result.IsFailure);

        Assert.Equal(ConnectionState.Closed, connection.State);

        Assert.Equal(1, drain.ClearCount);

        Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

    }

    [Fact]
    public async Task Raw_open_attempt_while_stage_two_drain_is_blocked_is_refused_before_native_open()
    {

        BlockingStageTwoDrain drain = new();

        await using ServiceProvider provider = CreateProvider(drain);

        GrimoireConnectionAdmissionGate gate = provider
            .GetRequiredService<GrimoireConnectionAdmissionGate>();

        await using IGrimoireClosingOwner closing = BeginClosing(gate);

        Result stageOne = await gate.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None);

        Assert.True(stageOne.IsSuccess, stageOne.IsFailure ? stageOne.Error.Message : null);

        Task<Result<IGrimoireExclusiveClosedLease>> closingAdmission = gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
            .AsTask();

        Task first = await Task.WhenAny(drain.Entered, closingAdmission);

        Assert.Same(drain.Entered, first);

        RecordingTestSeam seam = new();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            new GrimoireOrdinaryConnectionLifecycle(gate, drain),
            new RecordingRuntime(initializeProvider: true),
            drain,
            seam);

        await using SqliteConnection connection = CanonicalScopedConnection();

        Result<IGrimoireOrdinaryConnectionLease> acquisition = await factory.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.True(acquisition.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, acquisition.Error.Code);

        Assert.Equal(0, seam.BeforeNativeOpenCount);

        Assert.Equal(ConnectionState.Closed, connection.State);

        drain.Release();

        Result<IGrimoireExclusiveClosedLease> closed = await closingAdmission;

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

    }

    [Fact]
    public async Task Generation_race_closes_clears_observes_and_then_terminally_refuses()
    {

        List<string> events = [];

        RecordingRegistration registration = new(
            connection: null,
            events)
        {
            RevalidateResult = Result.Failure(
                new Error(ErrorCodes.Covenant.Unavailable, "generation changed")),
        };

        RecordingLifecycle lifecycle = new(events)
        {
            RegistrationFactory = connection => registration.ConnectionOverride(connection),
        };

        RecordingDrain drain = new(events);

        RecordingTestSeam seam = new(events);

        await using SqliteConnection connection = CanonicalScopedConnection();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: true, events),
            drain,
            seam);

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ConnectionState.Closed, connection.State);

        Assert.Same(connection, drain.ClearedConnection);

        Assert.Equal(1, drain.ClearCount);

        Assert.Equal(ConnectionState.Closed, drain.StateAtClear);

        Assert.Same(connection, seam.ClearedConnection);

        AssertOrdered(events, "revalidate", "clear", "after-clear", "refused", "release");

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initializer_failure_or_cancellation_closes_clears_observes_then_terminalizes(
        bool cancel)
    {

        List<string> events = [];

        RecordingLifecycle lifecycle = new(events);

        RecordingDrain drain = new(events);

        RecordingTestSeam seam = new(events);

        RecordingInitializer initializer = new(events)
        {
            Failure = cancel
                ? new OperationCanceledException("initializer cancelled")
                : new InvalidOperationException("initializer failed"),
        };

        await using SqliteConnection connection = CanonicalScopedConnection();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: true, events),
            drain,
            seam,
            initializer);

        if (cancel)
        {

            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => factory.AcquireScopedAsync(
                connection,
                CovenantSqliteConnectionMode.ReadWrite,
                CancellationToken.None));

        }
        else
        {

            Result<IGrimoireOrdinaryConnectionLease> result = await factory.AcquireScopedAsync(
                connection,
                CovenantSqliteConnectionMode.ReadWrite,
                CancellationToken.None);

            Assert.True(result.IsFailure);

        }

        Assert.Equal(ConnectionState.Closed, connection.State);

        Assert.Equal(1, drain.ClearCount);

        Assert.Equal(ConnectionState.Closed, drain.StateAtClear);

        AssertOrdered(events, "initializer", "clear", "after-clear", "refused", "release");

    }

    [Fact]
    public async Task Pre_native_open_failure_marks_ticket_failed_without_exact_pool_clear()
    {

        List<string> events = [];

        RecordingLifecycle lifecycle = new(events);

        RecordingDrain drain = new(events);

        RecordingTestSeam seam = new(events)
        {
            NativeOpenFailure = new InvalidOperationException("pre-open seam failure"),
        };

        await using SqliteConnection connection = CanonicalScopedConnection();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: true, events),
            drain,
            seam);

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Null(drain.ClearedConnection);

        AssertOrdered(events, "ticket", "native-open", "failed", "release");

    }

    [Fact]
    public async Task Successful_open_enrolls_before_ticket_terminal_and_can_be_borrowed()
    {

        RecordingDrain drain = new();

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System, drain);

        GrimoireOrdinaryConnectionLifecycle lifecycle = new(gate, drain);

        await using SqliteConnection connection = CanonicalScopedConnection();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: true),
            drain);

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(1, drain.RegisterCount);

        Result<IGrimoireOrdinaryConnectionRegistration> borrowed = lifecycle.BorrowCurrentOpen(connection);

        Assert.True(borrowed.IsSuccess, borrowed.IsFailure ? borrowed.Error.Message : null);

        borrowed.Value.Dispose();

        await result.Value.DisposeAsync();

    }

    [Theory]
    [InlineData((byte)GrimoireOrdinaryFreshConnectionKind.ReadOnly)]
    [InlineData((byte)GrimoireOrdinaryFreshConnectionKind.ReadWrite)]
    [InlineData((byte)GrimoireOrdinaryFreshConnectionKind.IsolatedHeartbeat)]
    public async Task Fresh_connection_string_is_canonical_keyed_and_unpooled(
        byte kindValue)
    {

        GrimoireOrdinaryFreshConnectionKind kind =
            (GrimoireOrdinaryFreshConnectionKind)kindValue;

        await CreateCanonicalDatabaseAsync();

        SqliteConnectionStringBuilder? observed = null;

        RecordingInitializer initializer = new()
        {
            OnInitialize = connection => observed = new(connection.ConnectionString),
        };

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            new RecordingLifecycle(),
            new RecordingRuntime(initializeProvider: true),
            initializer: initializer);

        Result<IGrimoireOrdinaryConnectionLease> result = await factory.OpenFreshAsync(
            kind,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.NotNull(observed);

        Assert.Equal(Path.GetFullPath(ArcanumPaths.GrimoireDatabaseFile), Path.GetFullPath(observed.DataSource));

        Assert.Equal("ordinary-factory-passphrase", observed.Password);

        Assert.False(observed.Pooling);

        if (kind == GrimoireOrdinaryFreshConnectionKind.ReadOnly)
        {

            Assert.Equal(SqliteOpenMode.ReadOnly, observed.Mode);

            Assert.Equal(SqliteCacheMode.Private, observed.Cache);

        }
        else
        {

            Assert.NotEqual(SqliteOpenMode.ReadOnly, observed.Mode);

        }

        await result.Value.DisposeAsync();

    }

    [Fact]
    public async Task Scoped_open_sync_disposal_closes_without_disposing_scoped_connection_before_release()
    {

        List<string> events = [];

        RecordingLifecycle lifecycle = new(events);

        await using SqliteConnection connection = CanonicalScopedConnection();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: true, events));

        IGrimoireOrdinaryConnectionLease lease = (await factory.AcquireScopedAsync(
            connection,
            CovenantSqliteConnectionMode.ReadWrite,
            CancellationToken.None)).Value;

        lease.Dispose();

        Assert.Equal(ConnectionState.Closed, connection.State);

        Assert.Equal(ConnectionState.Closed, lifecycle.LastRegistration!.StateAtDispose);

        connection.Open();

        Assert.Equal(ConnectionState.Open, connection.State);

        connection.Close();

    }

    [Fact]
    public async Task Fresh_sync_disposal_physically_closes_before_registration_release()
    {

        RecordingLifecycle lifecycle = new();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: true));

        IGrimoireOrdinaryConnectionLease lease = (await factory.OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind.ReadWrite,
            CancellationToken.None)).Value;

        SqliteConnection connection = lease.Connection;

        lease.Dispose();

        Assert.Equal(ConnectionState.Closed, connection.State);

        Assert.Equal(ConnectionState.Closed, lifecycle.LastRegistration!.StateAtDispose);

    }

    [Fact]
    public async Task Async_disposal_physically_closes_before_registration_release()
    {

        RecordingLifecycle lifecycle = new();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: true));

        IGrimoireOrdinaryConnectionLease lease = (await factory.OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind.ReadWrite,
            CancellationToken.None)).Value;

        SqliteConnection connection = lease.Connection;

        await lease.DisposeAsync();

        Assert.Equal(ConnectionState.Closed, connection.State);

        Assert.Equal(ConnectionState.Closed, lifecycle.LastRegistration!.StateAtDispose);

    }

    [Fact]
    public async Task Sync_then_async_disposal_is_cross_idempotent()
    {

        RecordingLifecycle lifecycle = new();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: true));

        IGrimoireOrdinaryConnectionLease lease = (await factory.OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind.ReadWrite,
            CancellationToken.None)).Value;

        lease.Dispose();

        await lease.DisposeAsync();

        Assert.Equal(1, lifecycle.LastRegistration!.DisposeCount);

    }

    [Fact]
    public async Task Async_then_sync_disposal_is_cross_idempotent()
    {

        RecordingLifecycle lifecycle = new();

        GrimoireOrdinaryConnectionFactory factory = CreateFactory(
            lifecycle,
            new RecordingRuntime(initializeProvider: true));

        IGrimoireOrdinaryConnectionLease lease = (await factory.OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind.ReadWrite,
            CancellationToken.None)).Value;

        await lease.DisposeAsync();

        lease.Dispose();

        Assert.Equal(1, lifecycle.LastRegistration!.DisposeCount);

    }

    public void Dispose()
    {

        SqliteConnection.ClearAllPools();

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _originalDotnetEnvironment);

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _originalAspNetCoreEnvironment);

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _originalTestHome);

        if (Directory.Exists(_testHome))
        {

            Directory.Delete(_testHome, recursive: true);

        }

    }

    private static GrimoireOrdinaryConnectionFactory CreateFactory(
        IGrimoireOrdinaryConnectionLifecycle lifecycle,
        ISqliteNativeRuntime runtime,
        ICovenantConnectionDrain? drain = null,
        IGrimoireOrdinaryConnectionFactoryTestSeam? seam = null,
        ICovenantSqliteConnectionInitializer? initializer = null) =>
        new(
            lifecycle,
            drain ?? new RecordingDrain(),
            new FixedPassphraseSource(),
            initializer ?? new RecordingInitializer(),
            runtime,
            seam ?? new RecordingTestSeam());

    private static ServiceProvider CreateProvider(ICovenantConnectionDrain drain)
    {

        ServiceCollection services = new();

        services.AddArcanumGrimoireForCli();

        services.AddSingleton<ICovenantConnectionDrain>(drain);

        return services.BuildServiceProvider();

    }

    private static SqliteConnection CanonicalScopedConnection() =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(
                ArcanumPaths.GrimoireDirectory,
                ".",
                Path.GetFileName(ArcanumPaths.GrimoireDatabaseFile)),

            Mode = SqliteOpenMode.Memory,

            Pooling = false,
        }.ToString());

    private static async Task CreateCanonicalDatabaseAsync()
    {

        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = ArcanumPaths.GrimoireDatabaseFile,

            Password = "ordinary-factory-passphrase",

            Pooling = false,
        }.ToString());

        await connection.OpenAsync();

        await connection.CloseAsync();

    }

    private static void AssertOrdered(IReadOnlyList<string> events, params string[] expected)
    {

        int previous = -1;

        foreach (string value in expected)
        {

            int current = events.ToList().IndexOf(value);

            Assert.True(current > previous, $"Expected '{value}' after index {previous}: {string.Join(", ", events)}");

            previous = current;

        }

    }

    private static IGrimoireClosingOwner BeginClosing(GrimoireConnectionAdmissionGate gate)
    {

        CovenantExclusiveRecoveryOwner owner = new(
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat((byte)3, 32).ToArray()));

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(owner);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        return begun.Value;

    }

    private sealed class RecordingLifecycle(List<string>? events = null)
        : IGrimoireOrdinaryConnectionLifecycle
    {

        private readonly List<string> _events = events ?? [];

        internal int BeginOpenCount { get; private set; }

        internal int BorrowCount { get; private set; }

        internal RecordingRegistration? LastRegistration { get; private set; }

        internal Func<DbConnection, RecordingRegistration>? RegistrationFactory { get; init; }

        internal Result<IGrimoireOrdinaryConnectionRegistration>? BorrowResult { get; init; }

        public IGrimoireOrdinaryConnectionRegistration BeginOpen(DbConnection connection)
        {

            BeginOpenCount++;

            _events.Add("ticket");

            LastRegistration = RegistrationFactory?.Invoke(connection)
                ?? new RecordingRegistration(connection, _events);

            return LastRegistration;

        }

        public Result<IGrimoireOrdinaryConnectionRegistration> BorrowCurrentOpen(DbConnection connection)
        {

            BorrowCount++;

            return BorrowResult
                ?? Result<IGrimoireOrdinaryConnectionRegistration>.Failure(
                    new Error(ErrorCodes.Covenant.Unavailable, "not admitted"));

        }

        public void ReleaseAfterExternalClose(DbConnection connection)
        {
        }

    }

    private sealed class RecordingRegistration(
        DbConnection? connection,
        List<string>? events = null) : IGrimoireOrdinaryConnectionRegistration
    {

        private readonly List<string> _events = events ?? [];

        private DbConnection? _connection = connection;

        internal Result RevalidateResult { get; init; } = Result.Success();

        internal Result OpenedResult { get; init; } = Result.Success();

        internal int DisposeCount { get; private set; }

        internal ConnectionState? StateAtDispose { get; private set; }

        public DbConnection Connection => _connection
            ?? throw new InvalidOperationException("The recording connection was not assigned.");

        public long Generation => 1;

        public Result RevalidateAfterNativeOpen()
        {

            _events.Add("revalidate");

            return RevalidateResult;

        }

        public Result MarkOpened()
        {

            _events.Add("opened");

            return OpenedResult;

        }

        public void MarkFailed() => _events.Add("failed");

        public void MarkRefusedAfterOpen() => _events.Add("refused");

        public void Dispose()
        {

            DisposeCount++;

            StateAtDispose = Connection.State;

            _events.Add("release");

        }

        internal RecordingRegistration ConnectionOverride(DbConnection value)
        {

            _connection = value;

            return this;

        }

    }

    private sealed class RecordingDrain(List<string>? events = null) : ICovenantConnectionDrain
    {

        private readonly List<string> _events = events ?? [];

        internal int RegisterCount { get; private set; }

        internal int ClearCount { get; private set; }

        internal SqliteConnection? ClearedConnection { get; private set; }

        internal ConnectionState? StateAtClear { get; private set; }

        public IDisposable Register(SqliteConnection connection)
        {

            RegisterCount++;

            _events.Add("enroll");

            return new NoopDisposable();

        }

        public Result ClearExactPoolAfterClose(SqliteConnection connection)
        {

            ClearCount++;

            ClearedConnection = connection;

            StateAtClear = connection.State;

            _events.Add("clear");

            return connection.State == ConnectionState.Closed
                ? Result.Success()
                : Result.Failure(
                    new Error(ErrorCodes.Covenant.MaintenanceFailed, "connection was not closed"));

        }

        public Task<Result> DrainAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

    }

    private sealed class BlockingStageTwoDrain : ICovenantConnectionDrain
    {

        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _released.TrySetResult();

        public IDisposable Register(SqliteConnection connection) => new NoopDisposable();

        public Result ClearExactPoolAfterClose(SqliteConnection connection) => Result.Success();

        public async Task<Result> DrainAsync(CancellationToken cancellationToken)
        {

            _entered.TrySetResult();

            await _released.Task.WaitAsync(cancellationToken);

            return Result.Success();

        }

    }

    private sealed class RecordingRuntime(
        bool initializeProvider,
        List<string>? events = null) : ISqliteNativeRuntime
    {

        private readonly List<string> _events = events ?? [];

        internal Exception? Failure { get; init; }

        internal int InitializeCount { get; private set; }

        public void Initialize()
        {

            InitializeCount++;

            _events.Add("runtime");

            if (Failure is not null)
            {

                throw Failure;

            }

            if (initializeProvider)
            {

                SqliteNativeRuntime.Instance.Initialize();

            }

        }

    }

    private sealed class RecordingInitializer(List<string>? events = null)
        : ICovenantSqliteConnectionInitializer
    {

        private readonly List<string> _events = events ?? [];

        internal Exception? Failure { get; init; }

        internal Action<SqliteConnection>? OnInitialize { get; init; }

        public ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            _events.Add("initializer");

            OnInitialize?.Invoke(connection);

            return Failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(Failure);

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

    private sealed class RecordingTestSeam(List<string>? events = null)
        : IGrimoireOrdinaryConnectionFactoryTestSeam
    {

        private readonly List<string> _events = events ?? [];

        private readonly TaskCompletionSource _nativeOpenEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowNativeOpen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Exception? NativeOpenFailure { get; init; }

        internal bool BlockNativeOpen { get; init; }

        internal Task NativeOpenEntered => _nativeOpenEntered.Task;

        internal int BeforeProviderConstructionCount { get; private set; }

        internal int BeforeNativeOpenCount { get; private set; }

        internal SqliteConnection? ClearedConnection { get; private set; }

        public void BeforeProviderConstruction()
        {

            BeforeProviderConstructionCount++;

            _events.Add("provider-construction");

        }

        internal void AllowNativeOpen() => _allowNativeOpen.TrySetResult();

        public async ValueTask BeforeNativeOpenAsync(CancellationToken cancellationToken)
        {

            BeforeNativeOpenCount++;

            _events.Add("native-open");

            _nativeOpenEntered.TrySetResult();

            if (NativeOpenFailure is not null)
            {

                throw NativeOpenFailure;

            }

            if (BlockNativeOpen)
            {

                await _allowNativeOpen.Task.WaitAsync(cancellationToken);

            }

        }

        public void AfterExactPoolClear(SqliteConnection connection)
        {

            ClearedConnection = connection;

            _events.Add("after-clear");

        }

    }

    private sealed class FixedPassphraseSource : IGrimoireDbPassphraseSource
    {

        public string Passphrase { get; private set; } = "ordinary-factory-passphrase";

        public void SetPassphrase(string passphrase) => Passphrase = passphrase;

    }

    private sealed class NoopDisposable : IDisposable
    {

        public void Dispose()
        {
        }

    }

    private sealed class MalformedConnection : SqliteConnection
    {

        [AllowNull]
        public override string ConnectionString
        {
            get => "not-a-valid-connection-string";

            set
            {

                if (string.IsNullOrEmpty(value))
                {

                    base.ConnectionString = value;

                    return;

                }

                throw new NotSupportedException();

            }
        }

    }

}
