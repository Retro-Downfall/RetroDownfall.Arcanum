using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The sole base-rebuild algorithm for the accelerator projection.
/// </summary>
/// <remarks>
/// One public method, one batch at a time. There is no whole-operation entry point on purpose: the
/// long-running-operation adapter, its checkpoint envelope, and its recovery handler belong to a
/// later plan, and a convenience "rebuild everything" method here would become a second, unbounded
/// path that no checkpoint covers.
///
/// <para>Each call rechecks the captured dataset generation, accelerator epoch, base target, and
/// core Campaign-deletion sequence inside the same immediate transaction as its batch. A reset or an
/// epoch change between batches therefore discards the unpublished partial generation and returns
/// <see cref="CovenantIndexRebuildPhase.RestartRequired"/> rather than publishing half an index.</para>
///
/// <para>No partial generation is ever eligible. The applied tuple is written once, after rank-1
/// integrity passes.</para>
/// </remarks>
internal sealed class CovenantIndexRebuilder(
    ICovenantConnectionSource connections,
    ICovenantSqliteConnectionInitializer initializer)
{

    internal CovenantIndexRebuilder(ICovenantConnectionSource connections)
        : this(connections, CovenantSqliteConnectionInitializer.Instance)
    {
    }

    public async ValueTask<Result<CovenantIndexRebuildProgress>> AdvanceBatchAsync(
        CovenantIndexRebuildProgress? progress,
        CovenantAcceleratorLease acceleratorLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(acceleratorLease);

        if (progress is { IsTerminal: true })
        {

            // Both terminal phases are idempotent: asking again returns the same answer rather than
            // restarting work whose identity is already known to be finished or stale.
            return progress;

        }

        Result revalidated = await acceleratorLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return revalidated.Error;

        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

        CovenantMutationTransaction owned = new(connection, transaction);

        using CovenantSqliteAuthorizationScope authorization = initializer.Authorize(
            connection,
            CovenantSqliteAuthorizationKind.AcceleratorSynchronization);

        RebuildState state = await ReadStateAsync(owned, cancellationToken).ConfigureAwait(false);

        Result<CovenantIndexRebuildProgress> advanced = progress is null
            ? await StartAsync(owned, state, cancellationToken).ConfigureAwait(false)
            : await ResumeAsync(owned, state, progress, cancellationToken).ConfigureAwait(false);

        if (advanced.IsFailure)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return advanced;

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return advanced;

    }

    /// <summary>
    /// The only start arm. It captures every identity at once and clears whatever the previous
    /// generation left behind, so the first batch scans against a projection it fully owns.
    /// </summary>
    private static async ValueTask<Result<CovenantIndexRebuildProgress>> StartAsync(
        CovenantMutationTransaction transaction,
        RebuildState state,
        CancellationToken cancellationToken)
    {

        await ExecuteAsync(transaction, "DELETE FROM covenant_search_documents;", cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(
                transaction,
                """
                UPDATE covenant_state
                SET AppliedDatasetGeneration = NULL,
                    AppliedSearchSequence = NULL,
                    RebuildStateCode = 3,
                    RebuildTargetSequence = $target,
                    RebuildCursor = NULL,
                    UpdatedAtUtc = $updated
                WHERE StateKey = 1;
                """,
                cancellationToken,
                ("$target", state.CanonicalSearchSequence),
                ("$updated", NowIso()))
            .ConfigureAwait(false);

        long total = await ScalarAsync(transaction, "SELECT COUNT(*) FROM covenant_heads;", cancellationToken)
            .ConfigureAwait(false);

        return new CovenantIndexRebuildProgress(
            state.DatasetGeneration,
            state.AcceleratorEpoch,
            state.CanonicalSearchSequence,
            state.CoreCampaignDeletionSequence,
            CovenantIndexRebuildPhase.BaseScan,
            BaseScanAfterSearchRowId: null,
            LastContiguousAppliedSequence: state.CanonicalSearchSequence,
            BaseHeadsProcessed: 0,
            BaseHeadsTotal: total,
            DeltaRowsProcessed: 0);

    }

    private static async ValueTask<Result<CovenantIndexRebuildProgress>> ResumeAsync(
        CovenantMutationTransaction transaction,
        RebuildState state,
        CovenantIndexRebuildProgress progress,
        CancellationToken cancellationToken)
    {

        if (state.DatasetGeneration != progress.DatasetGeneration
            || state.AcceleratorEpoch != progress.AcceleratorEpoch
            || state.RebuildStateCode != 3
            || state.RebuildTargetSequence != progress.BaseTargetSearchSequence
            || state.CoreCampaignDeletionSequence != progress.CapturedCoreCampaignDeletionSequence)
        {

            // The ground moved. The partial generation is unpublished, so discarding it costs
            // nothing; publishing it would cost correctness.
            return progress with { Phase = CovenantIndexRebuildPhase.RestartRequired };

        }

        return progress.Phase switch
        {
            CovenantIndexRebuildPhase.BaseScan =>
                await AdvanceBaseScanAsync(transaction, state, progress, cancellationToken).ConfigureAwait(false),

            CovenantIndexRebuildPhase.DeltaCatchUp =>
                await AdvanceDeltaAsync(transaction, state, progress, cancellationToken).ConfigureAwait(false),

            _ => await VerifyAsync(transaction, state, progress, cancellationToken).ConfigureAwait(false),
        };

    }

    private static async ValueTask<Result<CovenantIndexRebuildProgress>> AdvanceBaseScanAsync(
        CovenantMutationTransaction transaction,
        RebuildState state,
        CovenantIndexRebuildProgress progress,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        // Ordered by the stable projection row ID, which never moves within a generation, so a batch
        // boundary is a real position rather than a place in an unstable sort.
        command.CommandText = $"""
            INSERT INTO covenant_search_documents (
                SearchRowId, EntryId, LaneCode, VersionId, ScopeCode, CampaignId, LifecycleCode,
                NormalizedKey, AuthoredContent, CompiledContent, DatasetGeneration, CanonicalSearchSequence)
            SELECT h.SearchRowId, h.EntryId, h.LaneCode, v.VersionId, h.ScopeCode, h.CampaignId,
                   v.OperationCode, h.NormalizedKey,
                   CASE WHEN v.OperationCode = 1 THEN v.AuthoredContent ELSE NULL END,
                   CASE WHEN v.OperationCode = 1 THEN v.CompiledContent ELSE NULL END,
                   $dataset, $target
            FROM covenant_heads h
            JOIN covenant_versions v ON v.VersionId = h.CurrentVersionId
            WHERE h.SearchRowId > $after
            ORDER BY h.SearchRowId
            LIMIT {CovenantIndexRebuildProgress.BaseBatchHeads}
            ON CONFLICT (SearchRowId) DO NOTHING;
            """;

        Bind(command, "$dataset", state.DatasetGeneration.ToByteArray());

        Bind(command, "$target", progress.BaseTargetSearchSequence);

        Bind(command, "$after", progress.BaseScanAfterSearchRowId ?? 0);

        int written = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (written == 0)
        {

            return progress with { Phase = CovenantIndexRebuildPhase.DeltaCatchUp };

        }

        long cursor = await ScalarAsync(
                transaction,
                "SELECT MAX(SearchRowId) FROM covenant_search_documents;",
                cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(
                transaction,
                "UPDATE covenant_state SET RebuildCursor = $cursor, UpdatedAtUtc = $updated WHERE StateKey = 1;",
                cancellationToken,
                ("$cursor", cursor),
                ("$updated", NowIso()))
            .ConfigureAwait(false);

        return progress with
        {

            BaseScanAfterSearchRowId = cursor,

            BaseHeadsProcessed = checked(progress.BaseHeadsProcessed + written),

        };

    }

    /// <summary>
    /// Applies one contiguous post-target delta batch. Mutations to an already-passed key are
    /// recovered here rather than by rescanning: the outbox recorded them while the base scan ran.
    /// </summary>
    private static async ValueTask<Result<CovenantIndexRebuildProgress>> AdvanceDeltaAsync(
        CovenantMutationTransaction transaction,
        RebuildState state,
        CovenantIndexRebuildProgress progress,
        CancellationToken cancellationToken)
    {

        if (progress.LastContiguousAppliedSequence >= state.CanonicalSearchSequence)
        {

            return progress with { Phase = CovenantIndexRebuildPhase.Verifying };

        }

        long next = checked(progress.LastContiguousAppliedSequence + 1);

        long rows = await ScalarAsync(
                transaction,
                $"SELECT COUNT(*) FROM covenant_search_outbox WHERE SearchSequence = {next};",
                cancellationToken)
            .ConfigureAwait(false);

        if (rows == 0)
        {

            // A gap means the outbox overflowed or was truncated, so the deltas that would have
            // reconciled this generation no longer exist.
            return progress with { Phase = CovenantIndexRebuildPhase.RestartRequired };

        }

        await ExecuteAsync(
                transaction,
                """
                DELETE FROM covenant_search_documents
                WHERE SearchRowId IN (
                    SELECT SearchRowId FROM covenant_search_outbox
                    WHERE SearchSequence = $sequence AND DesiredVersionId IS NULL);
                """,
                cancellationToken,
                ("$sequence", next))
            .ConfigureAwait(false);

        await ExecuteAsync(
                transaction,
                """
                INSERT INTO covenant_search_documents (
                    SearchRowId, EntryId, LaneCode, VersionId, ScopeCode, CampaignId, LifecycleCode,
                    NormalizedKey, AuthoredContent, CompiledContent, DatasetGeneration, CanonicalSearchSequence)
                SELECT o.SearchRowId, h.EntryId, h.LaneCode, v.VersionId, h.ScopeCode, h.CampaignId,
                       v.OperationCode, h.NormalizedKey,
                       CASE WHEN v.OperationCode = 1 THEN v.AuthoredContent ELSE NULL END,
                       CASE WHEN v.OperationCode = 1 THEN v.CompiledContent ELSE NULL END,
                       $dataset, $sequence
                FROM covenant_search_outbox o
                JOIN covenant_versions v ON v.VersionId = o.DesiredVersionId
                JOIN covenant_heads h ON h.EntryId = v.EntryId AND h.LaneCode = v.LaneCode
                WHERE o.SearchSequence = $sequence AND o.DesiredVersionId IS NOT NULL
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
                """,
                cancellationToken,
                ("$dataset", state.DatasetGeneration.ToByteArray()),
                ("$sequence", next))
            .ConfigureAwait(false);

        return progress with
        {

            LastContiguousAppliedSequence = next,

            DeltaRowsProcessed = checked(progress.DeltaRowsProcessed + rows),

        };

    }

    /// <summary>
    /// Runs rank-1 integrity and publishes the complete applied tuple in one step, which is the only
    /// moment this generation becomes eligible.
    /// </summary>
    private static async ValueTask<Result<CovenantIndexRebuildProgress>> VerifyAsync(
        CovenantMutationTransaction transaction,
        RebuildState state,
        CovenantIndexRebuildProgress progress,
        CancellationToken cancellationToken)
    {

        if (progress.LastContiguousAppliedSequence != state.CanonicalSearchSequence)
        {

            return progress with { Phase = CovenantIndexRebuildPhase.DeltaCatchUp };

        }

        try
        {

            await ExecuteAsync(
                    transaction,
                    "INSERT INTO covenant_fts(covenant_fts, rank) VALUES('integrity-check', 1);",
                    cancellationToken)
                .ConfigureAwait(false);

        }
        catch (SqliteException exception)
        {

            return new Error("Covenant.IntegrityFailure", exception.Message);

        }

        await ExecuteAsync(
                transaction,
                """
                UPDATE covenant_state
                SET AppliedDatasetGeneration = $dataset,
                    AppliedSearchSequence = $sequence,
                    AppliedCampaignDeletionSequence = $deletions,
                    RebuildStateCode = 1,
                    RebuildTargetSequence = NULL,
                    RebuildCursor = NULL,
                    UpdatedAtUtc = $updated
                WHERE StateKey = 1;
                """,
                cancellationToken,
                ("$dataset", state.DatasetGeneration.ToByteArray()),
                ("$sequence", progress.LastContiguousAppliedSequence),
                ("$deletions", state.CoreCampaignDeletionSequence),
                ("$updated", NowIso()))
            .ConfigureAwait(false);

        await ExecuteAsync(
                transaction,
                "DELETE FROM covenant_search_outbox WHERE SearchSequence <= $sequence;",
                cancellationToken,
                ("$sequence", progress.LastContiguousAppliedSequence))
            .ConfigureAwait(false);

        return progress with { Phase = CovenantIndexRebuildPhase.Completed };

    }

    private static async ValueTask<RebuildState> ReadStateAsync(
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT st.DatasetGeneration, st.CanonicalSearchSequence, st.AcceleratorEpoch, st.RebuildStateCode,
                   st.RebuildTargetSequence,
                   COALESCE((SELECT MAX(Sequence) FROM owner_deletion_events WHERE OwnerKindCode = 1), 0)
            FROM covenant_state st
            WHERE st.StateKey = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new RebuildState(
            new Guid((byte[])reader.GetValue(0)),
            reader.GetInt64(1),
            checked((ulong)reader.GetInt64(2)),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetInt64(5));

    }

    private static async ValueTask<int> ExecuteAsync(
        CovenantMutationTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            Bind(command, name, value);

        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async ValueTask<long> ScalarAsync(
        CovenantMutationTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static void Bind(SqliteCommand command, string name, object value) =>
        _ = command.Parameters.AddWithValue(name, value);

    private static string NowIso() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private readonly record struct RebuildState(
        Guid DatasetGeneration,
        long CanonicalSearchSequence,
        ulong AcceleratorEpoch,
        int RebuildStateCode,
        long? RebuildTargetSequence,
        long CoreCampaignDeletionSequence);

}
