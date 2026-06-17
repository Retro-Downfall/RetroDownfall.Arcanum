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

    public static async Task EnsureInitializedAsync(
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        Batteries_V2.Init();

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {

            Log.Fatal("Grimoire startup aborted: master API key is not present. Persist a key before enabling the database.");

            Environment.FailFast("Arcanum Grimoire requires the master API key.");

        }

        string dbPath = ArcanumPaths.GrimoireDatabaseFile;

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

        string? dedicatedSecret = await secretStore.GetGrimoireEncryptionSecretAsync().ConfigureAwait(false);

        if (!string.IsNullOrEmpty(dedicatedSecret))
        {

            return GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(dedicatedSecret);

        }

        if (!File.Exists(dbPath))
        {

            byte[] secretBytes = new byte[32];

            RandomNumberGenerator.Fill(secretBytes);

            string newSecret = Convert.ToBase64String(secretBytes);

            await secretStore.SaveGrimoireEncryptionSecretAsync(newSecret).ConfigureAwait(false);

            return GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(newSecret);

        }

        return GrimoireKeyDerivation.DerivePassphraseFromApiKey(apiKey);

    }

}
