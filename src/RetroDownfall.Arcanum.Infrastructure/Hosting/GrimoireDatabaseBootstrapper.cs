using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
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

            Environment.FailFast("Arcanum Grimoire requires the master API key.");

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

        string? dedicatedSecret = await secretStore.GetGrimoireEncryptionSecretAsync().ConfigureAwait(false);

        if (!string.IsNullOrEmpty(dedicatedSecret))
        {

            return dedicatedSecret;

        }

        return apiKey;

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
