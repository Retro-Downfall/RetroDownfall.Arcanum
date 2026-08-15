using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using Serilog;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Shared first-run Grimoire initialization: SQLCipher passphrase, key probe, and a fresh install of
/// the declarative embedded schema (AOT-safe; no <c>Database.MigrateAsync</c>, no migration chain).
/// </summary>
public static class GrimoireDatabaseBootstrapper
{

    public static Task EnsureInitializedAsync(
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken) =>
        EnsureInitializedAsync(
            secretStore,
            passphraseSource,
            scopeFactory,
            heldInstallationLock: null,
            cancellationToken);

    /// <summary>
    /// The same bootstrap, with the caller's already-held installation lock threaded through so
    /// Covenant authority can be prepared under it.
    /// </summary>
    /// <remarks>
    /// <see cref="ArcanumMaintenanceLock"/> is internal, so this cannot be an overload of the public
    /// entry point. The lock is optional rather than required because the CLI legitimately runs
    /// alongside a host that already owns it; a null lock installs the schema and skips only the
    /// authority preparation that needs exclusive ownership.
    /// </remarks>
    internal static Task EnsureInitializedAsync(
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        IServiceScopeFactory scopeFactory,
        ArcanumMaintenanceLock? heldInstallationLock,
        CancellationToken cancellationToken) =>
        EnsureInitializedAsync(
            secretStore,
            passphraseSource,
            scopeFactory,
            ArcanumPaths.GrimoireDatabaseFile,
            ArcanumPaths.GrimoireDirectory,
            heldInstallationLock,
            cancellationToken);

    /// <summary>
    /// Runs <c>PRAGMA wal_checkpoint(TRUNCATE)</c> on a fresh connection at graceful shutdown
    /// so the <c>-wal</c>/<c>-shm</c> sidecar files do not persist across restarts. Best-effort:
    /// any failure is logged and swallowed so it never blocks shutdown.
    /// </summary>
    public static Task CheckpointOnShutdownAsync(
        IGrimoireDbPassphraseSource passphraseSource,
        CancellationToken cancellationToken) =>
        CheckpointOnShutdownAsync(passphraseSource, ArcanumPaths.GrimoireDatabaseFile, cancellationToken);

    internal static async Task CheckpointOnShutdownAsync(
        IGrimoireDbPassphraseSource passphraseSource,
        string dbPath,
        CancellationToken cancellationToken)
    {

        // W3.4 Group D #9: check the file exists BEFORE accessing the passphrase — the
        // passphrase source throws if uninitialized, and on a cold shutdown where the DB was
        // never created there is nothing to checkpoint. The passphrase access is inside the
        // try below so an uninitialized passphrase on a stray file is also handled best-effort.
        if (!File.Exists(dbPath))
        {

            return;

        }

        try
        {

            string passphrase = passphraseSource.Passphrase;

            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Password = passphrase,
            }.ToString();

            await using SqliteConnection connection = new(connectionString);

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Apply the standard pragmas so busy_timeout/synchronous match the runtime and the
            // checkpoint can wait briefly for any concurrent reader to release the WAL.
            await SqliteConnectionPragmas.ApplyAsync(connection, cancellationToken).ConfigureAwait(false);

            await using SqliteCommand checkpoint = connection.CreateCommand();

            // W3.4 Group D #9: TRUNCATE checkpoints the WAL back into the main database and
            // truncates the -wal file to zero bytes, so it does not persist across restarts.
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";

            _ = await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await connection.CloseAsync().ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            Log.Warning(ex, "Grimoire WAL checkpoint on shutdown failed for {DbPath}; continuing shutdown.", dbPath);

        }

    }

    internal static Task EnsureInitializedAsync(
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        IServiceScopeFactory scopeFactory,
        string dbPath,
        string grimoireDirectory,
        CancellationToken cancellationToken) =>
        EnsureInitializedAsync(
            secretStore,
            passphraseSource,
            scopeFactory,
            dbPath,
            grimoireDirectory,
            heldInstallationLock: null,
            cancellationToken);

    internal static async Task EnsureInitializedAsync(
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        IServiceScopeFactory scopeFactory,
        string dbPath,
        string grimoireDirectory,
        ArcanumMaintenanceLock? heldInstallationLock,
        CancellationToken cancellationToken)
    {
        SqliteNativeRuntime.Instance.Initialize();

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(grimoireDirectory);

        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {

            Log.Fatal("Grimoire startup aborted: master API key is not present. Persist a key before enabling the database.");

            throw new MissingMasterApiKeyException();

        }

        string passphrase = await ResolveGrimoirePassphraseAsync(secretStore, apiKey, dbPath, cancellationToken).ConfigureAwait(false);

        passphraseSource.SetPassphrase(passphrase);

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Password = passphrase,
        }.ToString();

        if (File.Exists(dbPath))
        {
            try
            {
                await using SqliteConnection probe = new(connectionString);

                await probe.OpenAsync(cancellationToken).ConfigureAwait(false);

                await SqliteConnectionPragmas.ApplyAsync(probe, cancellationToken).ConfigureAwait(false);

                await using SqliteCommand cmd = probe.CreateCommand();

                cmd.CommandText = "SELECT 1;";

                _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Fatal(
                    ex,
                    "Grimoire database exists at {DbPath} but could not be opened with the derived key. Possible tampering, corruption, or master key mismatch. Arcanum will exit.",
                    dbPath);

                throw new GrimoireDatabaseUnavailableException(
                    "Arcanum Grimoire database key verification failed. See logs for recovery steps.");
            }
        }

        await using SqliteConnection installConnection = new(connectionString);

        await installConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await SqliteConnectionPragmas.ApplyAsync(installConnection, cancellationToken).ConfigureAwait(false);

        await InstallSchemaAsync(
            installConnection,
            scopeFactory,
            heldInstallationLock,
            grimoireDirectory,
            apiKey,
            cancellationToken).ConfigureAwait(false);

        await installConnection.CloseAsync().ConfigureAwait(false);

        if (File.Exists(dbPath))
        {

            SecureFilePermissions.ApplyOwnerOnlyFile(dbPath);

        }

        await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
        {
            IGrimoireDbReadiness readiness = scope.ServiceProvider.GetRequiredService<IGrimoireDbReadiness>();

            readiness.MarkReady();
        }
    }

    /// <summary>
    /// Installs the complete Grimoire schema from the declarative <c>Data/Schema/</c> tree as the
    /// three independently validated transaction tiers, then publishes the outcome through
    /// <see cref="CovenantAvailability"/>.
    /// </summary>
    /// <remarks>
    /// Core is not optional, so its failure aborts startup. The two Covenant tiers fail at their own
    /// boundaries and are published as unavailable or degraded, which is why the whole
    /// <see cref="GrimoireSchemaInstallResult"/> is handed to the publisher rather than a success
    /// flag.
    ///
    /// <para><see cref="WeaveIndexAvailability"/> is set to managed-only unconditionally. The
    /// templated <c>vec0</c> accelerator tier it used to reflect no longer exists: the hermetic
    /// SQLCipher runtime ships without extension loading, so managed cosine over the durable BLOB
    /// tables is the only Divination search path and advertising anything else would be a lie
    /// (§21.2).</para>
    /// </remarks>
    private static async Task InstallSchemaAsync(
        SqliteConnection installConnection,
        IServiceScopeFactory scopeFactory,
        ArcanumMaintenanceLock? heldInstallationLock,
        string grimoireDirectory,
        string masterApiKey,
        CancellationToken cancellationToken)
    {

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        // Settings resolution runs first and owns its own failure. It used to share a try block with
        // an unrelated GetRequiredService<WeaveIndexAvailability>(), which AddArcanumGrimoireForCli
        // never registered: the throw happened before the dimension assignment, the catch swallowed
        // it, and every CLI bootstrap silently installed with the DEFAULT embedding dimension instead
        // of the configured one. Keep the two resolutions separate, and keep the optional one
        // nullable, so an absent registration can never again decide the embedding width.
        int dimensions = ResolveEmbeddingDimensions(scope);

        WeaveIndexAvailability? availability = scope.ServiceProvider.GetService<WeaveIndexAvailability>();

        GrimoireSchemaInstaller installer = scope.ServiceProvider.GetRequiredService<GrimoireSchemaInstaller>();

        GrimoireSchemaInitializationContext context = BuildInitializationContext(
            heldInstallationLock,
            grimoireDirectory,
            masterApiKey);

        GrimoireSchemaInstallResult result = await installer.InstallAsync(
            installConnection,
            dimensions,
            context,
            cancellationToken).ConfigureAwait(false);

        _ = scope.ServiceProvider
            .GetRequiredService<CovenantAvailability>()
            .PublishSchema(result, CovenantHealthTransition.Bootstrap);

        availability?.SetAvailable(false);

    }

    /// <summary>
    /// Resolves the configured embedding width, falling back to the shipped default when the options
    /// pipeline is not composed in this container.
    /// </summary>
    private static int ResolveEmbeddingDimensions(AsyncServiceScope scope)
    {

        try
        {

            IOptionsMonitor<ArcanumSettings> optionsMonitor =
                scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

            return ArcanumSettingClamps.EmbeddingsDimensions(
                optionsMonitor.CurrentValue.ResolveEmbeddings().Dimensions);

        }
        catch (Exception ex)
        {

            Log.Warning(
                ex,
                "Embedding settings could not be resolved for schema installation; installing with the default dimension.");

            return new EmbeddingSettings().Dimensions;

        }

    }

    /// <summary>
    /// Builds the installation-local facts every tier initializer runs against.
    /// </summary>
    /// <remarks>
    /// Both arms compute the same fingerprint from the same master key. The lock is what separates
    /// them: with it the caller is the sole owner of this installation and authority is prepared under
    /// it, and without it the caller is a CLI coexisting with a live host that already prepared
    /// authority, so the context is built directly rather than under an ownership claim nobody holds.
    /// </remarks>
    private static GrimoireSchemaInitializationContext BuildInitializationContext(
        ArcanumMaintenanceLock? heldInstallationLock,
        string grimoireDirectory,
        string masterApiKey)
    {

        DateTimeOffset installedAtUtc = DateTimeOffset.UtcNow;

        if (heldInstallationLock is null)
        {

            return CovenantAuthorityBootstrapper.PrepareWithoutInstallationLock(
                masterApiKey,
                installedAtUtc);

        }

        return new CovenantAuthorityBootstrapper().PrepareUnderInstallationLock(
            heldInstallationLock,
            grimoireDirectory,
            masterApiKey,
            installedAtUtc);

    }

    private static async Task<string> ResolveGrimoirePassphraseAsync(
        ISecretStore secretStore,
        string apiKey,
        string dbPath,
        CancellationToken cancellationToken)
    {

        if (GrimoireKdfSidecarFile.Exists(dbPath))
        {

            GrimoireKdfSidecar sidecar = GrimoireKdfSidecarFile.Read(dbPath);

            string secret = await ResolveActiveSecretAsync(secretStore).ConfigureAwait(false);

            return DeriveWithSidecar(secret, sidecar);

        }

        if (File.Exists(dbPath))
        {

            // A pending salt means a previous KDF upgrade was interrupted. It is written durably
            // before PRAGMA rekey, so it is the only copy of the salt if the rekey committed and
            // the promotion did not land; if the rekey never committed the database is still
            // legacy and the pending salt simply does not open it.
            string? recovered = await TryRecoverPendingKdfUpgradeAsync(secretStore, dbPath, cancellationToken).ConfigureAwait(false);

            if (recovered is not null)
            {

                return recovered;

            }

            return await UpgradeLegacyDatabaseAsync(secretStore, apiKey, dbPath, cancellationToken).ConfigureAwait(false);

        }

        return await CreateNewDatabaseSecretAsync(secretStore, dbPath, cancellationToken).ConfigureAwait(false);

    }

    private static async Task<string?> TryRecoverPendingKdfUpgradeAsync(
        ISecretStore secretStore,
        string dbPath,
        CancellationToken cancellationToken)
    {

        if (!GrimoireKdfSidecarFile.PendingExists(dbPath))
        {

            return null;

        }

        GrimoireKdfSidecar pending;

        try
        {

            pending = GrimoireKdfSidecarFile.ReadPending(dbPath);

        }
        catch (Exception ex)
        {

            Log.Warning(
                ex,
                "A pending Grimoire KDF salt exists at {PendingPath} but could not be read; falling back to the legacy upgrade path.",
                GrimoireKdfSidecarFile.GetPendingSidecarPath(dbPath));

            return null;

        }

        string? secret = await secretStore.GetGrimoireEncryptionSecretAsync().ConfigureAwait(false);

        if (string.IsNullOrEmpty(secret))
        {

            return null;

        }

        string candidate = DeriveWithSidecar(secret, pending);

        if (!await CanOpenDatabaseAsync(dbPath, candidate, cancellationToken).ConfigureAwait(false))
        {

            // The interrupted rekey never committed; the database is still legacy and the pending
            // salt is stale. UpgradeLegacyDatabaseAsync re-drives the upgrade with a fresh salt.
            return null;

        }

        GrimoireKdfSidecarFile.PromotePending(dbPath);

        Log.Warning(
            "A previously interrupted Grimoire KDF upgrade was completed: the pending PBKDF2 salt opened {DbPath} and was promoted to the committed sidecar.",
            dbPath);

        return candidate;

    }

    private static async Task<string> UpgradeLegacyDatabaseAsync(
        ISecretStore secretStore,
        string apiKey,
        string dbPath,
        CancellationToken cancellationToken)
    {

        string? dedicatedSecret = await secretStore.GetGrimoireEncryptionSecretAsync().ConfigureAwait(false);

        if (!string.IsNullOrEmpty(dedicatedSecret))
        {

            string legacyPassphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecretLegacy(dedicatedSecret);

            if (await CanOpenDatabaseAsync(dbPath, legacyPassphrase, cancellationToken).ConfigureAwait(false))
            {

                return await RekeyToPbkdf2Async(secretStore, dedicatedSecret, legacyPassphrase, dbPath, cancellationToken).ConfigureAwait(false);

            }

        }

        string legacyApiPassphrase = GrimoireKeyDerivation.DerivePassphraseFromApiKeyLegacy(apiKey);

        if (await CanOpenDatabaseAsync(dbPath, legacyApiPassphrase, cancellationToken).ConfigureAwait(false))
        {

            string newDedicatedSecret = await GenerateAndSaveDedicatedSecretAsync(secretStore).ConfigureAwait(false);

            return await RekeyToPbkdf2Async(secretStore, newDedicatedSecret, legacyApiPassphrase, dbPath, cancellationToken).ConfigureAwait(false);

        }

        Log.Fatal(
            "Grimoire database at {DbPath} exists but could not be opened with either the legacy dedicated secret or the master API key.",
            dbPath);

        throw new GrimoireDatabaseUnavailableException(
            "Arcanum Grimoire database key verification failed. See logs for recovery steps.");

    }

    private static async Task<string> RekeyToPbkdf2Async(
        ISecretStore secretStore,
        string secret,
        string oldPassphrase,
        string dbPath,
        CancellationToken cancellationToken)
    {

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        string newPassphrase = DeriveWithSidecar(secret, sidecar);

        // PRAGMA rekey is irreversible, so the salt must already be on durable storage before it
        // runs — otherwise a crash, a full disk, or a read-only config directory between the rekey
        // and the sidecar write destroys the only copy of the salt and bricks the database.
        // The pending file is promoted to the committed sidecar once the rekey has committed;
        // ResolveGrimoirePassphraseAsync recovers from either side of that window on next start.
        GrimoireKdfSidecarFile.WritePending(dbPath, sidecar);

        await using (SqliteConnection rekeyConnection = new(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Password = oldPassphrase,
        }.ToString()))
        {

            await rekeyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand rekeyCommand = rekeyConnection.CreateCommand();

            rekeyCommand.CommandText = $"PRAGMA rekey = '{EscapeSqlString(newPassphrase)}';";

            await rekeyCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await rekeyConnection.CloseAsync().ConfigureAwait(false);

        }

        GrimoireKdfSidecarFile.PromotePending(dbPath);

        Log.Information("Grimoire database upgraded to PBKDF2 KDF (version 2).");

        return newPassphrase;

    }

    private static async Task<string> CreateNewDatabaseSecretAsync(
        ISecretStore secretStore,
        string dbPath,
        CancellationToken cancellationToken)
    {

        string newSecret = await GenerateAndSaveDedicatedSecretAsync(secretStore).ConfigureAwait(false);

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        GrimoireKdfSidecarFile.Write(dbPath, sidecar);

        return DeriveWithSidecar(newSecret, sidecar);

    }

    private static async Task<string> ResolveActiveSecretAsync(
        ISecretStore secretStore)
    {

        SecretStoreReadResult dedicated = await ReadGrimoireSecretResultAsync(secretStore).ConfigureAwait(false);

        if (dedicated.Status == SecretStoreReadStatus.Ok
            && !string.IsNullOrEmpty(dedicated.Value))
        {

            return dedicated.Value;

        }

        if (dedicated.Status == SecretStoreReadStatus.Corrupted)
        {

            // Sidecar-backed databases are keyed from the dedicated secret. Falling back to the
            // API key here yields a wrong passphrase and a confusing "key verification failed"
            // FailFast — surface the real cause (missing/corrupt Data Protection key material).
            Log.Fatal(
                "Grimoire encryption secret store is present but cannot be decrypted ({Message}). "
                + "The Data Protection key that sealed grimoire-key.dat is missing from ~/.config/arcanum/keys/. "
                + "Restore the matching key-*.xml from backup, or reset the Grimoire (delete arcanum.db, arcanum.db.kdf under ~/.config/arcanum/, and grimoire-key.dat under the Application Support arcanum folder) to start fresh — session data is otherwise unrecoverable.",
                dedicated.Message ?? "unknown");

            throw new GrimoireDatabaseUnavailableException(
                "Arcanum Grimoire encryption secret cannot be decrypted (missing Data Protection key). See logs for recovery steps.");

        }

        // Same reasoning for an absent secret: every sidecar-backed database was keyed from the
        // dedicated secret (CreateNewDatabaseSecretAsync / RekeyToPbkdf2Async / backup restore),
        // never from the master API key, so PBKDF2(apiKey, salt) can only produce the misleading
        // "key verification failed". Point the operator at the file that is actually missing.
        Log.Fatal(
            "Grimoire encryption secret grimoire-key.dat is missing while a KDF sidecar exists. "
            + "The database was keyed from the dedicated Grimoire secret, not the master API key, so it cannot be opened without it. "
            + "Restore grimoire-key.dat (it lives under the Application Support arcanum folder on macOS/Windows, not beside arcanum.db) together with the matching key-*.xml from ~/.config/arcanum/keys/, "
            + "run 'arcanum backup restore' against a verified .arcbackup generation, or reset the Grimoire (delete arcanum.db and arcanum.db.kdf) to start fresh — session data is otherwise unrecoverable.");

        throw new GrimoireDatabaseUnavailableException(
            "Arcanum Grimoire encryption secret (grimoire-key.dat) is missing. See logs for recovery steps.");

    }

    private static Task<SecretStoreReadResult> ReadGrimoireSecretResultAsync(
        ISecretStore secretStore) =>
        secretStore.GetGrimoireEncryptionSecretReadResultAsync();

    private static async Task<string> GenerateAndSaveDedicatedSecretAsync(ISecretStore secretStore)
    {

        byte[] secretBytes = new byte[32];

        RandomNumberGenerator.Fill(secretBytes);

        string newSecret = Convert.ToBase64String(secretBytes);

        CryptographicOperations.ZeroMemory(secretBytes);

        await secretStore.SaveGrimoireEncryptionSecretAsync(newSecret).ConfigureAwait(false);

        return newSecret;

    }

    private static string DeriveWithSidecar(string secret, GrimoireKdfSidecar sidecar)
    {

        byte[] salt = sidecar.GetSaltBytes();

        try
        {

            return sidecar.Version switch
            {

                GrimoireKeyDerivation.KdfVersion2 => GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(secret, salt),

                _ => throw new NotSupportedException($"Grimoire KDF version {sidecar.Version} is not supported."),

            };

        }
        finally
        {

            CryptographicOperations.ZeroMemory(salt);

        }

    }

    private static async Task<bool> CanOpenDatabaseAsync(
        string dbPath,
        string passphrase,
        CancellationToken cancellationToken)
    {

        if (!File.Exists(dbPath))
        {

            return false;

        }

        try
        {

            await using SqliteConnection probe = new(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Password = passphrase,
            }.ToString());

            await probe.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand cmd = probe.CreateCommand();

            cmd.CommandText = "SELECT 1;";

            _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            await probe.CloseAsync().ConfigureAwait(false);

            return true;

        }
        catch (Exception)
        {

            return false;

        }

    }

    private static string EscapeSqlString(string value)
    {

        return value.Replace("'", "''");

    }

}
