using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Shared EF Core options for <see cref="ArcanumDbContext"/>. Must run in DI
/// <c>AddDbContext</c>/<c>AddDbContextPool</c> callbacks — pooling forbids modifying
/// options from <see cref="ArcanumDbContext.OnConfiguring"/>.
/// </summary>
internal static class ArcanumDbContextOptionsConfigurator
{

    /// <summary>
    /// Builds one serving workload's options with the host-wide admission and drain singletons.
    /// </summary>
    public static void Configure(
        DbContextOptionsBuilder optionsBuilder,
        IGrimoireDbPassphraseSource passphraseSource,
        IGrimoireOrdinaryConnectionLifecycle lifecycle,
        ICovenantConnectionDrain drain,
        ICovenantSqliteConnectionInitializer initializer)
    {

        ArgumentNullException.ThrowIfNull(lifecycle);

        ArgumentNullException.ThrowIfNull(drain);

        ArgumentNullException.ThrowIfNull(initializer);

        ConfigureProvider(optionsBuilder, passphraseSource);

        _ = optionsBuilder.AddInterceptors(
            new CovenantConnectionEnrolmentInterceptor(
                lifecycle,
                drain,
                initializer));

    }

    /// <summary>
    /// Configures the manual/design-time fallback that has no serving host or maintenance owner.
    /// </summary>
    internal static void ConfigureNonServingFallback(
        DbContextOptionsBuilder optionsBuilder,
        IGrimoireDbPassphraseSource passphraseSource)
    {

        ConfigureProvider(optionsBuilder, passphraseSource);

        _ = optionsBuilder.AddInterceptors(SqlitePragmaConnectionInterceptor.Instance);

    }

    private static void ConfigureProvider(
        DbContextOptionsBuilder optionsBuilder,
        IGrimoireDbPassphraseSource passphraseSource)
    {

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ArcanumPaths.GrimoireDatabaseFile,
            Password = passphraseSource.Passphrase,
        }.ToString();

        _ = optionsBuilder
            .UseSqlite(connectionString)
            .UseModel(ArcanumDbContextModel.Instance);

    }

}
