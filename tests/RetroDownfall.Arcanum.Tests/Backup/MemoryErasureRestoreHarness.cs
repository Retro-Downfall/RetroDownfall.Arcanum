using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// One installation that erases through the host and is then restored over by the production restore
/// service, with one in-memory keychain shared by both.
/// </summary>
/// <remarks>
/// <para>The profile's <see cref="InMemoryOsCredentialStore"/> is the only keychain. Every host started
/// here runs on it, through <see cref="MemoryErasureRouteDriver.Host"/>, and every restore service built
/// here reads the erasure key from it unless a test hands in a wrapper around it. So the key an erase
/// created is the key a restore finds, exactly as it would be in the OS store, and no test reaches the
/// real keychain.</para>
///
/// <para>Hosts run one at a time and are disposed before the next step: the restore takes the
/// maintenance lock a running host holds, and an archive or a digest taken under a running host would
/// race its pooled connections. Every precondition goes through production writers: Saga memories
/// through the store's insert, fingerprints only through an actual erase, and archives through the
/// production backup service.</para>
/// </remarks>
internal sealed class MemoryErasureRestoreHarness : IAsyncDisposable
{
    /// <summary>The recovery passphrase every archive here is written and read with.</summary>
    internal const string Passphrase = "memory erasure restore passphrase";

    private ArcanumWebApplicationFactory? _host;

    private MemoryErasureRestoreHarness(RestartableArcanumProfileFixture profile)
    {
        Profile = profile;

        Credentials = (InMemoryOsCredentialStore)profile.CredentialStore;
    }

    internal RestartableArcanumProfileFixture Profile { get; }

    /// <summary>Where <see cref="ArcanumWebApplicationFactory"/> places this profile's Grimoire.</summary>
    internal string InstallationRoot => Path.Combine(Profile.TempHome, ".config", "arcanum");

    internal string DatabasePath => Path.Combine(InstallationRoot, "arcanum.db");

    /// <summary>The profile's keychain: the only one any host or restore service here reads.</summary>
    internal InMemoryOsCredentialStore Credentials { get; }

    /// <summary>The running host, which every host-phase step requires.</summary>
    internal ArcanumWebApplicationFactory Host =>
        _host ?? throw new InvalidOperationException("Start a host before a host-phase step.");

    internal static Task<MemoryErasureRestoreHarness> CreateAsync() =>
        Task.FromResult(new MemoryErasureRestoreHarness(new RestartableArcanumProfileFixture()));

    /// <summary>
    /// Core at version 11 beside Covenant canonical at version 5: an archive older than both tiers this
    /// build declares, whose Core tier reaches head only through a sweep-bearing step.
    /// </summary>
    internal static GrimoireSchemaVersionChainSet CoreElevenCanonicalFive() =>
        new(
        [
            CoreSchemaVersionElevenFixture.ChainSet().ForTier(GrimoireSchemaTransactionTier.Core),
            CovenantCanonicalSchemaVersionFiveFixture.ChainSet().ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
        ]);

    /// <summary>
    /// Starts a host on this profile with Saga, embeddings and the fixed-vector weave on, and the
    /// Covenant as asked. The first host seeds the Grimoire.
    /// </summary>
    internal ArcanumWebApplicationFactory StartHost(bool covenant = false)
    {
        if (_host is not null)
        {
            throw new InvalidOperationException("A host is already running on this profile.");
        }

        _host = MemoryErasureRouteDriver.Host(Credentials, Profile, covenant);

        _ = _host.Services;

        return _host;
    }

    /// <summary>Stops and disposes the running host, if any, and releases its pooled connections.</summary>
    internal async Task StopHostAsync()
    {
        if (_host is not { } host)
        {
            return;
        }

        _host = null;

        await host.DisposeAsync();

        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Writes one Saga memory through the store's insert. No Session means Global scope; a Campaign
    /// scope comes from a Session bound to that Campaign.
    /// </summary>
    internal Task<string> InsertSagaAsync(string content, Guid? sessionId = null) =>
        MemoryErasureRouteDriver.InsertSagaAsync(Host, content, sessionId);

    /// <summary>Erases one Saga memory through the routes, which creates the key on a first erase.</summary>
    internal Task<MemoryErasureRoundTrip<SagaEraseRequest>> EraseSagaAsync(string memoryId) =>
        new MemoryErasureRouteDriver(Host.CreateClient()).EraseSagaAsync(memoryId);

    /// <summary>Archives this installation through the production backup service, with no host running.</summary>
    /// <returns>The archive's path.</returns>
    internal async Task<string> CreateArchiveAsync(string name)
    {
        RequireStopped();

        string archives = Directory.CreateDirectory(Path.Combine(Profile.TempHome, "archives")).FullName;

        BackupStatePaths paths = Paths();

        BackupService backups = new(
            paths,
            new BackupInventoryPlanner(paths),
            new BackupDatabaseSnapshotter(),
            Codec(),
            new FixedSecretSnapshotReader(GrimoireFixture.TestGrimoireSecret),
            TimeProvider.System);

        BackupCreateResult created = await backups.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(BackupScope.Full, SessionId: null, Include: [], Exclude: []),
                Path.Combine(archives, name),
                Overwrite: true),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, created.Status);

        return created.ArchivePath!;
    }

    /// <summary>
    /// Archives a separate installation, with its own Grimoire secret and key-derivation sidecar, whose
    /// three tiers <paramref name="chains"/> installs and whose rows <paramref name="seed"/> writes.
    /// </summary>
    /// <remarks>
    /// This build has no writer for an older catalog, so an older archive's rows are written with SQL
    /// through the columns that catalog declares. The archive still goes through the production backup
    /// service, so a restore reads it exactly as it reads one this installation took.
    /// </remarks>
    /// <returns>The archive's path, and the secret its Grimoire, and so the restored Grimoire, opens with.</returns>
    internal async Task<ArchivedInstallation> CreateArchiveAtAsync(
        string name,
        GrimoireSchemaVersionChainSet chains,
        Func<SqliteConnection, Task> seed)
    {
        ArgumentNullException.ThrowIfNull(chains);

        ArgumentNullException.ThrowIfNull(seed);

        string root = Path.Combine(Profile.TempHome, "sources", name);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(root);

        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        string database = Path.Combine(root, "arcanum.db");

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        GrimoireKdfSidecarFile.Write(database, sidecar);

        byte[] salt = sidecar.GetSaltBytes();

        string passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(secret, salt);

        CryptographicOperations.ZeroMemory(salt);

        await using (SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Password = passphrase,
                Pooling = false,
            }.ToString(),
            CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 1536, CancellationToken.None);

            await seed(connection);
        }

        string archives = Directory.CreateDirectory(Path.Combine(Profile.TempHome, "archives")).FullName;

        BackupStatePaths paths = new(
            root,
            root,
            Path.Combine(root, "audit.jsonl"),
            Path.Combine(root, "guardrails.jsonl"));

        BackupService backups = new(
            paths,
            new BackupInventoryPlanner(paths),
            new BackupDatabaseSnapshotter(),
            Codec(),
            new FixedSecretSnapshotReader(secret),
            TimeProvider.System);

        BackupCreateResult created = await backups.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(BackupScope.Full, SessionId: null, Include: [], Exclude: []),
                Path.Combine(archives, name + BackupArchiveFormat.Extension),
                Overwrite: true),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, created.Status);

        return new ArchivedInstallation(created.ArchivePath!, secret);
    }

    /// <summary>
    /// Opens the live Grimoire read-only with <paramref name="grimoireSecret"/>: this profile's own, or,
    /// after a restore committed it, the secret of the installation the archive was taken from.
    /// </summary>
    internal Task<SqliteConnection> OpenLiveDatabaseAsync(string grimoireSecret)
    {
        RequireStopped();

        return BackupRestoreDatabaseWorker.OpenAsync(DatabasePath, grimoireSecret, readOnly: true, CancellationToken.None);
    }

    /// <summary>The SHA-256 of the live database file's bytes, in hex, with no pooled handle open on it.</summary>
    internal async Task<string> LiveDatabaseDigestAsync()
    {
        RequireStopped();

        SqliteConnection.ClearAllPools();

        await using FileStream database = new(DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        return Convert.ToHexString(await SHA256.HashDataAsync(database));
    }

    /// <summary>
    /// The production restore service over this installation, reading the erasure key from
    /// <paramref name="credentials"/> or the profile's keychain through a fresh keyring of its own.
    /// </summary>
    internal BackupRestoreService CreateRestoreService(
        IOsCredentialStore? credentials = null,
        ISecretStore? secrets = null,
        BackupRestoreServiceOptions? options = null,
        GrimoireSchemaInstaller? installer = null,
        Func<IBackupService>? safetyBackups = null) =>
        new(
            Paths(),
            Codec(),
            secrets ?? new FixedGrimoireSecretStore(GrimoireFixture.TestGrimoireSecret),
            safetyBackups,
            TimeProvider.System,
            installer ?? GrimoireSchemaTestInstaller.Create(),
            new MemoryErasureKeyring(credentials ?? Credentials),
            options);

    /// <summary>
    /// The Covenant arm a restore runs with the gate on, behind <paramref name="gate"/> and over this
    /// profile's keychain, for <see cref="BackupRestoreServiceOptions.RestoreStaging"/>.
    /// </summary>
    internal CovenantRestoreStagingServices CovenantStaging(ICovenantOperationGate gate) =>
        new(
            gate,
            new CovenantRestoreStagingTests.RecordingRestoreMarkerLifecycle(),
            new BackupRestoreJournalAnchorStore(
                Credentials,
                new BackupRestoreJournalKeyProvider(Credentials),
                new BackupRestoreJournalInstallationIdentityProvider(Credentials)),
            new BackupRestoreJournalInstallationIdentityProvider(Credentials),
            new BackupRestoreJournalKeyProvider(Credentials),
            new BackupRestoreEffectDigestCalculator());

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopHostAsync();
        }
        finally
        {
            await Profile.DisposeAsync();
        }
    }

    private BackupStatePaths Paths() => new(
        InstallationRoot,
        InstallationRoot,
        Path.Combine(InstallationRoot, "audit.jsonl"),
        Path.Combine(InstallationRoot, "guardrails.jsonl"));

    private static BackupArchiveCodec Codec() =>
        new(new BackupArchiveCodecOptions
        {
            KdfIterations = 10_000,
            ChunkSize = 64 * 1024,
        });

    private void RequireStopped()
    {
        if (_host is not null)
        {
            throw new InvalidOperationException("Stop the host before archiving, restoring or reading the live database.");
        }
    }

    /// <summary>An archive of a separate installation, and the Grimoire secret it was taken under.</summary>
    internal sealed record ArchivedInstallation(string ArchivePath, string GrimoireSecret);

    /// <summary>A secret store that serves one Grimoire secret, or none when it is null, and nothing else.</summary>
    /// <remarks>
    /// Every write is accepted and dropped, the file-encryption key ring included, so a restore that
    /// commits can rebuild local secret protection without this store ever serving anything new.
    /// </remarks>
    internal sealed class FixedGrimoireSecretStore(string? grimoireSecret) : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult(grimoireSecret);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

        public Task SaveFileEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    /// <summary>Serves the archive the Grimoire secret the live database is keyed from.</summary>
    private sealed class FixedSecretSnapshotReader(string grimoireSecret) : IBackupSecretSnapshotReader
    {
        public Task<SecretStoreReadResult> ReadGrimoireSecretAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(grimoireSecret));

        public Task<SecretStoreReadResult> ReadFileEncryptionKeysAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

        public Task<SecretStoreReadResult> ReadMasterApiKeyAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("archived-master-key"));
    }
}
