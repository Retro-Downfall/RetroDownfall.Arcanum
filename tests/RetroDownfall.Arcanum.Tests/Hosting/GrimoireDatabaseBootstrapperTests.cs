using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class GrimoireDatabaseBootstrapperTests : IDisposable
{

    private readonly string _tempDir;

    private readonly string _dbPath;

    private readonly string _sidecarPath;

    private readonly TestSecretStore _secretStore;

    private readonly GrimoireDbPassphraseSource _passphraseSource;

    private readonly IServiceScopeFactory _scopeFactory;

    public GrimoireDatabaseBootstrapperTests()
    {

        Batteries_V2.Init();

        _tempDir = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"bootstrapper-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_tempDir);

        _dbPath = Path.Combine(_tempDir, "grimoire.db");

        _sidecarPath = _dbPath + ".kdf";

        _secretStore = new TestSecretStore();

        _passphraseSource = new GrimoireDbPassphraseSource();

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireDbReadiness, GrimoireDbReadiness>();

        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    }

    [Fact]
    public async Task EnsureInitializedAsync_missing_api_key_throws_MissingMasterApiKeyException()
    {

        // No API key set on the secret store -> GetApiKeyAsync returns null/whitespace,
        // which must surface as a recoverable MissingMasterApiKeyException (not Environment.FailFast).

        MissingMasterApiKeyException ex = await Assert.ThrowsAsync<MissingMasterApiKeyException>(() =>
            GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                _secretStore,
                _passphraseSource,
                _scopeFactory,
                _dbPath,
                _tempDir,
                CancellationToken.None));

        Assert.Equal(MissingMasterApiKeyException.MessageText, ex.Message);

    }

    [Fact]
    public async Task EnsureInitializedAsync_NewDatabase_CreatesSidecarAndUsesPbkdf2()
    {

        _secretStore.SetApiKey("test-api-key");

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        Assert.True(File.Exists(_dbPath));

        Assert.True(File.Exists(_sidecarPath));

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecarFile.Read(_dbPath);

        Assert.Equal(GrimoireKeyDerivation.KdfVersion2, sidecar.Version);

        Assert.NotNull(_secretStore.DedicatedSecret);

    }

    [Fact]
    public async Task EnsureInitializedAsync_LegacyApiKeyDatabase_UpgradesToPbkdf2()
    {

        _secretStore.SetApiKey("legacy-api-key");

        string legacyPassphrase = GrimoireKeyDerivation.DerivePassphraseFromApiKeyLegacy("legacy-api-key");

        await CreateLegacyDatabaseAsync(legacyPassphrase);

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        Assert.True(File.Exists(_sidecarPath));

        Assert.NotNull(_secretStore.DedicatedSecret);

        await using SqliteConnection probe = new(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Password = _passphraseSource.Passphrase,
        }.ToString());

        await probe.OpenAsync();

        await using SqliteCommand cmd = probe.CreateCommand();

        cmd.CommandText = "SELECT 1;";

        _ = await cmd.ExecuteScalarAsync();

    }

    [Fact]
    public async Task EnsureInitializedAsync_LegacyDedicatedSecretDatabase_UpgradesToPbkdf2()
    {

        _secretStore.SetApiKey("legacy-api-key");

        string dedicatedSecret = "dedicated-legacy-secret";

        _secretStore.SetDedicatedSecret(dedicatedSecret);

        string legacyPassphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecretLegacy(dedicatedSecret);

        await CreateLegacyDatabaseAsync(legacyPassphrase);

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        Assert.True(File.Exists(_sidecarPath));

        await using SqliteConnection probe = new(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Password = _passphraseSource.Passphrase,
        }.ToString());

        await probe.OpenAsync();

        await using SqliteCommand cmd = probe.CreateCommand();

        cmd.CommandText = "SELECT 1;";

        _ = await cmd.ExecuteScalarAsync();

    }

    [Fact]
    public async Task EnsureInitializedAsync_CorruptedDedicatedSecret_FailsClosedWithoutTerminatingProcess()
    {

        _secretStore.SetApiKey("test-api-key");

        _secretStore.SetGrimoireReadResult(
            SecretStoreReadResult.Corrupted("missing test key"));

        GrimoireKdfSidecarFile.Write(
            _dbPath,
            GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2));

        GrimoireDatabaseUnavailableException error =
            await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() =>
                GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                    _secretStore,
                    _passphraseSource,
                    _scopeFactory,
                    _dbPath,
                    _tempDir,
                    CancellationToken.None));

        Assert.Contains(
            "cannot be decrypted",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            "missing test key",
            error.Message,
            StringComparison.Ordinal);

    }

    private async Task CreateLegacyDatabaseAsync(string passphrase)
    {

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Password = passphrase,
        }.ToString());

        await connection.OpenAsync();

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        await connection.CloseAsync();

    }

    // W3.4 Group D #9: graceful shutdown must run PRAGMA wal_checkpoint(TRUNCATE) so the
    // -wal sidecar file does not persist across restarts. The hosted service's StopAsync
    // wires this to ArcanumPaths.GrimoireDatabaseFile; the truncation behavior is verified
    // via the internal overload against the test's bootstrapped DB (StopAsync uses the real
    // on-disk path, which is not the test's temp DB).
    [Fact]
    public async Task CheckpointOnShutdownAsync_truncates_populated_wal_when_no_readers_hold_it()
    {

        _secretStore.SetApiKey("test-api-key");

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        await using SqliteConnection walOwner = new(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Password = _passphraseSource.Passphrase,
            Pooling = false,
        }.ToString());

        await walOwner.OpenAsync();

        await using (SqliteCommand populateWal = walOwner.CreateCommand())
        {

            populateWal.CommandText =
                """
                PRAGMA wal_autocheckpoint = 0;
                CREATE TABLE ShutdownCheckpointProbe (
                    Id INTEGER PRIMARY KEY,
                    Value TEXT NOT NULL
                );
                INSERT INTO ShutdownCheckpointProbe (Value) VALUES ('pending-checkpoint');
                """;

            await populateWal.ExecuteNonQueryAsync();

        }

        string walPath = _dbPath + "-wal";

        long beforeSize = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;

        // Keep an idle connection open so SQLite cannot remove the WAL as the final connection
        // closes. There is no active reader or transaction, so the shutdown TRUNCATE checkpoint
        // can still acquire the database locks it needs.
        await GrimoireDatabaseBootstrapper.CheckpointOnShutdownAsync(
            _passphraseSource,
            _dbPath,
            CancellationToken.None);

        long afterSize = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;

        Assert.True(beforeSize > 0, "WAL was not populated before the checkpoint; the test would be trivial.");

        Assert.True(afterSize == 0, $"WAL was not truncated by the checkpoint: before={beforeSize}, after={afterSize}.");

    }

    // W3.4 Group D #9: the hosted service's StopAsync is the real shutdown entry point and
    // must invoke the checkpoint best-effort (never throws, even on a missing/stray DB or an
    // uninitialized passphrase).
    [Fact]
    public async Task StopAsync_does_not_throw_on_missing_database()
    {

        GrimoireDatabaseHostedService svc = new(_scopeFactory, _secretStore, new GrimoireDbPassphraseSource());

        await svc.StopAsync(CancellationToken.None);

    }

    public void Dispose()
    {

        try
        {

            if (Directory.Exists(_tempDir))
            {

                Directory.Delete(_tempDir, recursive: true);

            }

        }
        catch
        {

            // Best-effort cleanup.

        }

    }

    private sealed class GrimoireDbReadiness : IGrimoireDbReadiness
    {

        public bool IsReady { get; private set; }

        public void MarkReady() => IsReady = true;

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) =>
            IsReady ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);

        public void MarkFailed(Exception exception)
        {
        }

    }

    private sealed class TestSecretStore : ISecretStore
    {

        public string? ApiKey { get; private set; }

        public string? DedicatedSecret { get; private set; }

        private SecretStoreReadResult? _grimoireReadResult;

        public void SetApiKey(string apiKey) => ApiKey = apiKey;

        public void SetDedicatedSecret(string secret) => DedicatedSecret = secret;

        public void SetGrimoireReadResult(SecretStoreReadResult result) =>
            _grimoireReadResult = result;

        public Task<string?> GetApiKeyAsync() => Task.FromResult(ApiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(ApiKey is null ? SecretStoreReadResult.Missing() : SecretStoreReadResult.Ok(ApiKey));

        public Task SaveApiKeyAsync(string apiKey)
        {

            ApiKey = apiKey;

            return Task.CompletedTask;

        }

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult(DedicatedSecret);

        public Task<SecretStoreReadResult> GetGrimoireEncryptionSecretReadResultAsync() =>
            Task.FromResult(
                _grimoireReadResult
                ?? (DedicatedSecret is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(DedicatedSecret)));

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
        {

            DedicatedSecret = encryptionSecret;

            return Task.CompletedTask;

        }

    }

}
