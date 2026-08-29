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
    /// Builds the workload's options, enrolling every connection it opens with <paramref name="drain"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="drain"/> is null only where there is no host to drain into: the design-time
    /// factory and <see cref="ArcanumDbContext.OnConfiguring"/>'s fallback for a context somebody
    /// constructed by hand. Every composition that registers this context also registers the drain,
    /// and passes it, because a Covenant erasure has to be able to close the handles that
    /// composition opens.
    /// </remarks>
    public static void Configure(
        DbContextOptionsBuilder optionsBuilder,
        IGrimoireDbPassphraseSource passphraseSource,
        ICovenantConnectionDrain? drain)
    {

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ArcanumPaths.GrimoireDatabaseFile,
            Password = passphraseSource.Passphrase,
        }.ToString();

        _ = optionsBuilder
            .UseSqlite(connectionString)
            .UseModel(ArcanumDbContextModel.Instance)
            .AddInterceptors(SqlitePragmaConnectionInterceptor.Instance);

        if (drain is not null)
        {

            _ = optionsBuilder.AddInterceptors(new CovenantConnectionEnrolmentInterceptor(drain));

        }

    }

}
