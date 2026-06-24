using Microsoft.EntityFrameworkCore;
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

        int sessionCount = await db.Sessions.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);

        int entryCount = await db.Entries.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);

        int campaignCount = await db.Campaigns.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);

        return new GrimoireStatsDto(databaseBytes, walBytes, sessionCount, entryCount, campaignCount);

    }

}
