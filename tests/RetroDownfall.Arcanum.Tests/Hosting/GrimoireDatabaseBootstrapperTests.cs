using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Security;
using SQLitePCL;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class GrimoireDatabaseBootstrapperTests : IDisposable
{

    private readonly string _tempDir;

    private readonly string _dbPath;

    private readonly string _sidecarPath;

    private readonly TestSecretStore _secretStore;

    private readonly GrimoireDbPassphraseSource _passphraseSource;

    private readonly IServiceScopeFactory _scopeFactory;

    private readonly InMemoryOsCredentialStore _credentialStore = new();

    public GrimoireDatabaseBootstrapperTests()
    {

        SqliteNativeRuntime.Instance.Initialize();

        _tempDir = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"bootstrapper-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_tempDir);

        _dbPath = Path.Combine(_tempDir, "grimoire.db");

        _sidecarPath = _dbPath + ".kdf";

        _secretStore = new TestSecretStore();

        _passphraseSource = new GrimoireDbPassphraseSource();

        _scopeFactory = CreateScopeFactory(_credentialStore);

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

    /// <summary>
    /// Bootstrap publishes the canonical facts it just installed, not only which tiers installed.
    /// </summary>
    /// <remarks>
    /// <c>PublishSchema</c> reports tier health; the dataset generation, the sequences and the
    /// accelerator's applied tuple live in <c>covenant_state</c> and nothing read them, so the
    /// snapshot kept its bootstrap default of a null <c>DatasetGeneration</c> for the process
    /// lifetime. <c>CovenantOperationGate.CaptureFacts</c> refuses every <c>requireCanonical: true</c>
    /// acquisition on exactly that null — and <c>AcquireOrdinary</c>, the lease every Covenant turn
    /// takes, always requires it. So the whole ordinary path failed closed the instant the feature
    /// flag was enabled, and every staleness guard downstream compared values that never moved.
    /// </remarks>
    [Fact]
    public async Task EnsureInitializedAsync_PublishesTheCanonicalStateItInstalled()
    {

        _secretStore.SetApiKey("test-api-key");

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        CovenantAvailabilitySnapshot snapshot = scope.ServiceProvider
            .GetRequiredService<CovenantAvailability>()
            .Current;

        Assert.Equal(CovenantCapabilityState.Healthy, snapshot.Canonical);

        // The one value whose absence closes the ordinary lease gate.
        Assert.NotNull(snapshot.DatasetGeneration);

        Assert.NotEqual(Guid.Empty, snapshot.DatasetGeneration!.Value);

        // A never-built accelerator is behind by definition, so a fresh install owes a rebuild.
        Assert.True(snapshot.RebuildRequired);

        Assert.Null(snapshot.AppliedDatasetGeneration);

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

    /// <summary>
    /// A damaged KDF sidecar fails closed as a Grimoire-unavailable error naming the file to restore.
    /// </summary>
    /// <remarks>
    /// The sidecar is the only copy of the salt, and truncation or byte damage is what it actually
    /// suffers. <c>GrimoireKdfSidecarFile.Read</c> normalizes that into <c>InvalidDataException</c>,
    /// but the bootstrap call site let it escape raw — and <c>CliFailureMapper</c>'s default arm
    /// prints "An unexpected CLI error occurred." for anything it does not recognise, so the
    /// operator was told nothing about <c>arcanum.db.kdf</c> on the one startup path that cannot
    /// continue without it. The sibling secret-store failure already fails closed this way.
    /// </remarks>
    [Fact]
    public async Task EnsureInitializedAsync_UnreadableKdfSidecar_FailsClosedNamingTheSidecar()
    {

        _secretStore.SetApiKey("test-api-key");

        _secretStore.SetDedicatedSecret(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        // A torn write: the file exists, so the sidecar branch is taken, but nothing parses.
        await File.WriteAllTextAsync(_sidecarPath, "{\"version\":2,\"salt\":\"not-base64!!\"");

        GrimoireDatabaseUnavailableException error =
            await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() =>
                GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                    _secretStore,
                    _passphraseSource,
                    _scopeFactory,
                    _dbPath,
                    _tempDir,
                    CancellationToken.None));

        Assert.Contains("arcanum.db.kdf", error.Message, StringComparison.Ordinal);

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

        _ = await FilesystemRefusal.ThrowsAsync(() =>
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

        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            embeddingDimensions: 1536,
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

    [Theory]
    [InlineData(LockedStartupTopology.DirectRootSymlink)]
    [InlineData(LockedStartupTopology.AncestorSymlink)]
    [InlineData(LockedStartupTopology.NonDirectoryAncestor)]
    [InlineData(LockedStartupTopology.InaccessibleAncestor)]
    public async Task StartAsync_rejects_ambiguous_topology_before_recovery_or_shipping_mutation(
        LockedStartupTopology topology)
    {

        if (topology is LockedStartupTopology.InaccessibleAncestor
            && OperatingSystem.IsWindows())
        {

            return;

        }

        string target = Path.Combine(_tempDir, $"topology-target-{topology}");

        string guardedRoot;

        string? symlink = null;

        string? inaccessible = null;

        UnixFileMode? originalMode = null;

        switch (topology)
        {
            case LockedStartupTopology.DirectRootSymlink:
                Directory.CreateDirectory(target);

                guardedRoot = Path.Combine(_tempDir, "direct-root-link");

                Directory.CreateSymbolicLink(guardedRoot, target);

                symlink = guardedRoot;

                break;

            case LockedStartupTopology.AncestorSymlink:
                Directory.CreateDirectory(target);

                symlink = Path.Combine(_tempDir, "ancestor-link");

                Directory.CreateSymbolicLink(symlink, target);

                guardedRoot = Path.Combine(symlink, "retained-parent", "arcanum");

                break;

            case LockedStartupTopology.NonDirectoryAncestor:
                string obstruction = Path.Combine(_tempDir, "file-obstruction");

                File.WriteAllText(obstruction, "unchanged");

                guardedRoot = Path.Combine(obstruction, "retained-parent", "arcanum");

                break;

            default:
                inaccessible = Path.Combine(_tempDir, "inaccessible-ancestor");

                Directory.CreateDirectory(inaccessible);

                originalMode = File.GetUnixFileMode(inaccessible);

                File.SetUnixFileMode(
                    inaccessible,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);

                guardedRoot = Path.Combine(inaccessible, "retained-parent", "arcanum");

                break;
        }

        int recoveryCalls = 0;

        int shippingMutationCalls = 0;

        InstallationResetMaintenanceLockAccessor accessor = new();

        HostLockSerilogFileSink sink = new(
            guardedRoot,
            Path.Combine(guardedRoot, "logs"),
            retainedFileCountLimit: 3,
            enabled: false);

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            guardedRoot,
            accessor,
            new DelegateStartupRecovery((_, _) =>
            {

                recoveryCalls++;

                return Task.FromResult(Result<InstallationResetStartupRecoveryState>.Success(
                    new InstallationResetStartupRecoveryState(
                        ActiveReset: null,
                        ExpectedInstallationId: null,
                        IsLegacyV1: false)));

            }),
            sink,
            masterKeyBootstrap: _ =>
            {

                shippingMutationCalls++;

                return Task.FromException<string?>(
                    new IOException("Injected post-topology mutation stop."));

            });

        try
        {

            Exception error = await Assert.ThrowsAnyAsync<Exception>(() =>
                service.StartAsync(CancellationToken.None));

            Assert.Contains(
                "validated safely",
                error.Message,
                StringComparison.OrdinalIgnoreCase);

            if (Directory.Exists(target))
            {

                Assert.Empty(Directory.GetFileSystemEntries(target));

            }

            Assert.Equal(0, recoveryCalls);

            Assert.Equal(0, shippingMutationCalls);

            Assert.True(accessor.BorrowHeldLock(guardedRoot).IsFailure);

        }
        finally
        {

            if (inaccessible is not null && originalMode is not null)
            {

                File.SetUnixFileMode(inaccessible, originalMode.Value);

            }

            if (symlink is not null
                && FileHandleIdentityInterop.TryGetPathMetadataNoFollow(symlink, out _))
            {

                Directory.Delete(symlink);

            }

        }

    }

    [Fact]
    public async Task StartAsync_admits_a_genuinely_absent_guarded_root_after_ordinary_lineage()
    {

        string guardedRoot = Path.Combine(
            _tempDir,
            "fresh-lineage",
            "retained-parent",
            "arcanum");

        int recoveryCalls = 0;

        int shippingMutationCalls = 0;

        HostLockSerilogFileSink sink = new(
            guardedRoot,
            Path.Combine(guardedRoot, "logs"),
            retainedFileCountLimit: 3,
            enabled: false);

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            guardedRoot,
            new InstallationResetMaintenanceLockAccessor(),
            new DelegateStartupRecovery((_, _) =>
            {

                recoveryCalls++;

                return Task.FromResult(Result<InstallationResetStartupRecoveryState>.Success(
                    new InstallationResetStartupRecoveryState(
                        ActiveReset: null,
                        ExpectedInstallationId: null,
                        IsLegacyV1: false)));

            }),
            sink,
            masterKeyBootstrap: _ =>
            {

                shippingMutationCalls++;

                return Task.FromException<string?>(
                    new IOException("Injected post-topology mutation stop."));

            });

        _ = await Assert.ThrowsAsync<IOException>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.Equal(1, recoveryCalls);

        Assert.Equal(1, shippingMutationCalls);

        Assert.False(Directory.Exists(guardedRoot));

    }

    [Fact]
    public async Task StartAsync_acquires_the_maintenance_lock_before_classifying_active_reset_evidence()
    {

        InstallationResetMaintenanceLockAccessor accessor = new();

        DelegateStartupRecovery recovery = new((held, _) =>
        {

            held.AssertHeldFor(_tempDir);

            Result<ArcanumMaintenanceLock> borrowed = accessor.BorrowHeldLock(_tempDir);

            Assert.True(borrowed.IsSuccess);

            Assert.Same(held, borrowed.Value);

            return Task.FromResult(Result<InstallationResetStartupRecoveryState>.Success(
                new InstallationResetStartupRecoveryState(
                    new ActiveInstallationReset(
                        InstallationResetScope.Workspace,
                        WorkspaceRoot: "/selected/workspace",
                        PlanId: "active-plan"),
                    ExpectedInstallationId: null,
                    IsLegacyV1: true)));

        });

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            _tempDir,
            accessor,
            recovery);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.StartAsync(CancellationToken.None));

        Assert.Contains(
            "factory reset",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.True(accessor.BorrowHeldLock(_tempDir).IsFailure);

        using ArcanumMaintenanceLock? reacquired =
            ArcanumMaintenanceLock.TryAcquire(_tempDir);

        Assert.NotNull(reacquired);

        Assert.False(File.Exists(_dbPath));

    }

    [Theory]
    [InlineData(StartupRecoveryFailureMode.ReturnedFailure)]
    [InlineData(StartupRecoveryFailureMode.ThrownFailure)]
    [InlineData(StartupRecoveryFailureMode.Cancellation)]
    public async Task StartAsync_detaches_and_disposes_the_host_lock_when_locked_recovery_fails(
        StartupRecoveryFailureMode mode)
    {

        InstallationResetMaintenanceLockAccessor accessor = new();

        DelegateStartupRecovery recovery = new((held, cancellationToken) =>
        {

            Assert.Same(held, accessor.BorrowHeldLock(_tempDir).Value);

            return mode switch
            {
                StartupRecoveryFailureMode.ReturnedFailure =>
                    Task.FromResult(Result<InstallationResetStartupRecoveryState>.Failure(
                        new Error(
                            ErrorCodes.Covenant.ManualRecoveryRequired,
                            "Locked recovery failed."))),
                StartupRecoveryFailureMode.ThrownFailure =>
                    Task.FromException<Result<InstallationResetStartupRecoveryState>>(
                        new IOException("Injected locked-recovery failure.")),
                _ => Task.FromCanceled<Result<InstallationResetStartupRecoveryState>>(
                    new CancellationToken(canceled: true)),
            };

        });

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            _tempDir,
            accessor,
            recovery);

        _ = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.True(accessor.BorrowHeldLock(_tempDir).IsFailure);

        using ArcanumMaintenanceLock? reacquired =
            ArcanumMaintenanceLock.TryAcquire(_tempDir);

        Assert.NotNull(reacquired);

        using IServiceScope scope = _scopeFactory.CreateScope();

        GrimoireDbReadiness readiness =
            scope.ServiceProvider.GetRequiredService<GrimoireDbReadiness>();

        Assert.False(readiness.IsReady);

        Assert.NotNull(readiness.Failure);

    }

    [Fact]
    public async Task StartAsync_detaches_and_disposes_the_host_lock_when_bootstrap_fails()
    {

        InstallationResetMaintenanceLockAccessor accessor = new();

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            _tempDir,
            accessor,
            DelegateStartupRecovery.NoActiveReset());

        _ = await Assert.ThrowsAsync<MissingMasterApiKeyException>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.True(accessor.BorrowHeldLock(_tempDir).IsFailure);

        using ArcanumMaintenanceLock? reacquired =
            ArcanumMaintenanceLock.TryAcquire(_tempDir);

        Assert.NotNull(reacquired);

        using IServiceScope scope = _scopeFactory.CreateScope();

        GrimoireDbReadiness readiness =
            scope.ServiceProvider.GetRequiredService<GrimoireDbReadiness>();

        Assert.False(readiness.IsReady);

        Assert.IsType<MissingMasterApiKeyException>(readiness.Failure);

    }

    [Fact]
    public async Task StartAsync_runs_shipping_mutations_after_locked_admission_under_the_attached_lock()
    {

        string guardedRoot = Path.Combine(_tempDir, "post-topology-root");

        InstallationResetMaintenanceLockAccessor accessor = new();

        bool recoveryCompleted = false;

        DelegateStartupRecovery recovery = new((held, _) =>
        {

            Assert.Same(held, accessor.BorrowHeldLock(guardedRoot).Value);

            Assert.False(Directory.Exists(guardedRoot));

            recoveryCompleted = true;

            return Task.FromResult(Result<InstallationResetStartupRecoveryState>.Success(
                new InstallationResetStartupRecoveryState(
                    ActiveReset: null,
                    ExpectedInstallationId: null,
                    IsLegacyV1: false)));

        });

        HostLockSerilogFileSink sink = new(
            guardedRoot,
            Path.Combine(guardedRoot, "logs"),
            retainedFileCountLimit: 3,
            enabled: false);

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            guardedRoot,
            accessor,
            recovery,
            sink,
            masterKeyBootstrap: cancellationToken =>
            {

                cancellationToken.ThrowIfCancellationRequested();

                Assert.True(recoveryCompleted);

                Assert.True(accessor.BorrowHeldLock(guardedRoot).IsSuccess);

                Assert.False(Directory.Exists(guardedRoot));

                return Task.FromException<string?>(
                    new IOException("Injected post-topology bootstrap failure."));

            });

        bool startupActionInvoked = false;

        bool startupLeaseDisposed = false;

        service.ConfigurePostTopologyStartupAction(() =>
        {

            Assert.True(recoveryCompleted);

            Assert.True(accessor.BorrowHeldLock(guardedRoot).IsSuccess);

            startupActionInvoked = true;

            return new DelegateDisposable(() =>
            {

                Assert.True(accessor.BorrowHeldLock(guardedRoot).IsSuccess);

                startupLeaseDisposed = true;

            });

        });

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.Contains("post-topology", error.Message, StringComparison.Ordinal);

        Assert.True(startupActionInvoked);

        Assert.True(startupLeaseDisposed);

        Assert.True(accessor.BorrowHeldLock(guardedRoot).IsFailure);

        Assert.False(Directory.Exists(guardedRoot));

        using ArcanumMaintenanceLock reacquired = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        Assert.Throws<InvalidOperationException>(() =>
            sink.Activate(reacquired, guardedRoot));

    }

    [Fact]
    public async Task StartAsync_cleans_a_terminal_blocker_suffix_and_retains_the_client_mutex_through_startup()
    {

        string guardedRoot = Path.Combine(_tempDir, "client-coordinated-root");

        StaticClientResetEvidenceProbe reset = new(active: null);

        StaticClientRestoreEvidenceProbe restore = new(active: false);

        ClientMutationBlockerStore blocker = new(guardedRoot);

        InstallationMaintenanceCoordination coordination = new(
            guardedRoot,
            blocker,
            reset,
            restore);

        InstallationMaintenanceCoordinationResult opening = await coordination
            .AcquireInstallationResetAsync(
                InstallationResetScope.All,
                "accepted-plan",
                operationId: null,
                CancellationToken.None);

        await opening.BorrowAcquiredLease().DisposeAsync();

        InstallationResetMaintenanceLockAccessor accessor = new();

        HostLockSerilogFileSink sink = new(
            guardedRoot,
            Path.Combine(guardedRoot, "logs"),
            retainedFileCountLimit: 3,
            enabled: false);

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            guardedRoot,
            accessor,
            DelegateStartupRecovery.NoActiveReset(),
            sink,
            masterKeyBootstrap: async _ =>
            {

                Assert.Null((await blocker.InspectAsync()).Value);

                ArcanumClientMutationLockAcquisitionResult competing =
                    ArcanumClientMutationLock.AcquireDetailed(guardedRoot);

                Assert.Equal(
                    ArcanumClientMutationLockAcquisitionDisposition.Contended,
                    competing.Disposition);

                throw new IOException("Injected coordinated startup failure.");

            },
            startupCoordination: coordination);

        _ = await Assert.ThrowsAsync<IOException>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.Null((await blocker.InspectAsync()).Value);

        using ArcanumClientMutationLock released = Assert.IsType<ArcanumClientMutationLock>(
            ArcanumClientMutationLock.AcquireDetailed(guardedRoot).Lock);

    }

    [Fact]
    public async Task Post_restore_activation_revalidates_a_replaced_root_before_shipping_mutation()
    {

        string guardedRoot = Path.Combine(_tempDir, "post-restore-replaced-root");

        string target = Path.Combine(_tempDir, "post-restore-symlink-target");

        Directory.CreateDirectory(target);

        InstallationResetMaintenanceLockAccessor accessor = new();

        using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        accessor.AttachHostLock(held, guardedRoot);

        int startupActionCalls = 0;

        int masterBootstrapCalls = 0;

        HostLockSerilogFileSink sink = new(
            guardedRoot,
            Path.Combine(guardedRoot, "logs"),
            retainedFileCountLimit: 3,
            enabled: true);

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            guardedRoot,
            accessor,
            DelegateStartupRecovery.NoActiveReset(),
            sink,
            masterKeyBootstrap: _ =>
            {

                masterBootstrapCalls++;

                return Task.FromResult<string?>(null);

            });

        service.ConfigurePostTopologyStartupAction(() =>
        {

            startupActionCalls++;

            return null;

        });

        Directory.CreateSymbolicLink(guardedRoot, target);

        try
        {

            System.Reflection.MethodInfo activation = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
                typeof(GrimoireDatabaseHostedService).GetMethod(
                    "ActivatePostRestoreTopologyAsync",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic));

            Exception? error = await Record.ExceptionAsync(async () =>
            {

                Task invocation = Assert.IsAssignableFrom<Task>(activation.Invoke(
                    service,
                    [held, CancellationToken.None]));

                await invocation;

            });

            Assert.Empty(Directory.GetFileSystemEntries(target));

            Assert.IsType<InvalidOperationException>(error);

            Assert.Equal(0, startupActionCalls);

            Assert.Equal(0, masterBootstrapCalls);

        }
        finally
        {

            service.Dispose();

            accessor.DetachHostLock(held);

            if (FileHandleIdentityInterop.TryGetPathMetadataNoFollow(guardedRoot, out _))
            {

                Directory.Delete(guardedRoot);

            }

        }

    }

    [Fact]
    public async Task No_hook_host_runs_the_configured_post_topology_action_before_root_creation()
    {

        string guardedRoot = Path.Combine(_tempDir, "no-hook-post-topology-action");

        _secretStore.SetApiKey("test-api-key");

        InstallationResetMaintenanceLockAccessor accessor = new();

        int startupActionCalls = 0;

        int startupLeaseDisposals = 0;

        bool rootWasAbsent = false;

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            guardedRoot,
            accessor,
            DelegateStartupRecovery.NoActiveReset());

        service.ConfigurePostTopologyStartupAction(() =>
        {

            startupActionCalls++;

            rootWasAbsent = !Directory.Exists(guardedRoot);

            Assert.True(accessor.BorrowHeldLock(guardedRoot).IsSuccess);

            return new DelegateDisposable(() => startupLeaseDisposals++);

        });

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, startupActionCalls);

        Assert.True(rootWasAbsent);

        Assert.True(Directory.Exists(guardedRoot));

        Assert.Equal(0, startupLeaseDisposals);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, startupLeaseDisposals);

    }

    [Fact]
    public async Task No_hook_host_revalidates_root_after_restore_scope_converges_before_any_mutation()
    {

        string guardedRoot = Path.Combine(_tempDir, "no-hook-post-restore-replaced-root");

        string target = Path.Combine(_tempDir, "no-hook-post-restore-target");

        Directory.CreateDirectory(target);

        _secretStore.SetApiKey("test-api-key");

        InstallationResetMaintenanceLockAccessor accessor = new();

        int startupActionCalls = 0;

        IServiceScopeFactory replacingScopeFactory = new DisposeCallbackScopeFactory(
            _scopeFactory,
            () => Directory.CreateSymbolicLink(guardedRoot, target));

        GrimoireDatabaseHostedService service = new(
            replacingScopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            guardedRoot,
            accessor,
            DelegateStartupRecovery.NoActiveReset());

        service.ConfigurePostTopologyStartupAction(() =>
        {

            startupActionCalls++;

            return null;

        });

        try
        {

            Exception? error = await Record.ExceptionAsync(() =>
                service.StartAsync(CancellationToken.None));

            Assert.Empty(Directory.GetFileSystemEntries(target));

            Assert.IsType<InvalidOperationException>(error);

            Assert.Equal(0, startupActionCalls);

            Assert.True(accessor.BorrowHeldLock(guardedRoot).IsFailure);

        }
        finally
        {

            service.Dispose();

            if (FileHandleIdentityInterop.TryGetPathMetadataNoFollow(guardedRoot, out _))
            {

                Directory.Delete(guardedRoot);

            }

        }

    }

    [Fact]
    public async Task StartAsync_rejected_recovery_invokes_no_configured_shipping_mutation()
    {

        string guardedRoot = Path.Combine(_tempDir, "blocked-shipping-mutation");

        InstallationResetMaintenanceLockAccessor accessor = new();

        int masterBootstrapCalls = 0;

        int startupActionCalls = 0;

        HostLockSerilogFileSink sink = new(
            guardedRoot,
            Path.Combine(guardedRoot, "logs"),
            retainedFileCountLimit: 3,
            enabled: false);

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            guardedRoot,
            accessor,
            new DelegateStartupRecovery((_, _) =>
                Task.FromResult(Result<InstallationResetStartupRecoveryState>.Success(
                    new InstallationResetStartupRecoveryState(
                        new ActiveInstallationReset(
                            InstallationResetScope.Workspace,
                            WorkspaceRoot: "/blocked",
                            PlanId: "blocked-plan"),
                        ExpectedInstallationId: null,
                        IsLegacyV1: true)))),
            sink,
            masterKeyBootstrap: _ =>
            {

                masterBootstrapCalls++;

                return Task.FromResult<string?>(null);

            });

        service.ConfigurePostTopologyStartupAction(() =>
        {

            startupActionCalls++;

            return null;

        });

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.Equal(0, masterBootstrapCalls);

        Assert.Equal(0, startupActionCalls);

        Assert.False(Directory.Exists(guardedRoot));

    }

    [Fact]
    public async Task StartAsync_publishes_a_new_master_key_for_one_post_start_consumption()
    {

        string guardedRoot = Path.Combine(_tempDir, "generated-master-key");

        InstallationResetMaintenanceLockAccessor accessor = new();

        HostLockSerilogFileSink sink = new(
            guardedRoot,
            Path.Combine(guardedRoot, "logs"),
            retainedFileCountLimit: 3,
            enabled: false);

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            guardedRoot,
            accessor,
            DelegateStartupRecovery.NoActiveReset(),
            sink,
            masterKeyBootstrap: _ =>
            {

                _secretStore.SetApiKey("generated-api-key");

                return Task.FromResult<string?>("generated-api-key");

            });

        await service.StartAsync(CancellationToken.None);

        Assert.Equal("generated-api-key", service.TakeGeneratedMasterApiKey());

        Assert.Null(service.TakeGeneratedMasterApiKey());

        await service.StopAsync(CancellationToken.None);

    }

    [Fact]
    public async Task StartAsync_attach_collision_disposes_only_the_new_lock_and_preserves_the_accessor_owner()
    {

        string incumbentRoot = Path.Combine(_tempDir, "incumbent");

        Directory.CreateDirectory(incumbentRoot);

        InstallationResetMaintenanceLockAccessor accessor = new();

        using ArcanumMaintenanceLock? incumbent =
            ArcanumMaintenanceLock.TryAcquire(incumbentRoot);

        Assert.NotNull(incumbent);

        accessor.AttachHostLock(incumbent, incumbentRoot);

        try
        {

            GrimoireDatabaseHostedService service = new(
                _scopeFactory,
                _secretStore,
                new GrimoireDbPassphraseSource(),
                _tempDir,
                accessor,
                DelegateStartupRecovery.NoActiveReset());

            InvalidOperationException error =
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.StartAsync(CancellationToken.None));

            Assert.Contains(
                "different installation maintenance lock",
                error.Message,
                StringComparison.OrdinalIgnoreCase);

            Assert.Same(incumbent, accessor.BorrowHeldLock(incumbentRoot).Value);

            using ArcanumMaintenanceLock? targetReacquired =
                ArcanumMaintenanceLock.TryAcquire(_tempDir);

            Assert.NotNull(targetReacquired);

            using ArcanumMaintenanceLock? incumbentContender =
                ArcanumMaintenanceLock.TryAcquire(incumbentRoot);

            Assert.Null(incumbentContender);

        }
        finally
        {

            accessor.DetachHostLock(incumbent);

        }

    }

    [Fact]
    public async Task A_second_StartAsync_refuses_without_overwriting_the_live_host_lock()
    {

        _secretStore.SetApiKey("test-api-key");

        InstallationResetMaintenanceLockAccessor accessor = new();

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            _tempDir,
            accessor,
            DelegateStartupRecovery.NoActiveReset());

        await service.StartAsync(CancellationToken.None);

        ArcanumMaintenanceLock incumbent = accessor.BorrowHeldLock(_tempDir).Value;

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.StartAsync(CancellationToken.None));

        Assert.Contains("already started", error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Same(incumbent, accessor.BorrowHeldLock(_tempDir).Value);

        using (IServiceScope scope = _scopeFactory.CreateScope())
        {

            Assert.True(scope.ServiceProvider.GetRequiredService<IGrimoireDbReadiness>().IsReady);

        }

        service.Dispose();

        Assert.True(accessor.BorrowHeldLock(_tempDir).IsFailure);

        using ArcanumMaintenanceLock? reacquired =
            ArcanumMaintenanceLock.TryAcquire(_tempDir);

        Assert.NotNull(reacquired);

    }

    [Fact]
    public async Task StopAsync_and_Dispose_detach_before_releasing_the_host_lock_idempotently()
    {

        _secretStore.SetApiKey("test-api-key");

        InstallationResetMaintenanceLockAccessor accessor = new();

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            _tempDir,
            accessor,
            DelegateStartupRecovery.NoActiveReset());

        await service.StartAsync(CancellationToken.None);

        Assert.True(accessor.BorrowHeldLock(_tempDir).IsSuccess);

        await service.StopAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        service.Dispose();

        service.Dispose();

        Assert.True(accessor.BorrowHeldLock(_tempDir).IsFailure);

        using ArcanumMaintenanceLock? reacquired =
            ArcanumMaintenanceLock.TryAcquire(_tempDir);

        Assert.NotNull(reacquired);

    }

    [Fact]
    public async Task StopAsync_checkpoints_once_and_never_after_another_owner_reacquires_the_lock()
    {

        _secretStore.SetApiKey("test-api-key");

        TrackingPassphraseSource passphraseSource = new();

        InstallationResetMaintenanceLockAccessor accessor = new();

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            passphraseSource,
            _tempDir,
            accessor,
            DelegateStartupRecovery.NoActiveReset());

        await service.StartAsync(CancellationToken.None);

        int beforeStop = passphraseSource.ReadCount;

        await service.StopAsync(CancellationToken.None);

        int afterFirstStop = passphraseSource.ReadCount;

        Assert.True(afterFirstStop > beforeStop);

        using ArcanumMaintenanceLock otherOwner = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(_tempDir));

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(afterFirstStop, passphraseSource.ReadCount);

    }

    [Fact]
    public async Task StopAsync_after_failed_StartAsync_never_opens_an_existing_database_without_the_lock()
    {

        string failedDatabasePath = Path.Combine(
            _tempDir,
            Path.GetFileName(ArcanumPaths.GrimoireDatabaseFile));

        await File.WriteAllBytesAsync(failedDatabasePath, [0x01, 0x02, 0x03]);

        TrackingPassphraseSource passphraseSource = new();

        passphraseSource.SetPassphrase("failed-start-passphrase");

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            passphraseSource,
            _tempDir,
            new InstallationResetMaintenanceLockAccessor(),
            new DelegateStartupRecovery(static (_, _) =>
                Task.FromException<Result<InstallationResetStartupRecoveryState>>(
                    new IOException("Injected startup failure."))));

        _ = await Assert.ThrowsAsync<IOException>(() =>
            service.StartAsync(CancellationToken.None));

        int afterFailedStart = passphraseSource.ReadCount;

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(afterFailedStart, passphraseSource.ReadCount);

    }

    [Fact]
    public async Task Dispose_waits_for_the_entire_post_topology_startup_critical_section()
    {

        string guardedRoot = Path.Combine(_tempDir, "dispose-during-start");

        InstallationResetMaintenanceLockAccessor accessor = new();

        using ManualResetEventSlim actionEntered = new();

        using ManualResetEventSlim releaseAction = new();

        using ManualResetEventSlim disposeAttempted = new();

        using ManualResetEventSlim disposeReturned = new();

        bool startupLeaseDisposedUnderLock = false;

        HostLockSerilogFileSink sink = new(
            guardedRoot,
            Path.Combine(guardedRoot, "logs"),
            retainedFileCountLimit: 3,
            enabled: false);

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            guardedRoot,
            accessor,
            DelegateStartupRecovery.NoActiveReset(),
            sink,
            masterKeyBootstrap: _ => Task.FromException<string?>(
                new IOException("Injected master-key bootstrap failure.")));

        service.ConfigurePostTopologyStartupAction(() =>
        {

            actionEntered.Set();

            releaseAction.Wait();

            return new DelegateDisposable(() =>
                startupLeaseDisposedUnderLock = accessor
                    .BorrowHeldLock(guardedRoot)
                    .IsSuccess);

        });

        Task start = Task.Run(() => service.StartAsync(CancellationToken.None));

        Assert.True(actionEntered.Wait(TimeSpan.FromSeconds(10)));

        Task dispose = Task.Run(() =>
        {

            disposeAttempted.Set();

            service.Dispose();

            disposeReturned.Set();

        });

        Assert.True(disposeAttempted.Wait(TimeSpan.FromSeconds(10)));

        bool returnedBeforeStartupWasReleased;

        bool lockStayedAttached;

        try
        {

            returnedBeforeStartupWasReleased = disposeReturned.Wait(TimeSpan.FromMilliseconds(250));

            lockStayedAttached = accessor.BorrowHeldLock(guardedRoot).IsSuccess;

        }
        finally
        {

            releaseAction.Set();

        }

        _ = await Assert.ThrowsAsync<IOException>(async () => await start);

        await dispose;

        Assert.False(returnedBeforeStartupWasReleased);

        Assert.True(lockStayedAttached);

        Assert.True(startupLeaseDisposedUnderLock);

        Assert.True(accessor.BorrowHeldLock(guardedRoot).IsFailure);

    }

    [Fact]
    public async Task Dispose_waits_for_the_owned_shutdown_checkpoint_before_releasing_the_lock()
    {

        _secretStore.SetApiKey("test-api-key");

        TrackingPassphraseSource passphraseSource = new();

        InstallationResetMaintenanceLockAccessor accessor = new();

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            passphraseSource,
            _tempDir,
            accessor,
            DelegateStartupRecovery.NoActiveReset());

        await service.StartAsync(CancellationToken.None);

        passphraseSource.BlockReads();

        Task stop = Task.Run(() => service.StopAsync(CancellationToken.None));

        Assert.True(passphraseSource.ReadEntered.Wait(TimeSpan.FromSeconds(10)));

        using ManualResetEventSlim disposeAttempted = new();

        using ManualResetEventSlim disposeReturned = new();

        Task dispose = Task.Run(() =>
        {

            disposeAttempted.Set();

            service.Dispose();

            disposeReturned.Set();

        });

        Assert.True(disposeAttempted.Wait(TimeSpan.FromSeconds(10)));

        bool returnedBeforeCheckpointWasReleased;

        bool lockStayedAttached;

        try
        {

            returnedBeforeCheckpointWasReleased = disposeReturned.Wait(TimeSpan.FromMilliseconds(250));

            lockStayedAttached = accessor.BorrowHeldLock(_tempDir).IsSuccess;

        }
        finally
        {

            passphraseSource.ReleaseReads();

        }

        await stop;

        await dispose;

        Assert.False(returnedBeforeCheckpointWasReleased);

        Assert.True(lockStayedAttached);

        Assert.True(accessor.BorrowHeldLock(_tempDir).IsFailure);

    }

    [Fact]
    public async Task StartAsync_authenticates_v2_and_closes_the_single_envelope_ahead_window_before_bootstrap()
    {

        string guardedRoot = Path.Combine(_tempDir, "one-ahead");

        Directory.CreateDirectory(guardedRoot);

        InstallationResetActiveStore store = new(guardedRoot, _credentialStore);

        InstallationResetActiveRecord prepared = CreateResetActiveRecord(
            InstallationResetScope.Global,
            InstallationResetPhase.Prepared,
            InstallationResetDataHandoff.HostFactoryErasure);

        Guid installationId = Guid.Parse("71515151-5151-4151-8151-515151515151");

        InstallationResetActivePublication first;

        using (ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
                   ArcanumMaintenanceLock.TryAcquire(guardedRoot)))
        {

            first = Value(await store.BeginAsync(
                held,
                installationId,
                prepared,
                CancellationToken.None));

        }

        InstallationResetActiveEnvelopeV2 ahead = SealEnvelope(
            guardedRoot,
            first,
            InstallationResetActivePayloadV2.FromRecord(prepared),
            revision: 2);

        File.WriteAllBytes(
            store.ActivePath,
            Value(InstallationResetActiveRecordAuthenticator.EncodeEnvelope(ahead)));

        Assert.True((await store.InspectAsync(CancellationToken.None)).IsFailure);

        InstallationResetMaintenanceLockAccessor accessor = new();

        GrimoireDatabaseHostedService service = CreateLockedRecoveryHost(
            guardedRoot,
            accessor,
            store);

        _ = await Assert.ThrowsAsync<MissingMasterApiKeyException>(() =>
            service.StartAsync(CancellationToken.None));

        InstallationResetActiveRecoveryState recovered = Value(
            await store.InspectAsync(CancellationToken.None));

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
            recovered.Outcome);

        Assert.Equal(2UL, recovered.Publication!.Anchor.Revision);

        Assert.Equal(installationId, recovered.Publication.Envelope.InstallationId);

        Assert.True(accessor.BorrowHeldLock(guardedRoot).IsFailure);

    }

    [Fact]
    public async Task StartAsync_allows_only_an_authenticated_prepared_global_or_all_host_handoff_without_proof()
    {

        InstallationResetActiveRecord global = CreateResetActiveRecord(
            InstallationResetScope.Global,
            InstallationResetPhase.Prepared,
            InstallationResetDataHandoff.HostFactoryErasure);

        InstallationResetActiveRecord all = CreateResetActiveRecord(
            InstallationResetScope.All,
            InstallationResetPhase.Prepared,
            InstallationResetDataHandoff.HostFactoryErasure);

        InstallationResetActiveRecord proofComplete = WithOnlineCompletion(
            global,
            InstallationResetPhase.DataResetComplete);

        (string Name, InstallationResetActiveRecord Record, bool Allowed)[] cases =
        [
            ("global", global, true),
            ("all", all, true),
            ("workspace", CreateResetActiveRecord(
                InstallationResetScope.Workspace,
                InstallationResetPhase.Prepared,
                handoff: null), false),
            ("global-no-handoff", global with { DataHandoff = null }, false),
            ("proof-complete", proofComplete, false),
            ("offline", WithOnlineCompletion(
                global,
                InstallationResetPhase.OfflineCleanupComplete), false),
            ("verified", WithOnlineCompletion(
                global,
                InstallationResetPhase.Verified), false),
            ("completed", WithOnlineCompletion(
                global,
                InstallationResetPhase.Completed), false),
        ];

        foreach ((string name, InstallationResetActiveRecord record, bool allowed) in cases)
        {

            string guardedRoot = Path.Combine(_tempDir, "v2-admission-" + name);

            Directory.CreateDirectory(guardedRoot);

            InstallationResetActiveStore store = new(guardedRoot, _credentialStore);

            using (ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
                       ArcanumMaintenanceLock.TryAcquire(guardedRoot)))
            {

                _ = Value(await store.BeginAsync(
                    held,
                    Guid.NewGuid(),
                    record,
                    CancellationToken.None));

            }

            GrimoireDatabaseHostedService service = CreateLockedRecoveryHost(
                guardedRoot,
                new InstallationResetMaintenanceLockAccessor(),
                store);

            Exception error = await Assert.ThrowsAnyAsync<Exception>(() =>
                service.StartAsync(CancellationToken.None));

            if (allowed)
            {

                Assert.IsType<MissingMasterApiKeyException>(error);

            }
            else
            {

                Assert.Contains(
                    "factory reset",
                    Assert.IsType<InvalidOperationException>(error).Message,
                    StringComparison.OrdinalIgnoreCase);

            }

        }

    }

    [Fact]
    public async Task StartAsync_publishes_the_exact_authenticated_recovery_identity_only_for_the_host_lifetime()
    {

        _secretStore.SetApiKey("test-api-key");

        ActiveInstallationReset active = CreateHostRecoveryActive(
            InstallationResetScope.Global) with
        {
            PlanId = "published-plan",
            OperationId = Guid.Parse("57575757-5757-4757-8757-575757575757"),
        };

        InstallationResetApiAdmission admission = new();

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            _tempDir,
            new InstallationResetMaintenanceLockAccessor(),
            new DelegateStartupRecovery((_, _) => Task.FromResult(
                Result<InstallationResetStartupRecoveryState>.Success(
                    new InstallationResetStartupRecoveryState(
                        active,
                        ExpectedInstallationId: null,
                        IsLegacyV1: false)))),
            admission);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(active.Scope, admission.ActiveRecovery?.Scope);

        Assert.Equal(active.PlanId, admission.ActiveRecovery?.InstallationPlanId);

        Assert.Equal(active.OperationId, admission.ActiveRecovery?.OperationId);

        await service.StopAsync(CancellationToken.None);

        Assert.NotNull(admission.ActiveRecovery);

    }

    [Fact]
    public async Task Recovery_host_retains_the_exact_client_mutex_and_durable_blocker_until_shutdown()
    {

        _secretStore.SetApiKey("test-api-key");

        string guardedRoot = Path.Combine(_tempDir, "retained-recovery-client-lock");

        ActiveInstallationReset active = CreateHostRecoveryActive(
            InstallationResetScope.All);

        ClientMutationBlockerStore blocker = new(guardedRoot);

        InstallationMaintenanceCoordination coordination = new(
            guardedRoot,
            blocker,
            new StaticClientResetEvidenceProbe(active),
            new StaticClientRestoreEvidenceProbe(active: false));

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            guardedRoot,
            new InstallationResetMaintenanceLockAccessor(),
            new DelegateStartupRecovery((_, _) => Task.FromResult(
                Result<InstallationResetStartupRecoveryState>.Success(
                    new InstallationResetStartupRecoveryState(
                        active,
                        ExpectedInstallationId: null,
                        IsLegacyV1: false)))),
            apiAdmission: new InstallationResetApiAdmission(),
            startupCoordination: coordination);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(
            ArcanumClientMutationLockAcquisitionDisposition.Contended,
            ArcanumClientMutationLock.AcquireDetailed(guardedRoot).Disposition);

        ClientMutationBlockerPublication publication = Assert.IsType<
            ClientMutationBlockerPublication>((await blocker.InspectAsync()).Value);

        Assert.Equal(active.PlanId, publication.Record.PlanId);

        Assert.Equal(active.OperationId, publication.Record.OperationId);

        await service.StopAsync(CancellationToken.None);

        using ArcanumClientMutationLock released = Assert.IsType<ArcanumClientMutationLock>(
            ArcanumClientMutationLock.AcquireDetailed(guardedRoot).Lock);

        Assert.NotNull((await blocker.InspectAsync()).Value);

    }

    [Fact]
    public async Task StartAsync_allows_eligible_v1_only_for_locked_migration_and_blocks_every_other_legacy_state()
    {

        InstallationResetActiveRecord global = CreateResetActiveRecord(
            InstallationResetScope.Global,
            InstallationResetPhase.Prepared,
            InstallationResetDataHandoff.HostFactoryErasure);

        InstallationResetActiveRecord all = CreateResetActiveRecord(
            InstallationResetScope.All,
            InstallationResetPhase.Prepared,
            InstallationResetDataHandoff.HostFactoryErasure);

        (string Name, InstallationResetActiveRecord Record, bool Allowed)[] cases =
        [
            ("global", global, true),
            ("all", all, true),
            ("workspace", CreateResetActiveRecord(
                InstallationResetScope.Workspace,
                InstallationResetPhase.Prepared,
                handoff: null), false),
            ("global-no-handoff", global with { DataHandoff = null }, false),
            ("proof-complete", WithOnlineCompletion(
                global,
                InstallationResetPhase.DataResetComplete), false),
            ("completed", WithOnlineCompletion(
                global,
                InstallationResetPhase.Completed), false),
        ];

        foreach ((string name, InstallationResetActiveRecord record, bool allowed) in cases)
        {

            string guardedRoot = Path.Combine(_tempDir, "v1-admission-" + name);

            Directory.CreateDirectory(guardedRoot);

            InstallationResetActiveStore legacyWriter = new(guardedRoot);

            Assert.True((await legacyWriter.WriteLegacyV1ForTestsAsync(
                record,
                CancellationToken.None)).IsSuccess);

            byte[] before = await File.ReadAllBytesAsync(legacyWriter.ActivePath);

            InstallationResetActiveStore authenticated = new(guardedRoot, _credentialStore);

            GrimoireDatabaseHostedService service = CreateLockedRecoveryHost(
                guardedRoot,
                new InstallationResetMaintenanceLockAccessor(),
                authenticated);

            Exception error = await Assert.ThrowsAnyAsync<Exception>(() =>
                service.StartAsync(CancellationToken.None));

            if (allowed)
            {

                Assert.IsType<MissingMasterApiKeyException>(error);

            }
            else
            {

                Assert.Contains(
                    "factory reset",
                    Assert.IsType<InvalidOperationException>(error).Message,
                    StringComparison.OrdinalIgnoreCase);

            }

            Assert.Equal(before, await File.ReadAllBytesAsync(legacyWriter.ActivePath));

            AssertNoResetCredentials(guardedRoot);

        }

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_completes_only_closed_or_key_only_suffixes_before_bootstrap(
        bool keyOnly)
    {

        string guardedRoot = Path.Combine(
            _tempDir,
            keyOnly ? "startup-key-only" : "startup-closed");

        Directory.CreateDirectory(guardedRoot);

        if (keyOnly)
        {

            using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            BackupRestoreProfileNamespace profile = Value(
                BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot));

            Value(new InstallationResetActiveRecordKeyProvider(_credentialStore).CreateOrOpen(
                held,
                guardedRoot,
                profile)).Dispose();

        }
        else
        {

            using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            InstallationResetActiveStore interrupted = new(
                guardedRoot,
                _credentialStore,
                new InstallationResetActiveFilePersistence(
                    failBeforeStep: step => string.Equals(
                        step,
                        "file:delete",
                        StringComparison.Ordinal)));

            InstallationResetActiveRecord completed = CreateResetActiveRecord(
                InstallationResetScope.Global,
                InstallationResetPhase.Completed,
                handoff: null);

            _ = Value(await interrupted.BeginAsync(
                held,
                Guid.NewGuid(),
                completed,
                CancellationToken.None));

            Assert.True((await interrupted.RetireAsync(
                held,
                completed.OperationId,
                CancellationToken.None)).IsFailure);

        }

        InstallationResetActiveStore store = new(guardedRoot, _credentialStore);

        GrimoireDatabaseHostedService service = CreateLockedRecoveryHost(
            guardedRoot,
            new InstallationResetMaintenanceLockAccessor(),
            store);

        _ = await Assert.ThrowsAsync<MissingMasterApiKeyException>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.NoActiveRecord,
            Value(await store.InspectAsync(CancellationToken.None)).Outcome);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_compares_the_active_envelope_installation_uuid_before_readiness(
        bool escapeHatchOptIn)
    {

        _secretStore.SetApiKey("test-api-key");

        IServiceScopeFactory setupScopes = CreateScopeFactory(_credentialStore);

        GrimoireDbPassphraseSource setupPassphrase = new();

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            setupPassphrase,
            setupScopes,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        Guid databaseInstallationId = await ReadDatabaseInstallationIdAsync(
            _dbPath,
            setupPassphrase.Passphrase);

        Guid activeInstallationId = Guid.Parse(
            "81515151-5151-4151-8151-515151515151");

        Assert.NotEqual(databaseInstallationId, activeInstallationId);

        InstallationResetActiveStore store = new(_tempDir, _credentialStore);

        using (ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
                   ArcanumMaintenanceLock.TryAcquire(_tempDir)))
        {

            _ = Value(await store.BeginAsync(
                held,
                activeInstallationId,
                CreateResetActiveRecord(
                    InstallationResetScope.Global,
                    InstallationResetPhase.Prepared,
                    InstallationResetDataHandoff.HostFactoryErasure),
                CancellationToken.None));

        }

        IServiceScopeFactory hostScopes = CreateScopeFactory(
            _credentialStore,
            includeHostProcessTools: true,
            escapeHatchOptIn);

        InstallationResetApiAdmission admission = new();

        GrimoireDatabaseHostedService service = new(
            hostScopes,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            _tempDir,
            new InstallationResetMaintenanceLockAccessor(),
            new InstallationResetStartupRecovery(_tempDir, store),
            admission);

        GrimoireDatabaseUnavailableException error =
            await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() =>
                service.StartAsync(CancellationToken.None));

        Assert.DoesNotContain(
            activeInstallationId.ToString("D"),
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        using IServiceScope scope = hostScopes.CreateScope();

        GrimoireDbReadiness readiness =
            scope.ServiceProvider.GetRequiredService<GrimoireDbReadiness>();

        CovenantAvailabilitySnapshot availability = scope.ServiceProvider
            .GetRequiredService<CovenantAvailability>()
            .Current;

        HostProcessToolsRuntimePolicy hostToolsPolicy = scope.ServiceProvider
            .GetRequiredService<HostProcessToolsRuntimePolicy>();

        Assert.False(readiness.IsReady);

        Assert.IsType<GrimoireDatabaseUnavailableException>(readiness.Failure);

        Assert.Equal(1, availability.Generation);

        Assert.Equal(CovenantCapabilityState.Unavailable, availability.Canonical);

        Assert.Null(availability.DatasetGeneration);

        Assert.False(hostToolsPolicy.IsPublished);

        Assert.False(hostToolsPolicy.CovenantPermitted);

        Assert.False(hostToolsPolicy.HostProcessToolsPermitted);

        Assert.Null(admission.ActiveRecovery);

    }

    [Fact]
    public async Task StartAsync_hard_host_tools_block_before_identity_keeps_the_process_policy_unpublished()
    {

        _secretStore.SetApiKey("test-api-key");

        IServiceScopeFactory hostScopes = CreateScopeFactory(
            _credentialStore,
            includeHostProcessTools: true,
            escapeHatchOptIn: false,
            markerReadStatusOverride: HostProcessToolsMarkerReadStatus.Malformed);

        GrimoireDatabaseHostedService service = new(
            hostScopes,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            _tempDir,
            new InstallationResetMaintenanceLockAccessor(),
            DelegateStartupRecovery.NoActiveReset());

        _ = await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() =>
            service.StartAsync(CancellationToken.None));

        using IServiceScope scope = hostScopes.CreateScope();

        HostProcessToolsRuntimePolicy policy = scope.ServiceProvider
            .GetRequiredService<HostProcessToolsRuntimePolicy>();

        CovenantAvailabilitySnapshot availability = scope.ServiceProvider
            .GetRequiredService<CovenantAvailability>()
            .Current;

        GrimoireDbReadiness readiness = scope.ServiceProvider
            .GetRequiredService<GrimoireDbReadiness>();

        Assert.False(policy.IsPublished);

        Assert.False(policy.CovenantPermitted);

        Assert.False(policy.HostProcessToolsPermitted);

        Assert.Equal(CovenantCapabilityState.Unavailable, availability.Canonical);

        Assert.False(readiness.IsReady);

        Assert.IsType<GrimoireDatabaseUnavailableException>(readiness.Failure);

    }

    [Theory]
    [InlineData(ExpectedIdentityEvidence.Missing)]
    [InlineData(ExpectedIdentityEvidence.Malformed)]
    [InlineData(ExpectedIdentityEvidence.Ambiguous)]
    [InlineData(ExpectedIdentityEvidence.Mismatch)]
    public async Task Expected_installation_uuid_comparison_fails_closed_on_nonexact_authority_rows(
        ExpectedIdentityEvidence evidence)
    {

        Guid expected = Guid.Parse("91515151-5151-4151-8151-515151515151");

        await using SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync();

        await using (SqliteCommand create = connection.CreateCommand())
        {

            create.CommandText =
                "CREATE TABLE covenant_authority_state (StateKey INTEGER, InstallationIdentity TEXT);";

            _ = await create.ExecuteNonQueryAsync();

        }

        if (evidence is not ExpectedIdentityEvidence.Missing)
        {

            await InsertAuthorityIdentityAsync(
                connection,
                stateKey: 1,
                evidence switch
                {
                    ExpectedIdentityEvidence.Malformed => "not-a-canonical-uuid",
                    _ => Guid.Parse("a1515151-5151-4151-8151-515151515151")
                        .ToString("D")
                        .ToUpperInvariant(),
                });

        }

        if (evidence is ExpectedIdentityEvidence.Ambiguous)
        {

            await InsertAuthorityIdentityAsync(
                connection,
                stateKey: 2,
                expected.ToString("D").ToUpperInvariant());

        }

        _ = await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() =>
            GrimoireDatabaseBootstrapper.VerifyExpectedInstallationIdentityAsync(
                connection,
                expected,
                CancellationToken.None));

    }

    [Fact]
    public async Task Expected_installation_uuid_comparison_accepts_only_the_exact_canonical_row()
    {

        Guid expected = Guid.Parse("b1515151-5151-4151-8151-515151515151");

        await using SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync();

        await using (SqliteCommand create = connection.CreateCommand())
        {

            create.CommandText =
                "CREATE TABLE covenant_authority_state (StateKey INTEGER, InstallationIdentity TEXT);";

            _ = await create.ExecuteNonQueryAsync();

        }

        await InsertAuthorityIdentityAsync(
            connection,
            stateKey: 1,
            expected.ToString("D").ToUpperInvariant());

        await GrimoireDatabaseBootstrapper.VerifyExpectedInstallationIdentityAsync(
            connection,
            expected,
            CancellationToken.None);

    }

    [Theory]

    [InlineData(InstallationResetScope.Global)]

    [InlineData(InstallationResetScope.All)]

    public async Task StartAsync_allows_proof_free_prepared_host_handoff_to_reach_host_lock(
        InstallationResetScope scope)
    {

        using ArcanumMaintenanceLock? held =
            ArcanumMaintenanceLock.TryAcquire(_tempDir);

        Assert.NotNull(held);

        GrimoireDatabaseHostedService service = new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            _tempDir,
            new ActiveResetProbe(CreateHostRecoveryActive(scope)));

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.StartAsync(CancellationToken.None));

        Assert.Contains(
            "maintenance lock",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            "factory reset",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(_dbPath));

    }

    [Fact]
    public async Task StartAsync_blocks_proof_complete_later_and_workspace_handoffs_under_host_lock()
    {

        ActiveInstallationReset recoverable =
            CreateHostRecoveryActive(InstallationResetScope.Global);

        ActiveInstallationReset[] blocked =
        [
            recoverable with
            {
                OnlineDataCompletionDurable = true,
            },
            recoverable with
            {
                Phase = InstallationResetPhase.DataResetComplete,
            },
            recoverable with
            {
                Phase = InstallationResetPhase.OfflineCleanupComplete,
            },
            recoverable with
            {
                Phase = InstallationResetPhase.Verified,
            },
            recoverable with
            {
                Phase = InstallationResetPhase.Completed,
            },
            CreateHostRecoveryActive(InstallationResetScope.Workspace),
        ];

        foreach (ActiveInstallationReset active in blocked)
        {

            InstallationResetMaintenanceLockAccessor accessor = new();

            GrimoireDatabaseHostedService service = new(
                _scopeFactory,
                _secretStore,
                new GrimoireDbPassphraseSource(),
                _tempDir,
                accessor,
                new DelegateStartupRecovery((held, _) =>
                {

                    Assert.Same(held, accessor.BorrowHeldLock(_tempDir).Value);

                    return Task.FromResult(
                        Result<InstallationResetStartupRecoveryState>.Success(
                            new InstallationResetStartupRecoveryState(
                                active,
                                ExpectedInstallationId: null,
                                IsLegacyV1: false)));

                }));

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

            Assert.True(accessor.BorrowHeldLock(_tempDir).IsFailure);

            using ArcanumMaintenanceLock? reacquired =
                ArcanumMaintenanceLock.TryAcquire(_tempDir);

            Assert.NotNull(reacquired);

        }

        Assert.False(File.Exists(_dbPath));

    }

    /// <summary>
    /// A canonical restore journal nothing commits to is evidence, and evidence is never absence.
    /// </summary>
    /// <remarks>
    /// The bootstrap has to stop before it creates the live root, opens a database, or publishes
    /// readiness: an unanchored journal beside the installation means either a restore this build
    /// cannot account for or a file somebody planted, and neither is a tree to start against
    /// (§10.19.8).
    /// </remarks>
    [Fact]
    public async Task EnsureInitializedAsync_refuses_to_start_beside_a_restore_journal_it_cannot_authenticate()
    {

        _secretStore.SetApiKey("test-api-key");

        string live = Path.Combine(_tempDir, "live");

        Directory.CreateDirectory(live);

        string lookalike = Path.Combine(_tempDir, BackupRestoreJournal.CreateStagingName());

        Directory.CreateDirectory(lookalike);

        File.WriteAllText(
            Path.Combine(lookalike, BackupRestoreJournalAnchorStore.JournalFileName),
            "{}");

        using ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(live);

        Assert.NotNull(held);

        _ = await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() =>
            GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                _secretStore,
                _passphraseSource,
                _scopeFactory,
                Path.Combine(live, "grimoire.db"),
                live,
                held,
                expectedInstallationId: null,
                CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(live, "grimoire.db")));

        Assert.False(IsReady());

    }

    /// <summary>
    /// The pre-Covenant sweep still runs, because <c>BackupRestoreService</c> still writes that journal.
    /// </summary>
    [Fact]
    public async Task EnsureInitializedAsync_still_resolves_an_interrupted_pre_covenant_restore()
    {

        _secretStore.SetApiKey("test-api-key");

        string live = Path.Combine(_tempDir, "live");

        Directory.CreateDirectory(live);

        string stagingRoot = Path.Combine(_tempDir, BackupRestoreJournal.CreateStagingName());

        OwnedTemporaryDirectory owned = OwnedTemporaryDirectory.Create(stagingRoot);

        _ = BackupRestoreJournal.Write(
            stagingRoot,
            new BackupRestoreJournalRecord(
                BackupRestoreJournal.CurrentVersion,
                Guid.NewGuid(),
                BackupRestoreConflictMode.ReplaceInstallation,
                BackupRestorePhase.Stage,
                live,
                Path.Combine(stagingRoot, BackupRestoreJournal.StagedDirectoryName),
                Path.Combine(stagingRoot, BackupRestoreJournal.DisplacedDirectoryName),
                SafetyBackupPath: null,
                Path.Combine(_tempDir, "source.arcbackup"),
                owned.VolumeId,
                owned.FileId));

        using ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(live);

        Assert.NotNull(held);

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            Path.Combine(live, "grimoire.db"),
            live,
            held,
            expectedInstallationId: null,
            CancellationToken.None);

        Assert.False(Directory.Exists(stagingRoot));

        Assert.True(IsReady());

    }

    [Fact]
    public async Task EnsureInitializedAsync_keeps_an_absent_live_root_closed_when_legacy_restore_evidence_is_ambiguous()
    {

        _secretStore.SetApiKey("test-api-key");

        string live = Path.Combine(_tempDir, "absent-legacy-live");

        string stagingRoot = Path.Combine(
            _tempDir,
            BackupRestoreJournal.CreateStagingName());

        OwnedTemporaryDirectory owned = OwnedTemporaryDirectory.Create(stagingRoot);

        string stagedRoot = Path.Combine(
            stagingRoot,
            BackupRestoreJournal.StagedDirectoryName);

        string displacedRoot = Path.Combine(
            stagingRoot,
            BackupRestoreJournal.DisplacedDirectoryName);

        Directory.CreateDirectory(displacedRoot);

        _ = BackupRestoreJournal.Write(
            stagingRoot,
            new BackupRestoreJournalRecord(
                BackupRestoreJournal.CurrentVersion,
                Guid.NewGuid(),
                BackupRestoreConflictMode.ReplaceInstallation,
                BackupRestorePhase.Commit,
                Path.Combine(_tempDir, "different-installation"),
                stagedRoot,
                displacedRoot,
                SafetyBackupPath: null,
                Path.Combine(_tempDir, "source.arcbackup"),
                owned.VolumeId,
                owned.FileId));

        using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(live));

        int postTopologyCalls = 0;

        _ = await Assert.ThrowsAsync<GrimoireDatabaseUnavailableException>(() =>
            GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                _secretStore,
                new GrimoireDbPassphraseSource(),
                _scopeFactory,
                Path.Combine(live, "grimoire.db"),
                live,
                held,
                expectedInstallationId: null,
                postRestoreTopology: _ =>
                {

                    postTopologyCalls++;

                    return Task.CompletedTask;

                },
                CancellationToken.None));

        Assert.Equal(0, postTopologyCalls);

        Assert.False(Directory.Exists(live));

        Assert.True(Directory.Exists(displacedRoot));

        Assert.False(IsReady());

    }

    [Fact]
    public async Task Lock_owning_bootstrap_publishes_the_adoption_boundary_immediately_before_readiness()
    {

        _secretStore.SetApiKey("test-api-key");

        using IServiceScope scope = _scopeFactory.CreateScope();

        GrimoireDbReadiness readiness = scope.ServiceProvider.GetRequiredService<GrimoireDbReadiness>();

        readiness.ProbeAdoptionAtMarkReady = true;

        using ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(_tempDir);

        Assert.NotNull(held);

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            held,
            expectedInstallationId: null,
            CancellationToken.None);

        Assert.True(readiness.IsReady);

        Assert.True(readiness.AdoptionRefusedAtMarkReady);

    }

    /// <summary>
    /// Bootstrapping with <c>Arcanum:Features:Covenant</c> off must leave Covenant residence unlatched.
    /// </summary>
    /// <remarks>
    /// The latch is what forbids the offline host-tools transition, and it is one-way. Startup latched
    /// it while taking envelope master material, before any request and before anything consulted the
    /// feature flag, so an operator who had never enabled Covenant lost the transition the moment the
    /// host came up — and so did the offline command's own process, which bootstraps the Grimoire
    /// before it can run the transition it exists to perform (§10.12).
    ///
    /// <para>Deriving is not what this test wants stopped. Startup has to keep taking that material on
    /// a disabled installation, because the recovery-keyed families are what let a factory erasure
    /// fence protected state, so the assertions below pin the derivation as still happening and the
    /// authority as still published. What must not happen is the latch.</para>
    ///
    /// <para>The assertion is guarded on the latch not already being set, because
    /// <c>CovenantProcessResidence</c> is process-wide by design and a full-suite run latches it in
    /// some other class long before this one. Nothing in this class latches it first: the guard is
    /// evaluated before any Covenant connection is opened here.</para>
    /// </remarks>
    [Fact]
    public async Task EnsureInitializedAsync_WithCovenantDisabled_PublishesAuthorityWithoutLatchingResidence()
    {

        _secretStore.SetApiKey("test-api-key");

        bool alreadyLatched = CovenantProcessResidence.HasOpened;

        IServiceScopeFactory scopes = CreateCovenantAuthorityScopeFactory(
            _credentialStore,
            covenantEnabled: false);

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            scopes,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        // Without this the test could pass because the host-tools gate refused Covenant outright,
        // which would prove nothing about what a permitted startup does.
        Assert.True(
            scope.ServiceProvider.GetRequiredService<HostProcessToolsRuntimePolicy>().CovenantPermitted);

        if (!alreadyLatched)
        {

            Assert.False(CovenantProcessResidence.HasOpened);

        }

        // The control on the assertion above: "never derive anything" would satisfy it and would take
        // the erasure fencing of every disabled installation down with it.
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<CovenantEnvelopeMasterKeyProvider>().Current);

        Result<CovenantInstallationReadLease> lease = await scope.ServiceProvider
            .GetRequiredService<CovenantOperationGate>()
            .AcquireInstallationReadAsync(CancellationToken.None);

        Assert.True(lease.IsSuccess);

        await lease.Value.DisposeAsync();

    }

    /// <summary>
    /// The composition the residence test mirrors is the one the CLI and the host actually build.
    /// </summary>
    /// <remarks>
    /// It registers the authority boundary by hand, because the shared schema fixture omits it. This
    /// pins that hand-registration to the production graph: if <c>AddArcanumGrimoireForCli</c> ever
    /// stops composing the key provider, the marker store, or the real environment probe, the
    /// residence guarantee above would be describing a container no operator ever runs.
    /// </remarks>
    [Fact]
    public void AddArcanumGrimoireForCli_ComposesTheAuthorityBoundaryTheResidenceTestMirrors()
    {

        ServiceCollection services = new();

        services.AddSingleton<IOsCredentialStore>(new InMemoryOsCredentialStore());

        _ = services.AddArcanumGrimoireForCli();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<CovenantEnvelopeMasterKeyProvider>());

        Assert.NotNull(provider.GetService<HostProcessToolsRuntimePolicy>());

        Assert.NotNull(provider.GetService<IHostProcessToolsMarkerStore>());

        Assert.IsType<HostProcessToolsEnvironmentProbe>(
            provider.GetService<IHostProcessToolsEnvironmentProbe>());

    }

    private bool IsReady()
    {

        using IServiceScope scope = _scopeFactory.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IGrimoireDbReadiness>().IsReady;

    }

    /// <summary>
    /// The schema container plus the real Covenant authority boundary, bound to a feature flag.
    /// </summary>
    /// <remarks>
    /// Deliberately the production types rather than the host-tools fakes the other tests use. A fake
    /// environment probe reports a canned residence value, and residence is the whole subject here.
    /// </remarks>
    internal static IServiceScopeFactory CreateCovenantAuthorityScopeFactory(
        IOsCredentialStore credentials,
        bool covenantEnabled)
    {

        ServiceCollection services = new();

        services.AddSingleton<GrimoireDbReadiness>();

        services.AddSingleton<IGrimoireDbReadiness>(
            static sp => sp.GetRequiredService<GrimoireDbReadiness>());

        services.AddSingleton<IOsCredentialStore>(credentials);

        _ = services.AddGrimoireSchemaInstallation();

        _ = services.AddOptions<ArcanumSettings>()
            .Configure(settings => settings.Features.Covenant = covenantEnabled);

        services.AddSingleton(
            static sp => new CovenantEnvelopeMasterKeyProvider(
                sp.GetRequiredService<CovenantRuntimeGenerationProvider>()));

        services.AddSingleton<HostProcessToolsRuntimePolicy>();

        services.AddSingleton<IHostProcessToolsRuntimePolicy>(
            static sp => sp.GetRequiredService<HostProcessToolsRuntimePolicy>());

        services.AddSingleton<IHostProcessToolsMarkerStore>(
            static sp => new HostProcessToolsMarkerStore(sp.GetRequiredService<IOsCredentialStore>()));

        services.AddSingleton<IHostProcessToolsEnvironmentProbe>(
            static sp => new HostProcessToolsEnvironmentProbe(
                sp.GetRequiredService<IOptions<ArcanumSettings>>()));

        return services
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    }

    private static IServiceScopeFactory CreateScopeFactory(
        IOsCredentialStore credentials,
        bool includeHostProcessTools = false,
        bool escapeHatchOptIn = false,
        HostProcessToolsMarkerReadStatus? markerReadStatusOverride = null)
    {

        ServiceCollection services = new();

        services.AddSingleton<GrimoireDbReadiness>();

        services.AddSingleton<IGrimoireDbReadiness>(
            static sp => sp.GetRequiredService<GrimoireDbReadiness>());

        services.AddSingleton<IOsCredentialStore>(credentials);

        _ = services.AddGrimoireSchemaInstallation();

        if (includeHostProcessTools)
        {

            HostProcessToolsRuntimePolicy policy = new();

            services.AddSingleton(policy);

            services.AddSingleton<IHostProcessToolsRuntimePolicy>(policy);

            services.AddSingleton<IHostProcessToolsMarkerStore>(
                new FakeHostProcessToolsMarkerStore
                {
                    ReadStatusOverride = markerReadStatusOverride,
                });

            services.AddSingleton<IHostProcessToolsEnvironmentProbe>(
                new FakeHostProcessToolsEnvironmentProbe
                {
                    Edition = escapeHatchOptIn
                        ? RetroDownfall.Arcanum.Core.Configuration.ArcanumEdition.Development
                        : RetroDownfall.Arcanum.Core.Configuration.ArcanumEdition.Local,
                    EscapeHatchOptIn = escapeHatchOptIn,
                });

        }

        return services
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    }

    private static ActiveInstallationReset CreateHostRecoveryActive(
        InstallationResetScope scope) =>
        new(
            scope,
            WorkspaceRoot: scope is InstallationResetScope.Workspace or InstallationResetScope.All
                ? "/workspace"
                : null,
            PlanId: "active-plan",
            OperationId: Guid.Parse("51515151-5151-4151-8151-515151515151"),
            Phase: InstallationResetPhase.Prepared,
            DataHandoff: InstallationResetDataHandoff.HostFactoryErasure,
            OnlineDataCompletionDurable: false);

    private GrimoireDatabaseHostedService CreateLockedRecoveryHost(
        string guardedRoot,
        InstallationResetMaintenanceLockAccessor accessor,
        InstallationResetActiveStore store) =>
        new(
            _scopeFactory,
            _secretStore,
            new GrimoireDbPassphraseSource(),
            guardedRoot,
            accessor,
            new InstallationResetStartupRecovery(guardedRoot, store));

    private static InstallationResetActiveRecord CreateResetActiveRecord(
        InstallationResetScope scope,
        InstallationResetPhase phase,
        InstallationResetDataHandoff? handoff)
    {

        InstallationResetAcceptedBinding binding = new(
            "binding",
            ["/selected"],
            ["/excluded"],
            [],
            [ArcanumCredentialIdentity.MasterApiKeyAccount],
            ["data-plan"]);

        return new InstallationResetActiveRecord(
            InstallationResetActiveStore.CurrentVersion,
            Guid.NewGuid(),
            "composite-plan",
            scope,
            scope is InstallationResetScope.Workspace or InstallationResetScope.All
                ? new DataRetentionWorkspaceBinding(
                    Guid.NewGuid(),
                    "/selected/workspace")
                : null,
            binding,
            phase,
            PointOfNoReturn: phase is not InstallationResetPhase.Prepared,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null,
            DataHandoff: handoff);

    }

    private static InstallationResetActiveRecord WithOnlineCompletion(
        InstallationResetActiveRecord record,
        InstallationResetPhase phase) =>
        record with
        {
            Phase = phase,

            PointOfNoReturn = true,

            OnlineDataCompletion = new InstallationResetOnlineDataCompletion(
                Guid.NewGuid(),
                record.OperationId,
                "data-plan",
                RowsDeleted: 1,
                FilesDeleted: 1,
                EstimatedBytesDeleted: 1,
                DerivedRecordsDeleted: 1),
        };

    private InstallationResetActiveEnvelopeV2 SealEnvelope(
        string guardedRoot,
        InstallationResetActivePublication publication,
        InstallationResetActivePayloadV2 payload,
        ulong revision)
    {

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot));

        using InstallationResetActiveRecordKeyLease key = Value(
            new InstallationResetActiveRecordKeyProvider(_credentialStore)
                .OpenExisting(profile));

        return Value(InstallationResetActiveRecordAuthenticator.Seal(
            key,
            publication.Location,
            publication.Anchor.InstallationId,
            revision,
            publication.EnvelopeDigest,
            payload));

    }

    private void AssertNoResetCredentials(string guardedRoot)
    {

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot));

        string[] accounts =
        [
            ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(
                profile.AccountSuffix),
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(
                profile.AccountSuffix),
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(
                profile.AccountSuffix),
        ];

        Assert.All(
            accounts,
            account => Assert.Equal(
                OsCredentialStoreStatus.NotFound,
                _credentialStore.TryGet(
                    ArcanumCredentialIdentity.Service,
                    account).Status));

    }

    private static async Task<Guid> ReadDatabaseInstallationIdAsync(
        string databasePath,
        string passphrase)
    {

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Password = passphrase,
        }.ToString());

        await connection.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT InstallationIdentity FROM covenant_authority_state WHERE StateKey = 1;";

        string value = Assert.IsType<string>(await command.ExecuteScalarAsync());

        return Guid.ParseExact(value, "D");

    }

    private static async Task InsertAuthorityIdentityAsync(
        SqliteConnection connection,
        long stateKey,
        string identity)
    {

        await using SqliteCommand insert = connection.CreateCommand();

        insert.CommandText =
            "INSERT INTO covenant_authority_state (StateKey, InstallationIdentity) VALUES ($key, $identity);";

        _ = insert.Parameters.AddWithValue("$key", stateKey);

        _ = insert.Parameters.AddWithValue("$identity", identity);

        _ = await insert.ExecuteNonQueryAsync();

    }

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;

    }

    private sealed class ActiveResetProbe(
        ActiveInstallationReset active) : IInstallationStartupProbe
    {

        public Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<ActiveInstallationReset?>.Success(
                active));

        public Result<bool> IsFreshInstallation() =>
            Result<bool>.Success(false);

    }

    private sealed class StaticClientResetEvidenceProbe(
        ActiveInstallationReset? active) : IClientMutationResetEvidenceProbe
    {

        public Task<Result<ActiveInstallationReset?>> InspectAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                Result<ActiveInstallationReset?>.Success(active));

        }

    }

    private sealed class StaticClientRestoreEvidenceProbe(bool active) :
        IClientMutationRestoreEvidenceProbe
    {

        public Task<Result<ActiveReplacementRestore?>> InspectAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                Result<ActiveReplacementRestore?>.Success(
                    active
                        ? new ActiveReplacementRestore(Guid.NewGuid())
                        : null));

        }

    }

    private sealed class DelegateStartupRecovery(
        Func<
            ArcanumMaintenanceLock,
            CancellationToken,
            Task<Result<InstallationResetStartupRecoveryState>>> recover)
        : IInstallationResetStartupRecovery
    {

        public static DelegateStartupRecovery NoActiveReset() =>
            new(static (_, _) =>
                Task.FromResult(Result<InstallationResetStartupRecoveryState>.Success(
                    new InstallationResetStartupRecoveryState(
                        ActiveReset: null,
                        ExpectedInstallationId: null,
                        IsLegacyV1: false))));

        public Task<Result<InstallationResetStartupRecoveryState>> RecoverBeforeBootstrapAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default) =>
            recover(heldInstallationLock, cancellationToken);

    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {

        public void Dispose() => dispose();

    }

    private sealed class DisposeCallbackScopeFactory(
        IServiceScopeFactory inner,
        Action afterFirstDispose) : IServiceScopeFactory
    {

        private int _callbackAvailable = 1;

        public IServiceScope CreateScope() =>
            new DisposeCallbackScope(
                inner.CreateScope(),
                () =>
                {

                    if (Interlocked.Exchange(ref _callbackAvailable, 0) == 1)
                    {

                        afterFirstDispose();

                    }

                });

    }

    private sealed class DisposeCallbackScope(
        IServiceScope inner,
        Action afterDispose) : IServiceScope, IAsyncDisposable
    {

        private int _disposed;

        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {

                return;

            }

            inner.Dispose();

            afterDispose();

        }

        public async ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {

                return;

            }

            if (inner is IAsyncDisposable asyncDisposable)
            {

                await asyncDisposable.DisposeAsync().ConfigureAwait(false);

            }
            else
            {

                inner.Dispose();

            }

            afterDispose();

        }

    }

    private sealed class TrackingPassphraseSource : IGrimoireDbPassphraseSource
    {

        private readonly ManualResetEventSlim _readEntered = new();

        private readonly ManualResetEventSlim _releaseReads = new(initialState: true);

        private string? _passphrase;

        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public ManualResetEventSlim ReadEntered => _readEntered;

        public string Passphrase
        {

            get
            {

                _ = Interlocked.Increment(ref _readCount);

                _readEntered.Set();

                _releaseReads.Wait();

                return _passphrase
                    ?? throw new InvalidOperationException(
                        "Grimoire database passphrase has not been initialized.");

            }

        }

        public void SetPassphrase(string passphrase)
        {

            ArgumentException.ThrowIfNullOrEmpty(passphrase);

            _passphrase = passphrase;

        }

        public void BlockReads()
        {

            _readEntered.Reset();

            _releaseReads.Reset();

        }

        public void ReleaseReads() => _releaseReads.Set();

    }

    public enum StartupRecoveryFailureMode : byte
    {

        ReturnedFailure = 1,

        ThrownFailure = 2,

        Cancellation = 3,

    }

    public enum ExpectedIdentityEvidence : byte
    {

        Missing = 1,

        Malformed = 2,

        Ambiguous = 3,

        Mismatch = 4,

    }

    public enum LockedStartupTopology : byte
    {

        DirectRootSymlink = 1,

        AncestorSymlink = 2,

        NonDirectoryAncestor = 3,

        InaccessibleAncestor = 4,

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

    private sealed class GrimoireDbReadiness(CovenantOperationGate gate) : IGrimoireDbReadiness
    {

        public bool IsReady { get; private set; }

        public Exception? Failure { get; private set; }

        public bool ProbeAdoptionAtMarkReady { get; set; }

        public bool AdoptionRefusedAtMarkReady { get; private set; }

        public void MarkReady()
        {

            if (ProbeAdoptionAtMarkReady)
            {

                try
                {

                    gate.AdoptDurableRecoveryOwner(
                        CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
                        scope: null,
                        cleanupOnlyHistoricalCampaign: false);

                }
                catch (InvalidOperationException)
                {

                    AdoptionRefusedAtMarkReady = true;

                }

            }

            IsReady = true;

        }

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) =>
            IsReady ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);

        public void MarkFailed(Exception exception)
        {

            Failure = exception;

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
