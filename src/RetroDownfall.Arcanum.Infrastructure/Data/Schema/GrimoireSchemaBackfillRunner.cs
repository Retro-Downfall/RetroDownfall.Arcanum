using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// What one bounded pass over a pending sweep achieved.
/// </summary>
/// <remarks>
/// <paramref name="StepComplete"/> means the sweep drained and its step is durably done - either its
/// version was recorded on the journal row, or, for the last step, the whole run was finished. It
/// does <b>not</b> mean the tier reached head; a chain with further steps still needs their DDL.
/// </remarks>
internal sealed record GrimoireSchemaBackfillProgress(int BatchesRun, long RowsProcessed, bool StepComplete);

/// <summary>
/// Drains one pending sweep in bounded batches, writing each cursor inside the batch that earned it.
/// </summary>
/// <remarks>
/// The driver owns the connection, the retry policy, and how often a pass runs; this owns what a
/// batch means and what finishing one costs. That is the division the Covenant maintenance sweeps
/// already keep, and it is why a "drain everything" entry point is deliberately absent: an unbounded
/// pass is a pass no checkpoint covers.
///
/// <para>Finishing the last step goes through <see cref="GrimoireSchemaInstaller.FinalizeRunAsync"/>
/// rather than a second copy here. Two copies would be two ideas of when a version is installed, and
/// the journal has no completion flag for them to disagree through.</para>
/// </remarks>
internal sealed class GrimoireSchemaBackfillRunner(GrimoireSchemaInstaller installer, TimeProvider timeProvider)
{

    private readonly GrimoireSchemaInstaller _installer =
        installer ?? throw new ArgumentNullException(nameof(installer));

    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    internal async Task<GrimoireSchemaBackfillProgress> AdvanceAsync(
        SqliteConnection connection,
        GrimoireSchemaVersionChain chain,
        GrimoireSchemaTransitionJournalRow journal,
        GrimoireSchemaInitializationContext context,
        int maxBatches,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(chain);

        ArgumentNullException.ThrowIfNull(journal);

        ArgumentNullException.ThrowIfNull(context);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatches);

        if (!chain.TryGetStep(journal.CompletedThroughVersion, out GrimoireSchemaVersionStep step)
            || step.Backfill is null
            || !string.Equals(step.Backfill.Name, journal.BackfillName, StringComparison.Ordinal))
        {

            throw new InvalidOperationException(
                $"The {chain.TransactionTier} schema journal names a sweep this chain does not declare "
                + "for the step it is stopped at. Classification must refuse such a row rather than "
                + "hand it here.");

        }

        GrimoireSchemaTransitionJournalRow row = journal;

        int batches = 0;

        long processed = 0;

        while (batches < maxBatches)
        {

            cancellationToken.ThrowIfCancellationRequested();

            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {

                GrimoireSchemaBackfillBatch batch = await step.Backfill
                    .AdvanceBatchAsync(connection, transaction, row.BackfillCursor, cancellationToken)
                    .ConfigureAwait(false);

                batches++;

                processed += batch.RowsProcessed;

                if (batch.RowsProcessed > step.Backfill.MaxRowsPerBatch)
                {

                    // A sweep that ignores its own bound can hold one transaction open over an
                    // unbounded corpus, which is the migration this design refuses to be. Refuse the
                    // batch rather than commit the work it should not have done.
                    throw new InvalidOperationException(
                        $"The '{step.Backfill.Name}' schema backfill reported {batch.RowsProcessed} rows in one "
                        + $"batch, above its declared bound of {step.Backfill.MaxRowsPerBatch}.");

                }

                if (!batch.IsComplete)
                {

                    // The cursor is written in the same transaction as the work it describes, which is
                    // the whole of "never advance past uncommitted work": there is no ordering between
                    // the two to get wrong.
                    row = await AdvanceJournalAsync(
                        connection,
                        transaction,
                        chain,
                        row,
                        row.CompletedThroughVersion,
                        step.Backfill.Name,
                        batch.NextCursor,
                        row.BackfillRowsProcessed + batch.RowsProcessed,
                        cancellationToken).ConfigureAwait(false);

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    continue;

                }

                if (step.ToVersion == chain.HeadVersion)
                {

                    GrimoireSchemaTierInstallResult finished = await _installer
                        .FinalizeRunAsync(connection, transaction, chain, row, context, cancellationToken)
                        .ConfigureAwait(false);

                    if (finished.IsHealthy)
                    {

                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                        return new GrimoireSchemaBackfillProgress(batches, processed, StepComplete: true);

                    }

                    // The journal row is left exactly as it was, so the run is retried rather than
                    // half-recorded.
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                    return new GrimoireSchemaBackfillProgress(batches, processed, StepComplete: false);

                }

                _ = await AdvanceJournalAsync(
                    connection,
                    transaction,
                    chain,
                    row,
                    step.ToVersion,
                    backfillName: null,
                    backfillCursor: null,
                    row.BackfillRowsProcessed + batch.RowsProcessed,
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return new GrimoireSchemaBackfillProgress(batches, processed, StepComplete: true);

            }
            catch
            {

                try
                {

                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                }
                catch (Exception)
                {

                    // Best-effort: disposal rolls an uncommitted transaction back anyway, and keeping
                    // the original failure is worth more than reporting the rollback's.

                }

                throw;

            }

        }

        return new GrimoireSchemaBackfillProgress(batches, processed, StepComplete: false);

    }

    private async Task<GrimoireSchemaTransitionJournalRow> AdvanceJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaVersionChain chain,
        GrimoireSchemaTransitionJournalRow row,
        int completedThroughVersion,
        string? backfillName,
        string? backfillCursor,
        long backfillRowsProcessed,
        CancellationToken cancellationToken)
    {

        if (!await GrimoireSchemaTransitionJournal.AdvanceAsync(
                connection,
                transaction,
                row,
                completedThroughVersion,
                backfillName,
                backfillCursor,
                backfillRowsProcessed,
                _time.GetUtcNow(),
                cancellationToken).ConfigureAwait(false))
        {

            throw new InvalidOperationException(
                $"The {chain.TransactionTier} schema transition journal moved while this sweep was draining.");

        }

        return row with
        {

            CompletedThroughVersion = completedThroughVersion,

            BackfillName = backfillName,

            BackfillCursor = backfillCursor,

            BackfillRowsProcessed = backfillRowsProcessed,

            Revision = row.Revision + 1,

        };

    }

}
