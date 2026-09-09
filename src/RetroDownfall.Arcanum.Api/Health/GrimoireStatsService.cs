using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Api.Health;

public sealed class GrimoireStatsService(ArcanumDbContext db)
{
    public async Task<GrimoireStatsDto> GetStatsAsync(CancellationToken cancellationToken)
    {
        string databasePath = ArcanumPaths.GrimoireDatabaseFile;

        long databaseBytes = 0;

        if (File.Exists(databasePath))
        {
            databaseBytes = new FileInfo(databasePath).Length;
        }

        string walPath = databasePath + "-wal";

        long walBytes = 0;

        if (File.Exists(walPath))
        {
            walBytes = new FileInfo(walPath).Length;
        }

        await using SqliteCommand counts = await GrimoireSqlCommandFactory.CreateAsync(
            db,
            """
            SELECT
                (SELECT COUNT(*) FROM "Sessions"),
                (SELECT COUNT(*) FROM "Entries"),
                (SELECT COUNT(*) FROM "Campaigns");
            """,
            cancellationToken).ConfigureAwait(false);

        await using SqliteDataReader reader = await counts
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Grimoire statistics query returned no row.");
        }

        int sessionCount = checked((int)reader.GetInt64(0));

        int entryCount = checked((int)reader.GetInt64(1));

        int campaignCount = checked((int)reader.GetInt64(2));

        return new GrimoireStatsDto(databaseBytes, walBytes, sessionCount, entryCount, campaignCount);
    }
}
