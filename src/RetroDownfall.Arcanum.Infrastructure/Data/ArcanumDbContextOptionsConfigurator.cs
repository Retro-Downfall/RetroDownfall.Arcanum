using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
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

    public static void Configure(
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
            .UseModel(ArcanumDbContextModel.Instance)
            .AddInterceptors(SqlitePragmaConnectionInterceptor.Instance);

    }

}
