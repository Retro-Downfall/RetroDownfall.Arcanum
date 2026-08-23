using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

[Collection("ProcessEnvironment")]
public sealed class GrimoireCliInitializationTests : IDisposable
{

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    private readonly string _testHome =
        Path.Combine(Path.GetTempPath(), "arcanum-tests", $"cli-initialization-{Guid.NewGuid():N}");

    public GrimoireCliInitializationTests()
    {

        Directory.CreateDirectory(_testHome);

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _testHome);

    }

    [Fact]
    public async Task RunExclusiveAsync_serializes_calls_and_holds_the_lock_through_each_callback()
    {

        GatedSecretStore secretStore = new("test-master-key", gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeStartupProbe(),
            ClearClientMutationBoundary());

        TaskCompletionSource firstCallbackEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseFirstCallback = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> first = initialization.RunExclusiveWithBootstrapAsync(
            async (_, token) =>
            {

                using ArcanumMaintenanceLock? competing =
                    ArcanumMaintenanceLock.AcquireDetailed(
                        ArcanumPaths.GrimoireDirectory).Lock;

                Assert.Null(competing);

                firstCallbackEntered.TrySetResult();

                using ArcanumClientMutationLock? competingClient =
                    ArcanumClientMutationLock.AcquireDetailed(
                        ArcanumPaths.GrimoireDirectory).Lock;

                Assert.Null(competingClient);

                await releaseFirstCallback.Task.WaitAsync(token);

                return 17;

            },
            CancellationToken.None);

        await firstCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        int secondCallbackCount = 0;

        Task<int> second = initialization.RunExclusiveWithBootstrapAsync(
            (_, _) =>
            {

                Interlocked.Increment(ref secondCallbackCount);

                return Task.FromResult(23);

            },
            CancellationToken.None);

        Assert.False(second.IsCompleted);

        Assert.Equal(0, Volatile.Read(ref secondCallbackCount));

        releaseFirstCallback.TrySetResult();

        Assert.Equal(17, await first);

        Assert.Equal(23, await second);

        Assert.Equal(2, secretStore.ApiKeyReadCount);

        Assert.Equal(1, secondCallbackCount);

        Assert.False(string.IsNullOrWhiteSpace(passphraseSource.Passphrase));

        Assert.True(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

        Assert.True(File.Exists(ArcanumPaths.GrimoireDatabaseFile + ".kdf"));

        Assert.True(provider.GetRequiredService<IGrimoireDbReadiness>().IsReady);

        using ArcanumMaintenanceLock? released = ArcanumMaintenanceLock.TryAcquire(
            ArcanumPaths.GrimoireDirectory);

        Assert.NotNull(released);

    }

    [Fact]
    public async Task RunExclusiveAsync_without_bootstrap_preserves_a_fresh_installation()
    {

        GatedSecretStore secretStore = new("test-master-key", gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeStartupProbe(),
            ClearClientMutationBoundary());

        int callbackCount = 0;

        int value = await initialization.RunExclusiveAsync(
            (_, _) =>
            {

                Interlocked.Increment(ref callbackCount);

                return Task.FromResult(31);

            },
            CancellationToken.None);

        Assert.Equal(31, value);

        Assert.Equal(1, callbackCount);

        Assert.Equal(0, secretStore.ApiKeyReadCount);

        Assert.False(Directory.Exists(ArcanumPaths.GrimoireDirectory));

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile + ".kdf"));

        Assert.False(provider.GetRequiredService<IGrimoireDbReadiness>().IsReady);

    }

    [Fact]
    public async Task RunExclusiveAsync_retains_the_client_mutation_mutex_for_the_whole_callback()
    {

        await using ServiceProvider provider = CreateServices();

        GrimoireCliInitialization initialization = new(
            new GatedSecretStore("test-master-key", gateApiKeyRead: false),
            new GrimoireDbPassphraseSource(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeStartupProbe(),
            ClearClientMutationBoundary());

        ArcanumClientMutationLockAcquisitionDisposition disposition =
            await initialization.RunExclusiveAsync(
                (_, _) =>
                {

                    ArcanumClientMutationLockAcquisitionResult competing =
                        ArcanumClientMutationLock.AcquireDetailed(
                            ArcanumPaths.GrimoireDirectory);

                    competing.Lock?.Dispose();

                    return Task.FromResult(competing.Disposition);

                },
                CancellationToken.None);

        Assert.Equal(
            ArcanumClientMutationLockAcquisitionDisposition.Contended,
            disposition);

    }

    [Fact]
    public async Task RunExclusiveAsync_after_bootstrap_failure_releases_lock_and_retries()
    {

        GatedSecretStore secretStore = new(apiKey: null, gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeStartupProbe(),
            ClearClientMutationBoundary());

        await Assert.ThrowsAsync<MissingMasterApiKeyException>(
            () => initialization.RunExclusiveWithBootstrapAsync(
                static (_, _) => Task.FromResult(0),
                CancellationToken.None));

        await Assert.ThrowsAsync<MissingMasterApiKeyException>(
            () => initialization.RunExclusiveWithBootstrapAsync(
                static (_, _) => Task.FromResult(0),
                CancellationToken.None));

        Assert.Equal(2, secretStore.ApiKeyReadCount);

        Assert.False(provider.GetRequiredService<IGrimoireDbReadiness>().IsReady);

        using ArcanumMaintenanceLock? released = ArcanumMaintenanceLock.TryAcquire(
            ArcanumPaths.GrimoireDirectory);

        Assert.NotNull(released);

    }

    [Fact]
    public async Task Standalone_lock_owning_cli_adopts_current_erasure_and_freezes_the_gate()
    {

        GatedSecretStore secretStore = new("test-master-key", gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            secretStore,
            passphraseSource,
            scopeFactory,
            CancellationToken.None);

        CovenantExclusiveRecoveryOwner owner = await SeedCurrentErasureAsync(passphraseSource);

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            scopeFactory,
            new FakeStartupProbe(),
            ClearClientMutationBoundary());

        _ = await initialization.RunExclusiveWithBootstrapAsync(
            static (_, _) => Task.FromResult(0),
            CancellationToken.None);

        CovenantOperationGate gate = provider.GetRequiredService<CovenantOperationGate>();

        Result<CovenantExclusiveLease> resumed = await gate.ResumeExclusiveAsync(
            owner,
            CancellationToken.None);

        Assert.True(
            resumed.IsSuccess
                || !string.Equals(
                    resumed.Error.Code,
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    StringComparison.Ordinal));

        if (resumed.IsSuccess)
        {

            await resumed.Value.DisposeAsync();

        }

        Assert.Throws<InvalidOperationException>(() => gate.AdoptDurableRecoveryOwner(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            scope: null,
            cleanupOnlyHistoricalCampaign: false));

    }

    [Theory]
    [InlineData("running host")]
    [InlineData("backup restore")]
    [InlineData("installation reset")]
    public async Task Contended_cli_operation_fails_before_bootstrap_or_callback(
        string cooperativeOwner)
    {

        GatedSecretStore secretStore = new("test-master-key", gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(
            ArcanumPaths.GrimoireDirectory);

        Assert.NotNull(held);

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeStartupProbe(),
            ClearClientMutationBoundary());

        int callbackCount = 0;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => initialization.RunExclusiveAsync(
                (_, _) =>
                {

                    Interlocked.Increment(ref callbackCount);

                    return Task.FromResult(0);

                },
                CancellationToken.None));

        Assert.Contains(cooperativeOwner, exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, callbackCount);

        Assert.Equal(0, secretStore.ApiKeyReadCount);

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

    }

    [Fact]
    public async Task Unsafe_lock_topology_fails_before_cli_bootstrap_mutates_the_installation()
    {

        GatedSecretStore secretStore = new("test-master-key", gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        string sentinel = Path.Combine(_testHome, "unsafe-lock-sentinel.txt");

        byte[] original = "lock-target-must-not-change"u8.ToArray();

        File.WriteAllBytes(sentinel, original);

        string lockPath = ArcanumMaintenanceLock.LockPathFor(
            ArcanumPaths.GrimoireDirectory);

        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        File.CreateSymbolicLink(lockPath, sentinel);

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeStartupProbe(),
            ClearClientMutationBoundary());

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initialization.RunExclusiveWithBootstrapAsync(
                static (_, _) => Task.FromResult(0),
                CancellationToken.None));

        Assert.Equal(0, secretStore.ApiKeyReadCount);

        Assert.Equal(original, File.ReadAllBytes(sentinel));

        Assert.False(Directory.Exists(ArcanumPaths.GrimoireDirectory));

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

    }

    [Fact]
    public async Task Direct_root_symlink_fails_before_cli_bootstrap_writes_through_it()
    {

        GatedSecretStore secretStore = new("test-master-key", gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        string target = Path.Combine(_testHome, "redirect-target");

        Directory.CreateDirectory(target);

        Directory.CreateDirectory(Path.GetDirectoryName(ArcanumPaths.GrimoireDirectory)!);

        File.CreateSymbolicLink(ArcanumPaths.GrimoireDirectory, target);

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeStartupProbe(),
            ClearClientMutationBoundary());

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initialization.RunExclusiveWithBootstrapAsync(
                static (_, _) => Task.FromResult(0),
                CancellationToken.None));

        Assert.Equal(0, secretStore.ApiKeyReadCount);

        Assert.Empty(Directory.GetFileSystemEntries(target));

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

    }

    [Fact]
    public async Task Acquired_cli_revalidates_root_after_restore_recovery_before_any_writer()
    {

        GatedSecretStore secretStore = new("test-master-key", gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        string target = Path.Combine(_testHome, "post-recovery-target");

        Directory.CreateDirectory(target);

        IServiceScopeFactory inner = provider.GetRequiredService<IServiceScopeFactory>();

        MutatingScopeFactory scopeFactory = new(
            inner,
            () => File.CreateSymbolicLink(ArcanumPaths.GrimoireDirectory, target));

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            scopeFactory,
            new FakeStartupProbe(),
            ClearClientMutationBoundary());

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initialization.RunExclusiveWithBootstrapAsync(
                static (_, _) => Task.FromResult(0),
                CancellationToken.None));

        Assert.Equal(0, secretStore.ApiKeyReadCount);

        Assert.Empty(Directory.GetFileSystemEntries(target));

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

    }

    [Fact]
    public async Task Active_installation_reset_fails_before_cli_bootstrap_or_callback()
    {

        GatedSecretStore secretStore = new("test-master-key", gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        FakeStartupProbe startupProbe = new(
            new ActiveInstallationReset(
                InstallationResetScope.All,
                WorkspaceRoot: null,
                PlanId: "active-reset"));

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>(),
            startupProbe,
            ClearClientMutationBoundary());

        int callbackCount = 0;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => initialization.RunExclusiveAsync(
                (_, _) =>
                {

                    Interlocked.Increment(ref callbackCount);

                    return Task.FromResult(0);

                },
                CancellationToken.None));

        Assert.Contains("installation factory reset", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, startupProbe.ReadCount);

        Assert.Equal(0, callbackCount);

        Assert.Equal(0, secretStore.ApiKeyReadCount);

        Assert.False(Directory.Exists(ArcanumPaths.GrimoireDirectory));

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

    }

    [Fact]
    public async Task Indeterminate_installation_reset_state_fails_before_cli_bootstrap_or_callback()
    {

        GatedSecretStore secretStore = new("test-master-key", gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        FakeStartupProbe startupProbe = new(
            probeError: new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "Reset evidence is unavailable."));

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>(),
            startupProbe,
            ClearClientMutationBoundary());

        int callbackCount = 0;

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => initialization.RunExclusiveAsync(
                (_, _) =>
                {

                    Interlocked.Increment(ref callbackCount);

                    return Task.FromResult(0);

                },
                CancellationToken.None));

        Assert.Equal(1, startupProbe.ReadCount);

        Assert.Equal(0, callbackCount);

        Assert.Equal(0, secretStore.ApiKeyReadCount);

        Assert.False(Directory.Exists(ArcanumPaths.GrimoireDirectory));

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Callback_failure_or_cancellation_releases_the_exact_lock(bool cancellation)
    {

        GatedSecretStore secretStore = new("test-master-key", gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeStartupProbe(),
            ClearClientMutationBoundary());

        Func<IServiceProvider, CancellationToken, Task<int>> operation = cancellation
            ? static (_, _) => Task.FromCanceled<int>(new CancellationToken(canceled: true))
            : static (_, _) => Task.FromException<int>(new TestOperationException());

        if (cancellation)
        {

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                initialization.RunExclusiveAsync(operation, CancellationToken.None));

        }
        else
        {

            _ = await Assert.ThrowsAsync<TestOperationException>(() =>
                initialization.RunExclusiveAsync(operation, CancellationToken.None));

        }

        using ArcanumMaintenanceLock? released = ArcanumMaintenanceLock.TryAcquire(
            ArcanumPaths.GrimoireDirectory);

        Assert.NotNull(released);

    }

    public void Dispose()
    {

        SqliteConnection.ClearAllPools();

        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {

            global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

        }

        try
        {

            if (Directory.Exists(_testHome))
            {

                Directory.Delete(_testHome, recursive: true);

            }

        }
        catch
        {

            // Best-effort cleanup for the uniquely owned test root.

        }

    }

    private static ServiceProvider CreateServices()
    {

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireDbReadiness, GrimoireDbReadiness>();

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        // The bootstrap resolves the whole schema-installation graph out of this container, and
        // WeaveIndexAvailability comes with it.
        _ = services.AddGrimoireSchemaInstallation();

        services.AddLogging();

        return services.BuildServiceProvider();

    }

    private static IArcanumClientMutationBoundary ClearClientMutationBoundary() =>
        new ArcanumClientMutationBoundary(
            ArcanumPaths.GrimoireDirectory,
            new ClearClientMutationEvidenceProbe());

    private static async Task<CovenantExclusiveRecoveryOwner> SeedCurrentErasureAsync(
        IGrimoireDbPassphraseSource passphraseSource)
    {

        Guid operationId = Guid.NewGuid();

        CovenantDigest digest = CovenantOperationGateFixture.Digest(17);

        CovenantExclusiveRecoveryOwner owner = new(
            operationId,
            CovenantExclusiveOperation.CovenantReset,
            digest);

        byte[] payload = CovenantRecoveryCheckpointCodec.Encode(
            new DataRetentionMutationCheckpointV3(
                DataRetentionMutationCheckpointV3.CurrentVersion,
                "reset-memory",
                ((int)MemoryResetScope.Covenant).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                new CovenantResetEffectArmV1(
                    operationId,
                    CovenantRecoveryCheckpointCodec.EncodeEffectDigest(digest),
                    owner.Operation,
                    CovenantResetPhase.InventoryPrepared)));

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ArcanumPaths.GrimoireDatabaseFile,
            Password = passphraseSource.Passphrase,
            Pooling = false,
        }.ToString();

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            connectionString,
            CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO "LongRunningOperations" (
                "Id", "Kind", "State", "RecoveryPolicy", "CreatedAt", "LeaseOwner",
                "AttemptCount", "CheckpointVersion", "CheckpointPayload", "CheckpointReference",
                "PublicSummary", "Revision")
            VALUES (@id, @kind, @state, @policy, @created, @owner, 1, @version, @payload,
                @reference, 'Interrupted Covenant erasure.', 1);
            """;

        _ = command.Parameters.AddWithValue("@id", operationId.ToString("N"));

        _ = command.Parameters.AddWithValue("@kind", LongRunningOperationKinds.DataRetentionMutation);

        _ = command.Parameters.AddWithValue("@state", (int)LongRunningOperationState.Running);

        _ = command.Parameters.AddWithValue(
            "@policy",
            (int)LongRunningOperationRecoveryPolicy.ReconcileAndComplete);

        _ = command.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O"));

        _ = command.Parameters.AddWithValue("@owner", "interrupted-owner");

        _ = command.Parameters.AddWithValue(
            "@version",
            DataRetentionMutationCheckpointV3.CurrentVersion);

        _ = command.Parameters.AddWithValue("@payload", payload);

        _ = command.Parameters.AddWithValue(
            "@reference",
            CovenantResetCheckpointInitiator.CheckpointReference(
                LongRunningOperationKinds.DataRetentionMutation,
                operationId));

        _ = await command.ExecuteNonQueryAsync();

        return owner;

    }

    private void SetEnvironment(string name, string value)
    {

        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);

    }

    private sealed class GatedSecretStore(string? apiKey, bool gateApiKeyRead) : ISecretStore
    {

        private readonly TaskCompletionSource _apiKeyReadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _releaseApiKeyRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private string? _encryptionSecret;

        private int _apiKeyReadCount;

        public Task ApiKeyReadStarted => _apiKeyReadStarted.Task;

        public int ApiKeyReadCount => Volatile.Read(ref _apiKeyReadCount);

        public async Task<string?> GetApiKeyAsync()
        {

            Interlocked.Increment(ref _apiKeyReadCount);

            _apiKeyReadStarted.TrySetResult();

            if (gateApiKeyRead)
            {

                await _releaseApiKeyRead.Task.ConfigureAwait(false);

            }

            return apiKey;

        }

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(
                string.IsNullOrWhiteSpace(apiKey)
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string savedApiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult(_encryptionSecret);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
        {

            _encryptionSecret = encryptionSecret;

            return Task.CompletedTask;

        }

        public void ReleaseApiKeyRead() => _releaseApiKeyRead.TrySetResult();

    }

    private sealed class FakeStartupProbe(
        ActiveInstallationReset? activeReset = null,
        Error? probeError = null) : IInstallationStartupProbe
    {

        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
            CancellationToken cancellationToken = default)
        {

            Interlocked.Increment(ref _readCount);

            return Task.FromResult(
                probeError is null
                    ? Result<ActiveInstallationReset?>.Success(activeReset)
                    : Result<ActiveInstallationReset?>.Failure(probeError.Value));

        }

        public Result<bool> IsFreshInstallation() => Result<bool>.Success(false);

    }

    private sealed class ClearClientMutationEvidenceProbe :
        IClientMutationEvidenceProbe
    {

        public Task<ClientMutationEvidenceResult> InspectAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(ClientMutationEvidenceResult.Clear());

        }

    }

    private sealed class TestOperationException : Exception;

    private sealed class MutatingScopeFactory(
        IServiceScopeFactory inner,
        Action mutateOnce) : IServiceScopeFactory
    {

        private int _mutated;

        public IServiceScope CreateScope()
        {

            IServiceScope scope = inner.CreateScope();

            return Interlocked.Exchange(ref _mutated, 1) == 0
                ? new MutatingScope(scope, mutateOnce)
                : scope;

        }

        private sealed class MutatingScope(
            IServiceScope innerScope,
            Action mutate) : IServiceScope, IAsyncDisposable
        {

            private int _disposed;

            public IServiceProvider ServiceProvider => innerScope.ServiceProvider;

            public void Dispose()
            {

                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {

                    return;

                }

                innerScope.Dispose();

                mutate();

            }

            public async ValueTask DisposeAsync()
            {

                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {

                    return;

                }

                if (innerScope is IAsyncDisposable asyncDisposable)
                {

                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);

                }
                else
                {

                    innerScope.Dispose();

                }

                mutate();

            }

        }

    }

}
