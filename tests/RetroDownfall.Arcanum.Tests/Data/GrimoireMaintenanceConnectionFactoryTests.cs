using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("ProcessEnvironment")]
public sealed class GrimoireMaintenanceConnectionFactoryTests : IDisposable
{

    private readonly string? _originalDotnetEnvironment;

    private readonly string? _originalAspNetCoreEnvironment;

    private readonly string? _originalTestHome;

    private readonly string _testHome = Path.Combine(
        Path.GetTempPath(),
        "arcanum-maintenance-factory-tests",
        Guid.NewGuid().ToString("N"));

    public GrimoireMaintenanceConnectionFactoryTests()
    {

        _originalDotnetEnvironment = global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        _originalAspNetCoreEnvironment = global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        _originalTestHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _testHome);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

    }

    [Theory]
    [InlineData(CapabilityMismatch.ForeignOwner)]
    [InlineData(CapabilityMismatch.ForeignGeneration)]
    [InlineData(CapabilityMismatch.WrongLaneInstance)]
    [InlineData(CapabilityMismatch.WrongPurpose)]
    [InlineData(CapabilityMismatch.Reused)]
    [InlineData(CapabilityMismatch.Disposed)]
    public async Task Capability_mismatch_or_terminal_state_is_typed_and_performs_zero_construction_or_open(
        CapabilityMismatch mismatch)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        CovenantExclusiveRecoveryOwner owner = Owner(1);

        await using IGrimoireExclusiveClosedLease closed = await Close(gate, owner);

        IGrimoireMaintenanceIoLane exactLane =
            (await closed.AcquireMaintenanceIoLaneAsync(
                static (_, _, _) => ValueTask.FromResult(true),
                CancellationToken.None)).Value;

        CovenantMaintenanceConnectionPurpose capabilityPurpose = mismatch == CapabilityMismatch.WrongPurpose
            ? CovenantMaintenanceConnectionPurpose.Compaction
            : CovenantMaintenanceConnectionPurpose.CanonicalErasure;

        IGrimoireMaintenanceConnectionCapability capability =
            closed.IssueMaintenanceConnectionCapability(capabilityPurpose, exactLane).Value;

        IGrimoireMaintenanceIoLane suppliedLane = mismatch switch
        {
            CapabilityMismatch.ForeignOwner => new ForeignLane(
                Owner(2),
                exactLane.Generation),

            CapabilityMismatch.ForeignGeneration => new ForeignLane(
                exactLane.Owner,
                checked(exactLane.Generation + 1)),

            CapabilityMismatch.WrongLaneInstance => new ForeignLane(
                exactLane.Owner,
                exactLane.Generation),

            _ => exactLane,
        };

        if (mismatch == CapabilityMismatch.Reused)
        {

            Result<IGrimoireTrackedMaintenanceHandle> first = capability.Consume(
                exactLane.Owner,
                exactLane.Generation,
                CovenantMaintenanceConnectionPurpose.CanonicalErasure,
                exactLane);

            Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

            Assert.True(first.Value.ReportNotOpened().IsSuccess);

        }
        else if (mismatch == CapabilityMismatch.Disposed)
        {

            await capability.DisposeAsync();

        }

        RecordingRuntime runtime = new(initializeProvider: false)
        {
            Failure = new InvalidOperationException("Runtime must remain unreachable."),
        };

        RecordingPassphraseSource passphrase = new();

        RecordingInitializer initializer = new();

        GrimoireMaintenanceConnectionFactory factory = new(
            passphrase,
            initializer,
            runtime);

        Result<IGrimoireMaintenanceConnectionLease> result =
            await factory.OpenJournalCanonicalErasureAsync(
                capability,
                suppliedLane,
                CancellationToken.None);

        AssertUnavailable(result);

        Assert.Equal(0, runtime.InitializeCount);

        Assert.Equal(0, passphrase.ReadCount);

        Assert.Equal(0, initializer.InitializeCount);

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

        await capability.DisposeAsync();

        await exactLane.DisposeAsync();

        Result keptClosed = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(keptClosed.IsSuccess, keptClosed.IsFailure ? keptClosed.Error.Message : null);

    }

    [Fact]
    public async Task Adopter_owned_interlock_refuses_a_retired_lane_before_construction_or_open()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        CovenantExclusiveRecoveryOwner owner = Owner(3);

        await using IGrimoireExclusiveClosedLease closed = await Close(gate, owner);

        IGrimoireMaintenanceIoLane retiredLane =
            (await closed.AcquireMaintenanceIoLaneAsync(
                static (_, _, _) => ValueTask.FromResult(true),
                CancellationToken.None)).Value;

        await using IGrimoireMaintenanceConnectionCapability capability =
            closed.IssueMaintenanceConnectionCapability(
                CovenantMaintenanceConnectionPurpose.CanonicalErasure,
                retiredLane).Value;

        await retiredLane.DisposeAsync();

        Result<IGrimoireExpiredLeaseAdoptionInterlock> adopted =
            await gate.AcquireExpiredLeaseAdoptionInterlockAsync(
                owner,
                static (_, _) => ValueTask.FromResult(true),
                CancellationToken.None);

        Assert.True(adopted.IsSuccess, adopted.IsFailure ? adopted.Error.Message : null);

        await using IGrimoireExpiredLeaseAdoptionInterlock adoption = adopted.Value;

        RecordingRuntime runtime = new(initializeProvider: false)
        {
            Failure = new InvalidOperationException("Runtime must remain unreachable."),
        };

        RecordingPassphraseSource passphrase = new();

        RecordingInitializer initializer = new();

        GrimoireMaintenanceConnectionFactory factory = new(
            passphrase,
            initializer,
            runtime);

        Result<IGrimoireMaintenanceConnectionLease> result =
            await factory.OpenJournalCanonicalErasureAsync(
                capability,
                retiredLane,
                CancellationToken.None);

        AssertUnavailable(result);

        Assert.Equal(0, runtime.InitializeCount);

        Assert.Equal(0, passphrase.ReadCount);

        Assert.Equal(0, initializer.InitializeCount);

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

    }

    [Fact]
    public async Task Native_runtime_failure_precedes_provider_construction_and_reports_not_opened_once()
    {

        List<string> events = [];

        RecordingHandle handle = new(events);

        RecordingCapability capability = new(handle, events);

        RecordingRuntime runtime = new(initializeProvider: false, events)
        {
            Failure = new InvalidOperationException("native runtime failure"),
        };

        RecordingPassphraseSource passphrase = new(events);

        GrimoireMaintenanceConnectionFactory factory = new(
            passphrase,
            new RecordingInitializer(events),
            runtime);

        Result<IGrimoireMaintenanceConnectionLease> result =
            await factory.OpenJournalCanonicalErasureAsync(
                capability,
                new RecordingLane(Owner(4), 7),
                CancellationToken.None);

        AssertUnavailable(result);

        Assert.Equal(1, capability.ConsumeCount);

        Assert.Equal(1, runtime.InitializeCount);

        Assert.Equal(0, passphrase.ReadCount);

        Assert.Equal(0, handle.OpenStartedCount);

        Assert.Equal(1, handle.NotOpenedCount);

        Assert.Equal(0, handle.PhysicallyClosedCount);

        Assert.Equal(["consume", "runtime", "not-opened"], events);

    }

    [Fact]
    public async Task Provider_construction_failure_reports_not_opened_once_before_any_open_start()
    {

        List<string> events = [];

        RecordingHandle handle = new(events);

        RecordingCapability capability = new(handle, events);

        RecordingRuntime runtime = new(initializeProvider: false, events);

        ThrowingPassphraseSource passphrase = new(events);

        GrimoireMaintenanceConnectionFactory factory = new(
            passphrase,
            new RecordingInitializer(events),
            runtime);

        Result<IGrimoireMaintenanceConnectionLease> result =
            await factory.OpenJournalCanonicalErasureAsync(
                capability,
                new RecordingLane(Owner(5), 8),
                CancellationToken.None);

        AssertUnavailable(result);

        Assert.Equal(1, passphrase.ReadCount);

        Assert.Equal(0, handle.OpenStartedCount);

        Assert.Equal(1, handle.NotOpenedCount);

        Assert.Equal(0, handle.PhysicallyClosedCount);

        Assert.Equal(["consume", "runtime", "passphrase", "not-opened"], events);

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

    }

    [Fact]
    public async Task Open_start_refusal_disposes_the_unopened_provider_and_reports_not_opened_once()
    {

        List<string> events = [];

        RecordingHandle handle = new(events)
        {
            OpenStartedResult = Result.Failure(
                new Error(ErrorCodes.Covenant.Unavailable, "open start refused")),
        };

        GrimoireMaintenanceConnectionFactory factory = new(
            new RecordingPassphraseSource(events),
            new RecordingInitializer(events),
            new RecordingRuntime(initializeProvider: true, events));

        Result<IGrimoireMaintenanceConnectionLease> result =
            await factory.OpenJournalCanonicalErasureAsync(
                new RecordingCapability(handle, events),
                new RecordingLane(Owner(6), 9),
                CancellationToken.None);

        AssertUnavailable(result);

        Assert.Equal(1, handle.OpenStartedCount);

        Assert.Equal(1, handle.NotOpenedCount);

        Assert.Equal(0, handle.PhysicallyClosedCount);

        Assert.Equal(
            ["consume", "runtime", "passphrase", "open-start", "not-opened"],
            events);

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

    }

    [Fact]
    public async Task Native_open_failure_reports_physical_closure_once_after_open_start()
    {

        Directory.Delete(ArcanumPaths.GrimoireDirectory, recursive: true);

        List<string> events = [];

        RecordingHandle handle = new(events);

        RecordingInitializer initializer = new(events);

        GrimoireMaintenanceConnectionFactory factory = new(
            new RecordingPassphraseSource(events),
            initializer,
            new RecordingRuntime(initializeProvider: true, events));

        Result<IGrimoireMaintenanceConnectionLease> result =
            await factory.OpenJournalCanonicalErasureAsync(
                new RecordingCapability(handle, events),
                new RecordingLane(Owner(7), 10),
                CancellationToken.None);

        AssertUnavailable(result);

        Assert.Equal(1, handle.OpenStartedCount);

        Assert.Equal(0, handle.NotOpenedCount);

        Assert.Equal(1, handle.PhysicallyClosedCount);

        Assert.Equal(ConnectionState.Closed, handle.StateAtPhysicalClose);

        Assert.Equal(0, initializer.InitializeCount);

        AssertOrdered(events, "open-start", "physically-closed");

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initializer_failure_or_cancellation_disposes_before_reporting_physical_closure(
        bool cancellation)
    {

        List<string> events = [];

        RecordingHandle handle = new(events);

        RecordingInitializer initializer = new(events)
        {
            Failure = cancellation
                ? new OperationCanceledException("initializer cancelled")
                : new InvalidOperationException("initializer failure"),
        };

        handle.ObserveState = () => initializer.Connection?.State;

        GrimoireMaintenanceConnectionFactory factory = new(
            new RecordingPassphraseSource(events),
            initializer,
            new RecordingRuntime(initializeProvider: true, events));

        Task<Result<IGrimoireMaintenanceConnectionLease>> open =
            factory.OpenJournalCanonicalErasureAsync(
                new RecordingCapability(handle, events),
                new RecordingLane(Owner(8), 11),
                CancellationToken.None);

        if (cancellation)
        {

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);

        }
        else
        {

            Result<IGrimoireMaintenanceConnectionLease> result = await open;

            AssertUnavailable(result);

        }

        Assert.Equal(CovenantSqliteConnectionMode.ExclusiveMaintenance, initializer.Mode);

        Assert.Equal(1, handle.OpenStartedCount);

        Assert.Equal(0, handle.NotOpenedCount);

        Assert.Equal(1, handle.PhysicallyClosedCount);

        Assert.Equal(ConnectionState.Closed, handle.StateAtPhysicalClose);

        Assert.NotNull(initializer.Connection);

        Assert.Equal(ConnectionState.Closed, initializer.Connection.State);

        AssertOrdered(events, "open-start", "initializer", "physically-closed");

    }

    [Fact]
    public async Task Successful_open_is_canonical_unpooled_read_write_and_exclusively_initialized()
    {

        List<string> events = [];

        RecordingHandle handle = new(events);

        RecordingInitializer initializer = new(events);

        GrimoireMaintenanceConnectionFactory factory = new(
            new RecordingPassphraseSource(events),
            initializer,
            new RecordingRuntime(initializeProvider: true, events));

        Result<IGrimoireMaintenanceConnectionLease> result =
            await factory.OpenJournalCanonicalErasureAsync(
                new RecordingCapability(handle, events),
                new RecordingLane(Owner(9), 12),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        IGrimoireMaintenanceConnectionLease lease = result.Value;

        handle.ObserveState = () => lease.Connection.State;

        SqliteConnectionStringBuilder builder = new(lease.Connection.ConnectionString);

        Assert.Equal(ArcanumPaths.GrimoireDatabaseFile, builder.DataSource);

        Assert.Equal(SqliteOpenMode.ReadWriteCreate, builder.Mode);

        Assert.False(builder.Pooling);

        Assert.Equal(ConnectionState.Open, lease.Connection.State);

        Assert.Same(lease.Connection, initializer.Connection);

        Assert.Equal(CovenantSqliteConnectionMode.ExclusiveMaintenance, initializer.Mode);

        Assert.Equal(1, handle.OpenStartedCount);

        Assert.Equal(0, handle.NotOpenedCount);

        Assert.Equal(0, handle.PhysicallyClosedCount);

        AssertOrdered(events, "consume", "runtime", "passphrase", "open-start", "initializer");

        await lease.DisposeAsync();

    }

    [Fact]
    public async Task Lease_disposal_closes_and_disposes_before_reporting_once()
    {

        List<string> events = [];

        RecordingHandle handle = new(events);

        RecordingInitializer initializer = new(events);

        GrimoireMaintenanceConnectionFactory factory = new(
            new RecordingPassphraseSource(events),
            initializer,
            new RecordingRuntime(initializeProvider: true, events));

        IGrimoireMaintenanceConnectionLease lease =
            (await factory.OpenJournalCanonicalErasureAsync(
                new RecordingCapability(handle, events),
                new RecordingLane(Owner(10), 13),
                CancellationToken.None)).Value;

        SqliteConnection connection = lease.Connection;

        handle.ObserveState = () => connection.State;

        await lease.DisposeAsync();

        await lease.DisposeAsync();

        Assert.Equal(ConnectionState.Closed, connection.State);

        Assert.Equal(1, handle.PhysicallyClosedCount);

        Assert.Equal(ConnectionState.Closed, handle.StateAtPhysicalClose);

        AssertOrdered(events, "initializer", "physically-closed");

    }

    [Fact]
    public async Task Lane_disposal_waits_for_the_factory_lease_to_physically_close()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        CovenantExclusiveRecoveryOwner owner = Owner(11);

        await using IGrimoireExclusiveClosedLease closed = await Close(gate, owner);

        IGrimoireMaintenanceIoLane lane =
            (await closed.AcquireMaintenanceIoLaneAsync(
                static (_, _, _) => ValueTask.FromResult(true),
                CancellationToken.None)).Value;

        await using IGrimoireMaintenanceConnectionCapability capability =
            closed.IssueMaintenanceConnectionCapability(
                CovenantMaintenanceConnectionPurpose.CanonicalErasure,
                lane).Value;

        GrimoireMaintenanceConnectionFactory factory = new(
            new RecordingPassphraseSource(),
            new RecordingInitializer(),
            new RecordingRuntime(initializeProvider: true));

        Result<IGrimoireMaintenanceConnectionLease> opened =
            await factory.OpenJournalCanonicalErasureAsync(
                capability,
                lane,
                CancellationToken.None);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

        Task laneDisposal = lane.DisposeAsync().AsTask();

        Assert.False(laneDisposal.IsCompleted);

        await opened.Value.DisposeAsync();

        await laneDisposal;

        Result keptClosed = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(keptClosed.IsSuccess, keptClosed.IsFailure ? keptClosed.Error.Message : null);

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

    private static GrimoireConnectionAdmissionGate CreateGate() =>
        new(
            TimeProvider.System,
            new NoOpDrain(),
            TimeSpan.FromSeconds(1));

    private static async Task<IGrimoireExclusiveClosedLease> Close(
        GrimoireConnectionAdmissionGate gate,
        CovenantExclusiveRecoveryOwner owner)
    {

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(owner);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        await using IGrimoireClosingOwner closing = begun.Value;

        Result<IGrimoireExclusiveClosedLease> closed =
            await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        return closed.Value;

    }

    private static CovenantExclusiveRecoveryOwner Owner(byte seed) =>
        new(
            Guid.Parse($"00000000-0000-0000-0000-{seed:D12}"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat(seed, 32).ToArray()));

    private static void AssertUnavailable(Result<IGrimoireMaintenanceConnectionLease> result)
    {

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);

    }

    private static void AssertOrdered(IReadOnlyList<string> events, params string[] expected)
    {

        int previous = -1;

        foreach (string value in expected)
        {

            int current = events.ToList().IndexOf(value);

            Assert.True(
                current > previous,
                $"Expected '{value}' after index {previous}: {string.Join(", ", events)}");

            previous = current;

        }

    }

    public enum CapabilityMismatch : byte
    {

        ForeignOwner = 1,

        ForeignGeneration = 2,

        WrongLaneInstance = 3,

        WrongPurpose = 6,

        Reused = 7,

        Disposed = 8,

    }

    private sealed class RecordingCapability(
        IGrimoireTrackedMaintenanceHandle handle,
        List<string>? events = null) : IGrimoireMaintenanceConnectionCapability
    {

        private readonly List<string> _events = events ?? [];

        internal int ConsumeCount { get; private set; }

        public string CanonicalPath { get; init; } = ArcanumPaths.GrimoireDatabaseFile;

        public CovenantMaintenanceConnectionMode Mode { get; init; } =
            CovenantMaintenanceConnectionMode.ReadWrite;

        public CovenantMaintenanceConnectionPurpose Purpose { get; init; } =
            CovenantMaintenanceConnectionPurpose.CanonicalErasure;

        public Result<IGrimoireTrackedMaintenanceHandle> Consume(
            CovenantExclusiveRecoveryOwner owner,
            long generation,
            CovenantMaintenanceConnectionPurpose purpose,
            IGrimoireMaintenanceIoLane lane)
        {

            ConsumeCount++;

            _events.Add("consume");

            return Result<IGrimoireTrackedMaintenanceHandle>.Success(handle);

        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

    private sealed class RecordingHandle(List<string>? events = null)
        : IGrimoireTrackedMaintenanceHandle
    {

        private readonly List<string> _events = events ?? [];

        internal Func<ConnectionState?>? ObserveState { get; set; }

        internal Result OpenStartedResult { get; init; } = Result.Success();

        internal int OpenStartedCount { get; private set; }

        internal int NotOpenedCount { get; private set; }

        internal int PhysicallyClosedCount { get; private set; }

        internal ConnectionState? StateAtPhysicalClose { get; private set; } = ConnectionState.Closed;

        public Result ReportOpenStarted()
        {

            OpenStartedCount++;

            _events.Add("open-start");

            return OpenStartedResult;

        }

        public Result ReportNotOpened()
        {

            NotOpenedCount++;

            _events.Add("not-opened");

            return Result.Success();

        }

        public Result ReportPhysicallyClosed()
        {

            PhysicallyClosedCount++;

            StateAtPhysicalClose = ObserveState?.Invoke() ?? ConnectionState.Closed;

            _events.Add("physically-closed");

            return Result.Success();

        }

        public ValueTask DisposeAsync()
        {

            _events.Add("handle-disposed");

            return ValueTask.CompletedTask;

        }

    }

    private class RecordingLane(
        CovenantExclusiveRecoveryOwner owner,
        long generation) : IGrimoireMaintenanceIoLane
    {

        public CovenantExclusiveRecoveryOwner Owner { get; } = owner;

        public long Generation { get; } = generation;

        public ValueTask<Result> RevalidateDurableOwnerAsync(
            Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>>
                revalidateDurableOwnerAsync,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

    private sealed class ForeignLane(
        CovenantExclusiveRecoveryOwner owner,
        long generation) : RecordingLane(owner, generation);

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

    private class RecordingPassphraseSource(List<string>? events = null)
        : IGrimoireDbPassphraseSource
    {

        private readonly List<string> _events = events ?? [];

        internal int ReadCount { get; private set; }

        public virtual string Passphrase
        {
            get
            {

                ReadCount++;

                _events.Add("passphrase");

                return "journal-maintenance-passphrase";

            }
        }

        public void SetPassphrase(string passphrase) => throw new NotSupportedException();

    }

    private sealed class ThrowingPassphraseSource(List<string>? events = null)
        : RecordingPassphraseSource(events)
    {

        public override string Passphrase
        {
            get
            {

                _ = base.Passphrase;

                throw new InvalidOperationException("provider construction failure");

            }
        }

    }

    private sealed class RecordingInitializer(List<string>? events = null)
        : ICovenantSqliteConnectionInitializer
    {

        private readonly List<string> _events = events ?? [];

        internal Exception? Failure { get; init; }

        internal int InitializeCount { get; private set; }

        internal SqliteConnection? Connection { get; private set; }

        internal CovenantSqliteConnectionMode? Mode { get; private set; }

        public ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            InitializeCount++;

            Connection = connection;

            Mode = mode;

            _events.Add("initializer");

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

    private sealed class NoOpDrain : ICovenantConnectionDrain
    {

        public IDisposable Register(SqliteConnection connection) => new NoOpDisposable();

        public Result ClearExactPoolAfterClose(SqliteConnection connection) => Result.Success();

        public Task<Result> DrainAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        private sealed class NoOpDisposable : IDisposable
        {

            public void Dispose()
            {
            }

        }

    }

}
