using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    // Read-only association with a committed executable row. This never supplies live execution authority.
    internal static async Task ValidateStoredWorkAssociationAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestWorkIdentity historical, LongRestWorkIdentity current, CancellationToken ct)
    {
        if (CanAssociateSweepWork(historical, current)) return;

        if (historical.Scope.ProtectedPartitionDigest is not null || LongRestDiscoveryHash.Work(historical).IsFailure
            || historical with { Fence = current.Fence } != current
            || historical.Fence.AdmissionGeneration >= current.Fence.AdmissionGeneration
            || historical.Fence with { AdmissionGeneration = current.Fence.AdmissionGeneration } != current.Fence)
        {
            throw new InvalidDataException();
        }

        LongRestDiscoveryGeneration generation = await ReadSweepGenerationAsync(connection, transaction, current.OperationId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException();

        if (generation.GenerationId != current.GenerationId || generation.Scope != current.Scope || generation.Fence != current.Fence
            || LongRestDiscoveryHash.Recipe(generation.Recipe).Value != current.RecipeDigest) throw new InvalidDataException();

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT count(*) FROM long_rest_work_items
            WHERE OperationId = $operation AND GenerationId = $generation AND KindCode = $kind AND ItemId = $item
              AND WorkDigest = $work AND SourceDigest = $source AND RecipeDigest = $recipe AND DispositionCode IN (1, 2);
            """, ("$operation", current.OperationId.ToString("N")), ("$generation", Format(current.GenerationId)),
            ("$kind", (int)current.Kind), ("$item", current.ItemId), ("$work", LongRestDiscoveryHash.Work(current).Value.Bytes),
            ("$source", current.SourceDigest.Bytes), ("$recipe", current.RecipeDigest.Bytes));

        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 1) throw new InvalidDataException();
    }
}
