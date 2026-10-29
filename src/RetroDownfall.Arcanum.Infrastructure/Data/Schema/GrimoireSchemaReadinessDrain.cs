using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>Drains only Core predecessors required to reach an explicitly quiescent data step.</summary>
internal static class GrimoireSchemaReadinessDrain
{
    internal static async Task<bool> RequiredAsync(SqliteConnection connection,
        GrimoireSchemaVersionChainSet chains, CancellationToken cancellationToken)
    {
        GrimoireSchemaVersionChain chain = chains.ForTier(GrimoireSchemaTransactionTier.Core);

        int through = chain.Steps.Where(static step => step.Backfill is IGrimoireQuiescentBackfill)
            .Select(static step => step.ToVersion).DefaultIfEmpty(0).Max();

        if (through == 0) return false;

        GrimoireSchemaRecordedTier? recorded = await GrimoireSchemaInstaller.ReadRecordedTierAsync(
            connection, chain, cancellationToken).ConfigureAwait(false);

        GrimoireSchemaTransitionJournalRow? journal = await GrimoireSchemaTransitionJournal.ReadAsync(
            connection, null, chain.TransactionTier, cancellationToken).ConfigureAwait(false);

        if (journal is not null && GrimoireSchemaEvolutionPlanner.Decide(chain, recorded, true, journal).Action
            != GrimoireSchemaEvolutionAction.ResumeRun)
        {
            throw new InvalidOperationException("The readiness-blocking Core transition cannot be resumed by this binary.");
        }

        return (journal?.CompletedThroughVersion ?? recorded?.SchemaVersion ?? 0) < through;
    }

    internal static async Task<GrimoireSchemaInstallResult> DrainAsync(SqliteConnection connection,
        GrimoireSchemaInstaller installer, GrimoireSchemaBackfillRunner runner,
        int embeddingDimensions, GrimoireSchemaInitializationContext context,
        GrimoireSchemaQuiescentExecution execution, GrimoireSchemaInstallResult installed,
        CancellationToken cancellationToken)
    {
        GrimoireSchemaVersionChain chain = installer.Chains.ForTier(GrimoireSchemaTransactionTier.Core);

        while (await RequiredAsync(connection, installer.Chains, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            execution.Require(connection);

            GrimoireSchemaTransitionJournalRow? before = await GrimoireSchemaTransitionJournal.ReadAsync(
                connection, null, chain.TransactionTier, cancellationToken).ConfigureAwait(false);

            if (before?.BackfillName is not null)
            {
                GrimoireSchemaBackfillProgress progress = await runner.AdvanceAsync(connection, chain, before,
                    context, 16, cancellationToken, execution).ConfigureAwait(false);

                if (progress.FailureCode is not null)
                {
                    throw new InvalidOperationException("The readiness-blocking Core backfill refused its finalization.");
                }
            }

            installed = await installer.InstallAsync(connection, embeddingDimensions, context, cancellationToken).ConfigureAwait(false);

            GrimoireSchemaTransitionJournalRow? after = await GrimoireSchemaTransitionJournal.ReadAsync(
                connection, null, chain.TransactionTier, cancellationToken).ConfigureAwait(false);

            if (before == after && await RequiredAsync(connection, installer.Chains, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The readiness-blocking Core transition made no committed progress.");
            }
        }

        return installed;
    }
}
