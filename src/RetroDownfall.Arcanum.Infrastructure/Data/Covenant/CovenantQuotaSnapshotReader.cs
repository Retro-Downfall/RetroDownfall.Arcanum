using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Turns one <see cref="CovenantStoreSql.QuotaSnapshot"/> row into the snapshot both capacity callers compare against.
/// </summary>
/// <remarks>
/// The column order is the contract between the statement and this reader, and it is easy to break
/// silently: every column is a count, so transposing two of them still parses and still returns
/// plausible numbers — it just compares the wrong one to the wrong ceiling. Reading the row in one
/// place keeps that order stated once, which is the same reason the statement itself is shared.
/// </remarks>
internal static class CovenantQuotaSnapshotReader
{

    internal static async ValueTask<CovenantQuotaSnapshot> ReadAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new CovenantQuotaSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9));

    }

}
