using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using Serilog;
using SQLitePCL;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Shared first-run Grimoire initialization: SQLCipher passphrase, key probe, and embedded SQL schema migrations (AOT-safe).
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
            ArcanumPaths.GrimoireDatabaseFile,
            ArcanumPaths.GrimoireDirectory,
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

    internal static async Task EnsureInitializedAsync(
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        IServiceScopeFactory scopeFactory,
        string dbPath,
        string grimoireDirectory,
        CancellationToken cancellationToken)
    {
        Batteries_V2.Init();

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

                Environment.FailFast("Arcanum Grimoire database key verification failed.");
            }
        }

        await using SqliteConnection migrationConnection = new(connectionString);

        await migrationConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await SqliteConnectionPragmas.ApplyAsync(migrationConnection, cancellationToken).ConfigureAwait(false);

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(migrationConnection, cancellationToken).ConfigureAwait(false);

        await EnsureWeaveSchemaAsync(migrationConnection, scopeFactory, cancellationToken).ConfigureAwait(false);

        await EnsureLexiconSchemaAsync(migrationConnection, scopeFactory, cancellationToken).ConfigureAwait(false);

        await migrationConnection.CloseAsync().ConfigureAwait(false);

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
    /// RAG Phase 1 — creates The Weave's embedding schema (see <see cref="WeaveSchemaInitializer"/>)
    /// right after Grimoire's own SQL migrations, on the same connection, before it is closed. Never
    /// fails startup: <see cref="WeaveSchemaInitializer.EnsureSchemaAsync"/> already swallows and logs
    /// its own failures, and this wrapper adds a second belt-and-suspenders catch around resolving the
    /// DI-scoped settings/logger/availability singleton themselves.
    /// </summary>
    private static async Task EnsureWeaveSchemaAsync(
        SqliteConnection migrationConnection,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {

        try
        {

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            IOptionsMonitor<ArcanumSettings> optionsMonitor =
                scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

            WeaveIndexAvailability availability = scope.ServiceProvider.GetRequiredService<WeaveIndexAvailability>();

            // WeaveSchemaInitializer is a static class and cannot be an ILogger<T> category, so the
            // logger is created by name (same effective category a typed ILogger<WeaveSchemaInitializer>
            // would have produced).
            Microsoft.Extensions.Logging.ILogger logger = scope.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("RetroDownfall.Arcanum.Infrastructure.Weave.WeaveSchemaInitializer");

            EmbeddingSettings embeddings = optionsMonitor.CurrentValue.Embeddings ?? new EmbeddingSettings();

            int dimensions = ArcanumSettingClamps.EmbeddingsDimensions(embeddings.Dimensions);

            await WeaveSchemaInitializer.EnsureSchemaAsync(
                migrationConnection,
                dimensions,
                availability,
                logger,
                cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            Log.Warning(ex, "The Weave schema bootstrap could not run; RAG features relying on it will report unavailable until this is resolved.");

        }

    }

    /// <summary>
    /// Creates The Lexicon's raw-SQL schema (<c>lexicon_entries</c> + <c>lexicon_fts</c> + sync
    /// triggers; see <see cref="LexiconSchemaInitializer"/>) right after The Weave's schema, on the
    /// same connection, before it is closed. Never fails startup: the initializer swallows and logs
    /// its own failures, and this wrapper adds a second belt-and-suspenders catch around resolving
    /// the DI-scoped logger.
    /// </summary>
    private static async Task EnsureLexiconSchemaAsync(
        SqliteConnection migrationConnection,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {

        try
        {

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            Microsoft.Extensions.Logging.ILogger logger = scope.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("RetroDownfall.Arcanum.Infrastructure.Lexicon.LexiconSchemaInitializer");

            await LexiconSchemaInitializer.EnsureSchemaAsync(migrationConnection, logger, cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            Log.Warning(ex, "The Lexicon schema bootstrap could not run; Lexicon memory features will be unavailable until this is resolved.");

        }

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

            string secret = await ResolveActiveSecretAsync(secretStore, apiKey, cancellationToken).ConfigureAwait(false);

            return DeriveWithSidecar(secret, sidecar);

        }

        if (File.Exists(dbPath))
        {

            return await UpgradeLegacyDatabaseAsync(secretStore, apiKey, dbPath, cancellationToken).ConfigureAwait(false);

        }

        return await CreateNewDatabaseSecretAsync(secretStore, dbPath, cancellationToken).ConfigureAwait(false);

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

        Environment.FailFast("Arcanum Grimoire database key verification failed.");

        throw new InvalidOperationException("Unreachable.");

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

        await using SqliteConnection rekeyConnection = new(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Password = oldPassphrase,
        }.ToString());

        await rekeyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand rekeyCommand = rekeyConnection.CreateCommand();

        rekeyCommand.CommandText = $"PRAGMA rekey = '{EscapeSqlString(newPassphrase)}';";

        await rekeyCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await rekeyConnection.CloseAsync().ConfigureAwait(false);

        GrimoireKdfSidecarFile.Write(dbPath, sidecar);

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
        ISecretStore secretStore,
        string apiKey,
        CancellationToken cancellationToken)
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

            Environment.FailFast(
                "Arcanum Grimoire encryption secret cannot be decrypted (missing Data Protection key). See logs for recovery steps.");

            throw new InvalidOperationException("Unreachable.");

        }

        return apiKey;

    }

    private static Task<SecretStoreReadResult> ReadGrimoireSecretResultAsync(ISecretStore secretStore) =>
        secretStore switch
        {
            OsKeychainSecretStore os => os.GetGrimoireEncryptionSecretReadResultAsync(),
            DataProtectionSecretStore dp => dp.GetGrimoireEncryptionSecretReadResultAsync(),
            _ => ReadGrimoireSecretResultFallbackAsync(secretStore),
        };

    private static async Task<SecretStoreReadResult> ReadGrimoireSecretResultFallbackAsync(ISecretStore secretStore)
    {

        string? value = await secretStore.GetGrimoireEncryptionSecretAsync().ConfigureAwait(false);

        return value is null
            ? SecretStoreReadResult.Missing()
            : SecretStoreReadResult.Ok(value);

    }

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
