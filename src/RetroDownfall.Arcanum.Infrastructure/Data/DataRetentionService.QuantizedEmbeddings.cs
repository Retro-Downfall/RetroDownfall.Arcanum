using System.Data.Common;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class DataRetentionService
{
    private async Task<long> CountEmbeddingCopiesAsync(string table, string? predicate,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters) =>
        await CountTableAsync(table, predicate, cancellationToken, parameters).ConfigureAwait(false)
        + await CountTableAsync(table + "_quantized", predicate, cancellationToken, parameters).ConfigureAwait(false);

    private async Task<long> CountEmbeddingIdSetAsync(string table, string column, IReadOnlyList<string> ids,
        CancellationToken cancellationToken) =>
        await CountIdSetAsync(table, column, ids, cancellationToken).ConfigureAwait(false)
        + await CountIdSetAsync(table + "_quantized", column, ids, cancellationToken).ConfigureAwait(false);

    private static async Task<long> CountEmbeddingCopiesInTransactionAsync(DbConnection connection, DbTransaction transaction,
        string table, string? predicate, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) =>
        await CountInTransactionAsync(connection, transaction, table, predicate, cancellationToken, parameters).ConfigureAwait(false)
        + await CountInTransactionAsync(connection, transaction, table + "_quantized", predicate, cancellationToken, parameters).ConfigureAwait(false);

    /// <summary>Removes and counts both copies within the caller's guarded writer transaction.</summary>
    private static async Task<int> DeleteEmbeddingCopiesAsync(DbConnection connection, DbTransaction transaction,
        string table, string predicate, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        bool hasCompact = await QuantizedEmbeddingStore.IsAvailableAsync(connection, transaction, table,
            cancellationToken).ConfigureAwait(false);

        long compact = hasCompact
            ? await CountInTransactionAsync(connection, transaction, table + "_quantized", predicate,
                cancellationToken, parameters).ConfigureAwait(false)
            : 0;

        if (compact > 0)
        {
            // Supported maintenance also runs with foreign keys disabled. Remove the same selected
            // compact copies explicitly; a later parent refusal rolls this delete back with its owner.
            _ = await ExecuteAsync(connection, transaction, $"DELETE FROM {table}_quantized WHERE {predicate}",
                cancellationToken, parameters).ConfigureAwait(false);
        }

        long parent = await ExecuteAsync(connection, transaction, $"DELETE FROM {table} WHERE {predicate}",
            cancellationToken, parameters).ConfigureAwait(false);

        // Count the measured compact copies once regardless of the connection's foreign-key mode.
        // Parent deletion still runs through its original guards in the same transaction.
        return checked((int)(parent + compact));
    }
}
