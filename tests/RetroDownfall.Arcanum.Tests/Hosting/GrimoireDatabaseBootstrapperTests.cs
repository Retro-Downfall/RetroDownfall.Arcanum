using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
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

    // Issue: the legacy KDF upgrade used to run the irreversible PRAGMA rekey before persisting the
    // salt, so a crash or a failing sidecar write in that window destroyed the only copy of the
    // salt. The salt must be on durable storage first: if it cannot be persisted, the database must
    // still open with its legacy passphrase.
    [Fact]
    public async Task EnsureInitializedAsync_LegacyUpgrade_DoesNotRekeyWhenTheSaltCannotBePersisted()
    {

        _secretStore.SetApiKey("legacy-api-key");

        string dedicatedSecret = "dedicated-legacy-secret";

        _secretStore.SetDedicatedSecret(dedicatedSecret);

        string legacyPassphrase =
            GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecretLegacy(dedicatedSecret);

        await CreateLegacyDatabaseAsync(legacyPassphrase);

        // Occupy the staging path with a directory so persisting the salt fails the way a full disk
        // or a read-only ~/.config/arcanum would.
        Directory.CreateDirectory(GrimoireKdfSidecarFile.GetPendingSidecarPath(_dbPath));

        _ = await Assert.ThrowsAnyAsync<IOException>(() =>
            GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                _secretStore,
                _passphraseSource,
                _scopeFactory,
                _dbPath,
                _tempDir,
                CancellationToken.None));

        Assert.False(File.Exists(_sidecarPath));

        // Pooling must be off: a pooled handle keeps the key it was opened with and would answer
        // SELECT 1 without re-deriving it from the file, hiding a committed rekey.
        await using SqliteConnection probe = new(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Password = legacyPassphrase,
            Pooling = false,
        }.ToString());

        await probe.OpenAsync();

        await using SqliteCommand cmd = probe.CreateCommand();

        cmd.CommandText = "SELECT 1;";

        _ = await cmd.ExecuteScalarAsync();

    }

    // The salt is staged before the rekey and promoted after it, so a completed upgrade leaves the
    // committed sidecar and no staging file behind.
    [Fact]
    public async Task EnsureInitializedAsync_LegacyUpgrade_PromotesTheStagedSaltAndLeavesNoPendingFile()
    {

        _secretStore.SetApiKey("legacy-api-key");

        string dedicatedSecret = "dedicated-legacy-secret";

        _secretStore.SetDedicatedSecret(dedicatedSecret);

        await CreateLegacyDatabaseAsync(
            GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecretLegacy(dedicatedSecret));

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        Assert.True(File.Exists(_sidecarPath));

        Assert.False(File.Exists(GrimoireKdfSidecarFile.GetPendingSidecarPath(_dbPath)));

        Assert.Equal(
            GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
                _secretStore.DedicatedSecret!,
                GrimoireKdfSidecarFile.Read(_dbPath).GetSaltBytes()),
            _passphraseSource.Passphrase);

    }

    // Crash side A: the rekey committed but the pending sidecar was never promoted. The salt is on
    // disk, so startup must find it, open the database, and promote it.
    [Fact]
    public async Task EnsureInitializedAsync_PendingSidecarAfterCommittedRekey_RecoversAndPromotes()
    {

        _secretStore.SetApiKey("test-api-key");

        string dedicatedSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        _secretStore.SetDedicatedSecret(dedicatedSecret);

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        string pbkdf2Passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
            dedicatedSecret,
            sidecar.GetSaltBytes());

        await CreateLegacyDatabaseAsync(pbkdf2Passphrase);

        // Simulate the interrupted upgrade: the database is already keyed with the new passphrase
        // and only the pending sidecar exists.
        GrimoireKdfSidecarFile.WritePending(_dbPath, sidecar);

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        Assert.Equal(pbkdf2Passphrase, _passphraseSource.Passphrase);

        Assert.True(File.Exists(_sidecarPath));

        Assert.False(File.Exists(GrimoireKdfSidecarFile.GetPendingSidecarPath(_dbPath)));

        Assert.Equal(sidecar.SaltBase64, GrimoireKdfSidecarFile.Read(_dbPath).SaltBase64);

    }

    // Crash side B: the salt was staged but the rekey never committed. The stale pending salt must
    // not block the legacy upgrade from being re-driven.
    [Fact]
    public async Task EnsureInitializedAsync_StalePendingSidecarBeforeRekey_RedrivesLegacyUpgrade()
    {

        _secretStore.SetApiKey("legacy-api-key");

        string dedicatedSecret = "dedicated-legacy-secret";

        _secretStore.SetDedicatedSecret(dedicatedSecret);

        await CreateLegacyDatabaseAsync(
            GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecretLegacy(dedicatedSecret));

        GrimoireKdfSidecar stale = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        GrimoireKdfSidecarFile.WritePending(_dbPath, stale);

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        Assert.True(File.Exists(_sidecarPath));

        Assert.NotEqual(stale.SaltBase64, GrimoireKdfSidecarFile.Read(_dbPath).SaltBase64);

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

    // A sidecar-backed database is never keyed from the master API key, so a missing
    // grimoire-key.dat must name that file instead of falling back and reporting "key verification
    // failed" / possible tampering.
    [Fact]
    public async Task EnsureInitializedAsync_MissingGrimoireSecretWithSidecar_NamesTheMissingKeyFile()
    {

        _secretStore.SetApiKey("test-api-key");

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        // The operator restored arcanum.db and arcanum.db.kdf but not grimoire-key.dat, which lives
        // under a different directory on macOS/Windows.
        TestSecretStore withoutGrimoireKey = new();

        withoutGrimoireKey.SetApiKey("test-api-key");

        GrimoireDatabaseUnavailableException error =
            await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() =>
                GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                    withoutGrimoireKey,
                    new GrimoireDbPassphraseSource(),
                    _scopeFactory,
                    _dbPath,
                    _tempDir,
                    CancellationToken.None));

        Assert.Contains(
            "grimoire-key.dat",
            error.Message,
            StringComparison.Ordinal);

        // The master API key never keys a sidecar-backed database, so it must not be tried and the
        // operator must not be pointed at tampering or a master-key mismatch.
        Assert.DoesNotContain(
            "key verification failed",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

    }

    private async Task CreateLegacyDatabaseAsync(string passphrase)
    {

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Password = passphrase,
        }.ToString());

        await connection.OpenAsync();

        _ = await GrimoireSchemaInstaller.InstallAsync(
            connection,
            embeddingDimensions: 1536,
            logger: null,
            CancellationToken.None);

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

    [Fact]
    public async Task StartAsync_refuses_to_open_the_database_when_the_maintenance_lock_is_unavailable()
    {

        using ArcanumMaintenanceLock? held =
            ArcanumMaintenanceLock.TryAcquire(_tempDir);

        Assert.NotNull(held);

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            _tempDir);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.StartAsync(CancellationToken.None));

        Assert.Contains(
            "maintenance lock",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(_dbPath));

    }

    [Fact]
    public async Task StartAsync_refuses_active_reset_before_acquiring_the_maintenance_lock()
    {

        using ArcanumMaintenanceLock? held =
            ArcanumMaintenanceLock.TryAcquire(_tempDir);

        Assert.NotNull(held);

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            _tempDir,
            new ActiveResetProbe());

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.StartAsync(CancellationToken.None));

        Assert.Contains(
            "factory reset",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            "maintenance lock",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(_dbPath));

    }

    private sealed class ActiveResetProbe : IInstallationStartupProbe
    {

        public Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<ActiveInstallationReset?>.Success(
                new ActiveInstallationReset(
                    InstallationResetScope.Global,
                    WorkspaceRoot: null,
                    PlanId: "active-plan")));

        public Result<bool> IsFreshInstallation() =>
            Result<bool>.Success(false);

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
