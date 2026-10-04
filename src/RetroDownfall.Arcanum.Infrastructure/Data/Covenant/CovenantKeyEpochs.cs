using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The two epochs one normalized key carries, read together.
/// </summary>
/// <remarks>
/// <see cref="Dependency"/> is <c>covenant_key_epochs.KeyEpoch</c>. It advances whenever a head for
/// the key appears, changes, or is removed, in any scope or lane, and it is what a prepared change is
/// compared against to learn whether the key moved underneath it.
///
/// <para><see cref="Binding"/> is <c>covenant_key_epochs.IncarnationEpoch</c>. It is fixed when the
/// key's epoch row is created and never moves afterwards, and it is what a curation head or version
/// records, so a pin or a mask survives every ordinary write to the key it curates.</para>
///
/// <para>A key with no epoch row reads as zero for both. That is what lets a pin or a mask be recorded
/// before the key's first head: the row that head creates starts its binding epoch at zero too.</para>
/// </remarks>
internal readonly record struct CovenantKeyEpochPair(long Dependency, long Binding);

/// <summary>
/// Reads a key's epoch pair inside the caller's write transaction.
/// </summary>
/// <remarks>
/// One statement rather than two, so the pair describes one row at one instant. Every writer that
/// validates a dependency epoch and then records a binding epoch reads both here, which is what keeps
/// the curation kernel and the review service from each deciding separately which column means which.
/// </remarks>
internal static class CovenantKeyEpochs
{
    internal static async ValueTask<CovenantKeyEpochPair> ReadAsync(
        CovenantMutationTransaction transaction,
        string normalizedKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentException.ThrowIfNullOrEmpty(normalizedKey);

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT COALESCE(MAX(KeyEpoch), 0), COALESCE(MAX(IncarnationEpoch), 0)
            FROM covenant_key_epochs
            WHERE NormalizedKey = $key;
            """;

        _ = command.Parameters.AddWithValue("$key", normalizedKey);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        // An aggregate with no GROUP BY always yields exactly one row, so a key with no epoch row is
        // the pair of zeroes rather than an empty result.
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new CovenantKeyEpochPair(reader.GetInt64(0), reader.GetInt64(1));
    }
}
