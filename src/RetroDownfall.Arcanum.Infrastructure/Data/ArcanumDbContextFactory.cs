using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Security;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

public sealed class ArcanumDbContextFactory : IDesignTimeDbContextFactory<ArcanumDbContext>
{
    public ArcanumDbContext CreateDbContext(string[] args)
    {
        Batteries_V2.Init();
        // MSBuild compiled-model generation runs without user env; `dotnet ef` should set ARCANUM_GRIMOIRE_DEV_KEY explicitly.
        string devKey = Environment.GetEnvironmentVariable("ARCANUM_GRIMOIRE_DEV_KEY")
            ?? "compile-time-placeholder-not-for-production";
        GrimoireDbPassphraseSource passphraseSource = new();
        passphraseSource.SetPassphrase(GrimoireKeyDerivation.DerivePassphraseFromApiKey(devKey));
        string dbPath = Path.Combine(Path.GetTempPath(), "arcanum-ef-design.db");
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Password = passphraseSource.Passphrase,
        }.ToString();
        DbContextOptionsBuilder<ArcanumDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlite(connectionString);
        optionsBuilder.UseModel(ArcanumDbContextModel.Instance);
        return new ArcanumDbContext(optionsBuilder.Options, DesignTimeSecretStore.Instance, passphraseSource);
    }

    private sealed class DesignTimeSecretStore : ISecretStore
    {
        public static readonly DesignTimeSecretStore Instance = new();
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);
        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;
    }
}
