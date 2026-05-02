using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

public sealed class GrimoireDatabaseHostedService(
    IServiceScopeFactory scopeFactory,
    ISecretStore secretStore,
    IGrimoireDbPassphraseSource passphraseSource)
    : IHostedService
{
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "EnsureCreatedAsync is RequiresDynamicCode; used only for first-run empty-database bootstrap with the compiled EF model—migrations and design-time model builds are not executed here.")]

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Batteries_V2.Init();
        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);
        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Fatal("Grimoire startup aborted: master API key is not present. Persist a key before enabling the database.");
            Environment.FailFast("Arcanum Grimoire requires the master API key.");
        }
        string passphrase = GrimoireKeyDerivation.DerivePassphraseFromApiKey(apiKey);
        passphraseSource.SetPassphrase(passphrase);
        string dbPath = ArcanumPaths.GrimoireDatabaseFile;
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
        else
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();
            await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
