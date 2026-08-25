using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// What one synchronization pass applied.
/// </summary>
public sealed record CovenantOutboxSyncOutcome(
    long AppliedSearchSequence,
    long RowsConsumed,
    long ProjectionsWritten,
    long ProjectionsRemoved,
    bool RebuildRequired);

/// <summary>
/// The single writer that carries canonical outbox deltas into the accelerator projection.
/// </summary>
/// <remarks>
/// It processes a contiguous range and never a partial sequence. Applying half of one canonical
/// commit would leave the applied tuple claiming a sequence whose projection is incomplete, and the
/// eligibility check would then approve stale text.
///
/// <para>Every effect here is derived. A canonical mutation succeeded before this worker ran, so a
/// failure at any point rolls back only accelerator state and leaves the canonical tier correct.</para>
/// </remarks>
internal sealed class CovenantSearchOutboxWorker(ICovenantSqliteConnectionInitializer initializer)
{

    internal const int DefaultBatchRows = 512;

    internal CovenantSearchOutboxWorker()
        : this(CovenantSqliteConnectionInitializer.Instance)
    {
    }

    /// <summary>
    /// Carries one bounded contiguous range of outbox rows into the accelerator projection.
    /// </summary>
    /// <remarks>
    /// The bound is stated by the caller rather than defaulted, for the same reason the cleanup
    /// worker's is: it decides how much of a backlog one pass drains, and a default nobody overrides
    /// hides that decision inside the worker. <see cref="DefaultBatchRows"/> is the value a caller
    /// with no reason to choose otherwise states.
    /// </remarks>
    public async ValueTask<Result<CovenantOutboxSyncOutcome>> SynchronizeAsync(
        CovenantAcceleratorLease acceleratorLease,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken,
        int maxRows)
    {

        ArgumentNullException.ThrowIfNull(acceleratorLease);

        ArgumentNullException.ThrowIfNull(transaction);

        if (maxRows < 1)
        {

            throw new ArgumentOutOfRangeException(nameof(maxRows));

        }

        Result revalidated = await acceleratorLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return revalidated.Error;

        }

        using CovenantSqliteAuthorizationScope authorization = initializer.Authorize(
            transaction.Connection,
            CovenantSqliteAuthorizationKind.AcceleratorSynchronization);

        AcceleratorState state = await ReadStateAsync(transaction, cancellationToken).ConfigureAwait(false);

        if (acceleratorLease.Snapshot.DatasetGeneration is { } expected && expected != state.DatasetGeneration)
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The Covenant dataset generation changed before this synchronization batch could apply.");

        }

        if (acceleratorLease.Snapshot.AcceleratorEpoch is { } epoch && epoch != state.AcceleratorEpoch)
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The Covenant accelerator epoch changed before this synchronization batch could apply.");

        }

        long? applied = state.AppliedDatasetGeneration == state.DatasetGeneration
            ? state.AppliedSearchSequence
            : null;

        if (applied is null)
        {

            // A projection with no published tuple is adoptable only while it is empty: an empty
            // projection is trivially correct for sequence zero, so the worker can start from there
            // instead of demanding a rebuild of nothing. Anything already projected under a tuple
            // this dataset never published cannot be reconciled by a delta.
            if (await ProjectionRowCountAsync(transaction, cancellationToken).ConfigureAwait(false) > 0)
            {

                return new CovenantOutboxSyncOutcome(0, 0, 0, 0, RebuildRequired: true);

            }

            applied = 0;

        }

        ImmutableArray<OutboxRow> pending = await ReadPendingAsync(transaction, applied.Value, maxRows + 1, cancellationToken)
            .ConfigureAwait(false);

        if (pending.IsEmpty)
        {

            // Nothing to apply, but the tuple may still need adopting so eligibility can be reached
            // on an installation that has simply never mutated.
            if (state.AppliedDatasetGeneration != state.DatasetGeneration)
            {

                await PublishAppliedAsync(transaction, state.DatasetGeneration, applied.Value, cancellationToken)
                    .ConfigureAwait(false);

            }

            return new CovenantOutboxSyncOutcome(applied.Value, 0, 0, 0, RebuildRequired: false);

        }

        // Whole sequences only. A range that stopped mid-sequence would publish an applied tuple for
        // a canonical commit that is only half projected.
        long target = ChooseContiguousTarget(pending, applied.Value, maxRows);

        if (target <= applied.Value)
        {

            return new CovenantOutboxSyncOutcome(applied.Value, 0, 0, 0, RebuildRequired: pending.Length > maxRows);

        }

        ImmutableArray<OutboxRow> range = [.. pending.Where(row => row.SearchSequence <= target)];

        // Coalescing within the range keeps only the last delta per projection row, so a head that
        // changed three times in one batch is written once.
        Dictionary<long, OutboxRow> coalesced = [];

        foreach (OutboxRow row in range)
        {

            coalesced[row.SearchRowId] = row;

        }

        long written = 0;

        long removed = 0;

        foreach (OutboxRow row in coalesced.Values.OrderBy(static row => row.SearchRowId))
        {

            if (row.DesiredVersionId is null)
            {

                removed = checked(removed + await DeleteProjectionAsync(transaction, row.SearchRowId, cancellationToken)
                    .ConfigureAwait(false));

                continue;

            }

            Result<bool> projected = await WriteProjectionAsync(
                    transaction,
                    row,
                    state.DatasetGeneration,
                    target,
                    cancellationToken)
                .ConfigureAwait(false);

            if (projected.IsFailure)
            {

                return projected.Error;

            }

            if (!projected.Value)
            {

                // The desired version is gone and no later absent delta covers it inside this range,
                // so nothing here can reconcile the projection. Advance nothing.
                return new CovenantOutboxSyncOutcome(applied.Value, 0, 0, 0, RebuildRequired: true);

            }

            written = checked(written + 1);

        }

        long consumed = await ConsumeAsync(transaction, target, cancellationToken).ConfigureAwait(false);

        await PublishAppliedAsync(transaction, state.DatasetGeneration, target, cancellationToken)
            .ConfigureAwait(false);

        return new CovenantOutboxSyncOutcome(target, consumed, written, removed, RebuildRequired: false);

    }

    /// <summary>
    /// The highest sequence whose rows fit entirely inside the batch bound.
    /// </summary>
    private static long ChooseContiguousTarget(ImmutableArray<OutboxRow> pending, long applied, int maxRows)
    {

        long target = applied;

        long counted = 0;

        foreach (IGrouping<long, OutboxRow> group in pending.GroupBy(static row => row.SearchSequence)
            .OrderBy(static group => group.Key))
        {

            long size = group.Count();

            if (checked(counted + size) > maxRows)
            {

                break;

            }

            counted += size;

            target = group.Key;

        }

        return target;

    }

    private static async ValueTask<long> ProjectionRowCountAsync(
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM covenant_search_documents;";

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async ValueTask<AcceleratorState> ReadStateAsync(
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT DatasetGeneration, CanonicalSearchSequence, AppliedDatasetGeneration, AppliedSearchSequence,
                   AcceleratorEpoch
            FROM covenant_state
            WHERE StateKey = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new AcceleratorState(
            new Guid((byte[])reader.GetValue(0)),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : new Guid((byte[])reader.GetValue(2)),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            checked((ulong)reader.GetInt64(4)));

    }

    private static async ValueTask<ImmutableArray<OutboxRow>> ReadPendingAsync(
        CovenantMutationTransaction transaction,
        long appliedSequence,
        int limit,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId
            FROM covenant_search_outbox
            WHERE SearchSequence > $applied
            ORDER BY SearchSequence, Ordinal
            LIMIT $limit;
            """;

        _ = command.Parameters.AddWithValue("$applied", appliedSequence);

        _ = command.Parameters.AddWithValue("$limit", limit);

        List<OutboxRow> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            rows.Add(
                new OutboxRow(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));

        }

        return [.. rows];

    }

    /// <summary>
    /// Writes one projection row from its desired immutable version. Returns false when that version
    /// no longer exists, which is a rebuild signal rather than an error.
    /// </summary>
    private static async ValueTask<Result<bool>> WriteProjectionAsync(
        CovenantMutationTransaction transaction,
        OutboxRow row,
        Guid datasetGeneration,
        long searchSequence,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        // A current tombstone indexes its key and lifecycle only. Retaining content on a retired head
        // would let search return text the operator asked to remove.
        command.CommandText = """
            INSERT INTO covenant_search_documents (
                SearchRowId, EntryId, LaneCode, VersionId, ScopeCode, CampaignId, LifecycleCode,
                NormalizedKey, AuthoredContent, CompiledContent, DatasetGeneration, CanonicalSearchSequence)
            SELECT $row, h.EntryId, h.LaneCode, v.VersionId, h.ScopeCode, h.CampaignId, v.OperationCode,
                   h.NormalizedKey,
                   CASE WHEN v.OperationCode = 1 THEN v.AuthoredContent ELSE NULL END,
                   CASE WHEN v.OperationCode = 1 THEN v.CompiledContent ELSE NULL END,
                   $dataset, $sequence
            FROM covenant_versions v
            JOIN covenant_heads h ON h.EntryId = v.EntryId AND h.LaneCode = v.LaneCode
            WHERE v.VersionId = $version
            ON CONFLICT (SearchRowId) DO UPDATE SET
                EntryId = excluded.EntryId,
                LaneCode = excluded.LaneCode,
                VersionId = excluded.VersionId,
                ScopeCode = excluded.ScopeCode,
                CampaignId = excluded.CampaignId,
                LifecycleCode = excluded.LifecycleCode,
                NormalizedKey = excluded.NormalizedKey,
                AuthoredContent = excluded.AuthoredContent,
                CompiledContent = excluded.CompiledContent,
                DatasetGeneration = excluded.DatasetGeneration,
                CanonicalSearchSequence = excluded.CanonicalSearchSequence;
            """;

        _ = command.Parameters.AddWithValue("$row", row.SearchRowId);

        _ = command.Parameters.AddWithValue("$version", row.DesiredVersionId!);

        _ = command.Parameters.AddWithValue("$dataset", datasetGeneration.ToByteArray());

        _ = command.Parameters.AddWithValue("$sequence", searchSequence);

        try
        {

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

        }
        catch (SqliteException exception)
        {

            return new Error(ErrorCodes.Covenant.IntegrityFailure, exception.Message);

        }

    }

    private static async ValueTask<int> DeleteProjectionAsync(
        CovenantMutationTransaction transaction,
        long searchRowId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = "DELETE FROM covenant_search_documents WHERE SearchRowId = $row;";

        _ = command.Parameters.AddWithValue("$row", searchRowId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async ValueTask<long> ConsumeAsync(
        CovenantMutationTransaction transaction,
        long target,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = "DELETE FROM covenant_search_outbox WHERE SearchSequence <= $target;";

        _ = command.Parameters.AddWithValue("$target", target);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async ValueTask PublishAppliedAsync(
        CovenantMutationTransaction transaction,
        Guid datasetGeneration,
        long target,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        // The applied tuple moves as one unit, including the Campaign-deletion watermark: a partial
        // tuple would let a stale generation pass an equality check against a fresh sequence.
        command.CommandText = """
            UPDATE covenant_state
            SET AppliedDatasetGeneration = $dataset,
                AppliedSearchSequence = $target,
                AppliedCampaignDeletionSequence = COALESCE(
                    (SELECT MAX(Sequence) FROM owner_deletion_events WHERE OwnerKindCode = 1), 0),
                UpdatedAtUtc = $updated
            WHERE StateKey = 1;
            """;

        _ = command.Parameters.AddWithValue("$dataset", datasetGeneration.ToByteArray());

        _ = command.Parameters.AddWithValue("$target", target);

        _ = command.Parameters.AddWithValue(
            "$updated",
            DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private readonly record struct AcceleratorState(
        Guid DatasetGeneration,
        long CanonicalSearchSequence,
        Guid? AppliedDatasetGeneration,
        long? AppliedSearchSequence,
        ulong AcceleratorEpoch);

    private readonly record struct OutboxRow(
        long SearchSequence,
        int Ordinal,
        long SearchRowId,
        string EntryId,
        int LaneCode,
        string? DesiredVersionId);

}
