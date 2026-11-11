using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    private static async Task<bool> MarketRootMatchesWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohort, Guid root, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM guild_cohorts WHERE Id=$cohort AND RootQuestId=$root)");
        Add(command, "$cohort", Id(cohort));
        Add(command, "$root", Id(root));
        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }
}
