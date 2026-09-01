using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

[Collection("ProcessEnvironment")]
public sealed class StoppedHostGrimoireConnectionAuthorityTests : IDisposable
{

    private readonly string? _originalTestHome;

    private readonly string _testHome = Path.Combine(
        Path.GetTempPath(),
        "arcanum-stopped-host-authority-tests",
        Guid.NewGuid().ToString("N"));

    public StoppedHostGrimoireConnectionAuthorityTests()
    {

        _originalTestHome = global::System.Environment.GetEnvironmentVariable(
            "ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable(
            "ARCANUM_TEST_HOME",
            _testHome);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

    }

    public void Dispose()
    {

        global::System.Environment.SetEnvironmentVariable(
            "ARCANUM_TEST_HOME",
            _originalTestHome);

        try
        {

            Directory.Delete(_testHome, recursive: true);

        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

    }

    public static TheoryData<int, int> WrongPurposeMatrix() =>
        new()
        {
            { 0, 1 },
            { 1, 2 },
            { 2, 3 },
            { 3, 4 },
            { 4, 5 },
            { 5, 0 },
        };

    [Theory]
    [MemberData(nameof(WrongPurposeMatrix))]
    public async Task Wrong_operation_authority_is_refused_before_provider_construction_or_open(
        int issuedOperation,
        int openedOperation)
    {

        using ArcanumMaintenanceLock held = Acquire(ArcanumPaths.GrimoireDirectory);

        StoppedHostGrimoireAuthorityIssuer issuer = Issuer(held);

        await using IStoppedHostGrimoireConnectionAuthority authority =
            Issue(issuer, issuedOperation).Value;

        RecordingStoppedHostFactoryTestSeam seam = new();

        StoppedHostGrimoireConnectionFactory factory = Factory(
            new RecordingNativeRuntime(),
            seam);

        Result<IStoppedHostGrimoireConnectionLease> result = await Open(
            factory,
            openedOperation,
            authority,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, seam.ProviderConstructionCount);

        Assert.Equal(0, seam.NativeOpenCount);

    }

    [Fact]
    public async Task Foreign_authority_implementation_is_refused_before_provider_construction_or_open()
    {

        RecordingStoppedHostFactoryTestSeam seam = new();

        StoppedHostGrimoireConnectionFactory factory = Factory(
            new RecordingNativeRuntime(),
            seam);

        await using ForeignAuthority authority = new();

        Result<IStoppedHostGrimoireConnectionLease> result = await factory
            .OpenStoppedHostInstallationResetPlanReadAsync(
                authority,
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, seam.ProviderConstructionCount);

        Assert.Equal(0, seam.NativeOpenCount);

    }

    [Fact]
    public void Wrong_guarded_root_is_rejected_during_issuer_construction()
    {

        using ArcanumMaintenanceLock held = Acquire(ArcanumPaths.GrimoireDirectory);

        string wrongRoot = Path.Combine(_testHome, "wrong-root");

        Directory.CreateDirectory(wrongRoot);

        _ = Assert.Throws<InvalidOperationException>(() =>
            new StoppedHostGrimoireAuthorityIssuer(
                held,
                wrongRoot,
                ArcanumPaths.GrimoireDatabaseFile));

    }

    [Fact]
    public async Task Wrong_canonical_path_is_refused_before_provider_construction_or_open()
    {

        using ArcanumMaintenanceLock held = Acquire(ArcanumPaths.GrimoireDirectory);

        StoppedHostGrimoireAuthorityIssuer issuer = new(
            held,
            ArcanumPaths.GrimoireDirectory,
            ArcanumPaths.GrimoireDatabaseFile + ".wrong");

        await using IStoppedHostGrimoireConnectionAuthority authority = issuer
            .IssueStoppedHostInstallationResetPlanReadAuthority().Value;

        RecordingStoppedHostFactoryTestSeam seam = new();

        StoppedHostGrimoireConnectionFactory factory = Factory(
            new RecordingNativeRuntime(),
            seam);

        Result<IStoppedHostGrimoireConnectionLease> result = await factory
            .OpenStoppedHostInstallationResetPlanReadAsync(
                authority,
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, seam.ProviderConstructionCount);

        Assert.Equal(0, seam.NativeOpenCount);

    }

    [Fact]
    public async Task Reused_authority_is_refused_before_a_second_provider_construction_or_open()
    {

        using ArcanumMaintenanceLock held = Acquire(ArcanumPaths.GrimoireDirectory);

        await using IStoppedHostGrimoireConnectionAuthority authority = Issuer(held)
            .IssueStoppedHostInstallationResetPlanReadAuthority().Value;

        RecordingNativeRuntime runtime = new()
        {
            Failure = new InvalidOperationException("stop before provider construction"),
        };

        RecordingStoppedHostFactoryTestSeam seam = new();

        StoppedHostGrimoireConnectionFactory factory = Factory(runtime, seam);

        Result<IStoppedHostGrimoireConnectionLease> first = await factory
            .OpenStoppedHostInstallationResetPlanReadAsync(
                authority,
                CancellationToken.None);

        Result<IStoppedHostGrimoireConnectionLease> replay = await factory
            .OpenStoppedHostInstallationResetPlanReadAsync(
                authority,
                CancellationToken.None);

        Assert.True(first.IsFailure);

        Assert.True(replay.IsFailure);

        Assert.Equal(1, runtime.InitializeCount);

        Assert.Equal(0, seam.ProviderConstructionCount);

        Assert.Equal(0, seam.NativeOpenCount);

    }

    [Fact]
    public async Task Disposed_authority_is_refused_before_provider_construction_or_open()
    {

        using ArcanumMaintenanceLock held = Acquire(ArcanumPaths.GrimoireDirectory);

        IStoppedHostGrimoireConnectionAuthority authority = Issuer(held)
            .IssueStoppedHostInstallationResetPlanReadAuthority().Value;

        await authority.DisposeAsync();

        RecordingStoppedHostFactoryTestSeam seam = new();

        Result<IStoppedHostGrimoireConnectionLease> result = await Factory(
                new RecordingNativeRuntime(),
                seam)
            .OpenStoppedHostInstallationResetPlanReadAsync(
                authority,
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, seam.ProviderConstructionCount);

        Assert.Equal(0, seam.NativeOpenCount);

    }

    [Fact]
    public async Task Disposed_original_lock_is_not_replaced_by_a_new_lock_instance()
    {

        ArcanumMaintenanceLock original = Acquire(ArcanumPaths.GrimoireDirectory);

        IStoppedHostGrimoireConnectionAuthority authority = Issuer(original)
            .IssueStoppedHostInstallationResetPlanReadAuthority().Value;

        original.Dispose();

        using ArcanumMaintenanceLock replacement = Acquire(ArcanumPaths.GrimoireDirectory);

        RecordingStoppedHostFactoryTestSeam seam = new();

        Result<IStoppedHostGrimoireConnectionLease> result = await Factory(
                new RecordingNativeRuntime(),
                seam)
            .OpenStoppedHostInstallationResetPlanReadAsync(
                authority,
                CancellationToken.None);

        await authority.DisposeAsync();

        Assert.True(result.IsFailure);

        Assert.Equal(0, seam.ProviderConstructionCount);

        Assert.Equal(0, seam.NativeOpenCount);

    }

    [Fact]
    public async Task Native_runtime_failure_constructs_and_opens_no_provider()
    {

        using ArcanumMaintenanceLock held = Acquire(ArcanumPaths.GrimoireDirectory);

        await using IStoppedHostGrimoireConnectionAuthority authority = Issuer(held)
            .IssueStoppedHostInstallationResetPlanReadAuthority().Value;

        RecordingNativeRuntime runtime = new()
        {
            Failure = new InvalidOperationException("native runtime unavailable"),
        };

        RecordingStoppedHostFactoryTestSeam seam = new();

        Result<IStoppedHostGrimoireConnectionLease> result = await Factory(runtime, seam)
            .OpenStoppedHostInstallationResetPlanReadAsync(
                authority,
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, runtime.InitializeCount);

        Assert.Equal(0, seam.ProviderConstructionCount);

        Assert.Equal(0, seam.NativeOpenCount);

    }

    [Fact]
    public async Task Cancellation_constructs_and_opens_no_provider()
    {

        using ArcanumMaintenanceLock held = Acquire(ArcanumPaths.GrimoireDirectory);

        await using IStoppedHostGrimoireConnectionAuthority authority = Issuer(held)
            .IssueStoppedHostInstallationResetPlanReadAuthority().Value;

        RecordingStoppedHostFactoryTestSeam seam = new();

        using CancellationTokenSource canceled = new();

        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Factory(
                new RecordingNativeRuntime(),
                seam)
            .OpenStoppedHostInstallationResetPlanReadAsync(
                authority,
                canceled.Token));

        Assert.Equal(0, seam.ProviderConstructionCount);

        Assert.Equal(0, seam.NativeOpenCount);

    }

    [Fact]
    public async Task Lock_is_revalidated_after_native_initialization_before_provider_construction()
    {

        ArcanumMaintenanceLock held = Acquire(ArcanumPaths.GrimoireDirectory);

        await using IStoppedHostGrimoireConnectionAuthority authority = Issuer(held)
            .IssueStoppedHostInstallationResetPlanReadAuthority().Value;

        RecordingNativeRuntime runtime = new()
        {
            AfterInitialize = held.Dispose,
        };

        RecordingStoppedHostFactoryTestSeam seam = new();

        Result<IStoppedHostGrimoireConnectionLease> result = await Factory(runtime, seam)
            .OpenStoppedHostInstallationResetPlanReadAsync(
                authority,
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, seam.ProviderConstructionCount);

        Assert.Equal(0, seam.NativeOpenCount);

    }

    [Fact]
    public async Task Lock_is_revalidated_immediately_before_native_open()
    {

        ArcanumMaintenanceLock held = Acquire(ArcanumPaths.GrimoireDirectory);

        await using IStoppedHostGrimoireConnectionAuthority authority = Issuer(held)
            .IssueStoppedHostInstallationResetPlanReadAuthority().Value;

        RecordingStoppedHostFactoryTestSeam seam = new()
        {
            OnAfterProviderConstruction = held.Dispose,
        };

        Result<IStoppedHostGrimoireConnectionLease> result = await Factory(
                new RecordingNativeRuntime(initializeProvider: true),
                seam)
            .OpenStoppedHostInstallationResetPlanReadAsync(
                authority,
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, seam.ProviderConstructionCount);

        Assert.Equal(0, seam.NativeOpenCount);

    }

    private StoppedHostGrimoireAuthorityIssuer Issuer(ArcanumMaintenanceLock held) =>
        new(
            held,
            ArcanumPaths.GrimoireDirectory,
            ArcanumPaths.GrimoireDatabaseFile);

    private static StoppedHostGrimoireConnectionFactory Factory(
        ISqliteNativeRuntime runtime,
        IStoppedHostGrimoireConnectionFactoryTestSeam seam) =>
        new(
            new FixedPassphrase(),
            runtime,
            new RecordingInitializer(),
            seam);

    private static ArcanumMaintenanceLock Acquire(string root) =>
        ArcanumMaintenanceLock.TryAcquire(root)
        ?? throw new InvalidOperationException("The test maintenance lock was contended.");

    private static Result<IStoppedHostGrimoireConnectionAuthority> Issue(
        IStoppedHostGrimoireAuthorityIssuer issuer,
        int operation) =>
        operation switch
        {
            0 => issuer.IssueStoppedHostInstallationResetPlanReadAuthority(),
            1 => issuer.IssueStoppedHostInstallationResetWorkspaceResolutionAuthority(),
            2 => issuer.IssueStoppedHostInstallationResetIdentityReadAuthority(),
            3 => issuer.IssueStoppedHostInstallationResetHostToolsEvidenceReadAuthority(),
            4 => issuer.IssueStoppedHostInstallationResetApplyAuthority(),
            5 => issuer.IssueStoppedHostMarkerPairResetAuthority(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static Task<Result<IStoppedHostGrimoireConnectionLease>> Open(
        IStoppedHostGrimoireConnectionFactory factory,
        int operation,
        IStoppedHostGrimoireConnectionAuthority authority,
        CancellationToken cancellationToken) =>
        operation switch
        {
            0 => factory.OpenStoppedHostInstallationResetPlanReadAsync(
                authority,
                cancellationToken),
            1 => factory.OpenStoppedHostInstallationResetWorkspaceResolutionAsync(
                authority,
                cancellationToken),
            2 => factory.OpenStoppedHostInstallationResetIdentityReadAsync(
                authority,
                cancellationToken),
            3 => factory.OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync(
                authority,
                cancellationToken),
            4 => factory.OpenStoppedHostInstallationResetApplyAsync(
                authority,
                cancellationToken),
            5 => factory.OpenStoppedHostMarkerPairResetAsync(
                authority,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private sealed class ForeignAuthority : IStoppedHostGrimoireConnectionAuthority
    {

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

    private sealed class FixedPassphrase : IGrimoireDbPassphraseSource
    {

        public string Passphrase => "test-passphrase";

        public void SetPassphrase(string passphrase) => throw new NotSupportedException();

    }

    private sealed class RecordingNativeRuntime(bool initializeProvider = false)
        : ISqliteNativeRuntime
    {

        internal Action? AfterInitialize { get; init; }

        internal Exception? Failure { get; init; }

        internal int InitializeCount { get; private set; }

        public void Initialize()
        {

            InitializeCount++;

            if (Failure is not null)
            {

                throw Failure;

            }

            if (initializeProvider)
            {

                SqliteNativeRuntime.Instance.Initialize();

            }

            AfterInitialize?.Invoke();

        }

    }

    private sealed class RecordingInitializer : ICovenantSqliteConnectionInitializer
    {

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

    private sealed class RecordingStoppedHostFactoryTestSeam
        : IStoppedHostGrimoireConnectionFactoryTestSeam
    {

        internal Action? OnAfterProviderConstruction { get; init; }

        internal int NativeOpenCount { get; private set; }

        internal int ProviderConstructionCount { get; private set; }

        public void AfterProviderConstruction()
        {

            ProviderConstructionCount++;

            OnAfterProviderConstruction?.Invoke();

        }

        public ValueTask BeforeNativeOpenAsync(CancellationToken cancellationToken)
        {

            NativeOpenCount++;

            return ValueTask.CompletedTask;

        }

    }

}
