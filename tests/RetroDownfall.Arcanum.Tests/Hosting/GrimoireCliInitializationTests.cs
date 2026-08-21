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
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Backup;
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
    public async Task EnsureInitializedAsync_concurrent_and_repeated_calls_bootstrap_once()
    {

        GatedSecretStore secretStore = new("test-master-key", gateApiKeyRead: true);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>());

        Task first = initialization.EnsureInitializedAsync(CancellationToken.None);

        await secretStore.ApiKeyReadStarted.WaitAsync(TimeSpan.FromSeconds(10));

        Task concurrent = initialization.EnsureInitializedAsync(CancellationToken.None);

        Assert.False(concurrent.IsCompleted);

        secretStore.ReleaseApiKeyRead();

        await Task.WhenAll(first, concurrent);

        await initialization.EnsureInitializedAsync(CancellationToken.None);

        Assert.Equal(1, secretStore.ApiKeyReadCount);

        Assert.False(string.IsNullOrWhiteSpace(passphraseSource.Passphrase));

        Assert.True(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

        Assert.True(File.Exists(ArcanumPaths.GrimoireDatabaseFile + ".kdf"));

        Assert.True(provider.GetRequiredService<IGrimoireDbReadiness>().IsReady);

    }

    [Fact]
    public async Task EnsureInitializedAsync_after_failure_releases_mutex_and_retries()
    {

        GatedSecretStore secretStore = new(apiKey: null, gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>());

        await Assert.ThrowsAsync<MissingMasterApiKeyException>(
            () => initialization.EnsureInitializedAsync(CancellationToken.None));

        await Assert.ThrowsAsync<MissingMasterApiKeyException>(
            () => initialization.EnsureInitializedAsync(CancellationToken.None));

        Assert.Equal(2, secretStore.ApiKeyReadCount);

        Assert.False(provider.GetRequiredService<IGrimoireDbReadiness>().IsReady);

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

        GrimoireCliInitialization initialization = new(secretStore, passphraseSource, scopeFactory);

        await initialization.EnsureInitializedAsync(CancellationToken.None);

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

    [Fact]
    public async Task Coexisting_no_lock_cli_neither_scans_adopts_nor_freezes_the_gate()
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

        using ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(
            ArcanumPaths.GrimoireDirectory);

        Assert.NotNull(held);

        GrimoireCliInitialization initialization = new(secretStore, passphraseSource, scopeFactory);

        await initialization.EnsureInitializedAsync(CancellationToken.None);

        CovenantOperationGate gate = provider.GetRequiredService<CovenantOperationGate>();

        Result<CovenantExclusiveLease> absent = await gate.ResumeExclusiveAsync(
            owner,
            CancellationToken.None);

        Assert.True(absent.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, absent.Error.Code);

        gate.AdoptDurableRecoveryOwner(
            owner,
            scope: null,
            cleanupOnlyHistoricalCampaign: false);

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

}
