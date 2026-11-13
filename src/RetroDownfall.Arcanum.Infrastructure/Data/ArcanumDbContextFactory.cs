using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

[ExcludeFromCodeCoverage] // Reason: EF design-time factory, not runtime logic
public sealed class ArcanumDbContextFactory : IDesignTimeDbContextFactory<ArcanumDbContext>
{
    private static readonly Lazy<bool> DesignTimeNativeRuntime =
        new(InitializeDesignTimeNativeRuntime, LazyThreadSafetyMode.ExecutionAndPublication);

    [RequiresAssemblyFiles(
        "This EF Core factory is a design-time-only scratch-database path and must never be constructed by the published Native AOT runtime.")]
    public ArcanumDbContextFactory()
    {
    }

    public ArcanumDbContext CreateDbContext(string[] args)
    {
        _ = DesignTimeNativeRuntime.Value;

        string devKey = Environment.GetEnvironmentVariable("ARCANUM_GRIMOIRE_DEV_KEY")
            ?? "compile-time-placeholder-not-for-production";
        GrimoireDbPassphraseSource passphraseSource = new();
        passphraseSource.SetPassphrase(GrimoireKeyDerivation.DerivePassphraseFromApiKeyLegacy(devKey));
        string dbPath = Path.Combine(Path.GetTempPath(), "arcanum-ef-design.db");
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Password = passphraseSource.Passphrase,
        }.ToString();
        DbContextOptionsBuilder<ArcanumDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlite(connectionString);
        return new ArcanumDbContext(optionsBuilder.Options, DesignTimeSecretStore.Instance, passphraseSource);
    }

    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000",
        Justification = "EF invokes this design-time-only factory from the ordinary target assembly, never from Arcanum's published process.")]
    private static bool InitializeDesignTimeNativeRuntime()
    {
        string assemblyDirectory = Path.GetDirectoryName(typeof(ArcanumDbContextFactory).Assembly.Location)
            ?? throw new InvalidOperationException("The design-time Infrastructure assembly has no directory.");

        SqliteNativeRuntime.Instance.InitializeForDesignTime(assemblyDirectory);

        return true;
    }

    private sealed class DesignTimeSecretStore : ISecretStore
    {
        public static readonly DesignTimeSecretStore Instance = new();
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }
}
