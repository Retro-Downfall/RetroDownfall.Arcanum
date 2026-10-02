using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>What one staged drain took: its passes, and the sweep batches and rows across them.</summary>
/// <remarks>Zero passes means the staged generation was already at every head with nothing journaled.</remarks>
internal sealed record BackupRestoreSchemaDrainReceipt(int Passes, int BatchesRun, long RowsProcessed);

/// <summary>
/// Drains a staged generation through every sweep-bearing step of every tier, so a restore can join this
/// installation's erasure evidence against a generation that is at every head.
/// </summary>
/// <remarks>
/// <para>An older archive's migration stops at the first step that carries a sweep, and the host finishes
/// that sweep after commit. That is enough for a restore with nothing to apply. It is not enough for one
/// whose destination holds erasure evidence: the evidence tables, and the store shapes the evidence is
/// matched against, exist only at head, so the staged copy is drained there before anything is joined to
/// it. This is the one place outside the host that drains a sweep. The copy is private to the restore,
/// which holds the maintenance lock throughout, so no reader or verb waits behind it.</para>
///
/// <para>Each pass advances every pending sweep by at most <see cref="MaxBatchesPerPass"/> batches through
/// the runner the host drives, then re-enters convergence so the next step's DDL runs, exactly as the
/// host's coordinator does. A sweep's work and its cursor commit together, so a pass that stops anywhere
/// leaves the staged copy at a state the journal describes, never one a later step could mistake for
/// drained. A pass that moves no journal row and no tier version is a stall: the next pass would do
/// exactly the same, so the drain refuses rather than loop.</para>
///
/// <para>Every way the drain can fail is one typed refusal, raised before the safety backup and before
/// anything is displaced. No refusal carries an exception's message: a sweep's diagnostic can describe
/// rows, and the refusal reaches the operator. It names the exception's type, or, for a stall or a
/// refused tier, the tier health that stopped it.</para>
/// </remarks>
internal static class BackupRestoreSchemaDrain
{
    /// <summary>The batch bound handed to the runner for each pending sweep in one pass.</summary>
    internal const int MaxBatchesPerPass = 64;

    /// <summary>
    /// Drains <paramref name="staged"/> until every tier is at head and the transition journal is empty,
    /// starting from what <paramref name="migrated"/> reported.
    /// </summary>
    /// <returns>
    /// A receipt, or <see cref="BackupRestoreErasureCodes.EvidenceUnjoinable"/> when a tier is refused, a
    /// pass throws, a pass makes no progress, or the drain is cancelled.
    /// </returns>
    internal static async Task<Result<BackupRestoreSchemaDrainReceipt>> DrainAsync(
        SqliteConnection staged,
        GrimoireSchemaInstaller installer,
        GrimoireSchemaBackfillRunner runner,
        GrimoireSchemaInstallResult migrated,
        int embeddingDimensions,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);

        ArgumentNullException.ThrowIfNull(installer);

        ArgumentNullException.ThrowIfNull(runner);

        ArgumentNullException.ThrowIfNull(migrated);

        ArgumentNullException.ThrowIfNull(context);

        int passes = 0;

        int batches = 0;

        long rows = 0;

        try
        {
            GrimoireSchemaInstallResult tiers = migrated;

            IReadOnlyList<GrimoireSchemaTransitionJournalRow> journal =
                await ReadJournalAsync(staged, cancellationToken).ConfigureAwait(false);

            while (!IsAtHead(tiers, journal))
            {
                if (RefusedHealth(tiers) is { } refused)
                {
                    return Unjoinable(refused.ToString());
                }

                Progress before = Progress.Of(tiers, journal);

                passes++;

                foreach (GrimoireSchemaTransitionJournalRow row in journal)
                {
                    if (row.BackfillName is null)
                    {
                        // A step whose DDL has not run yet; convergence below runs it.
                        continue;
                    }

                    GrimoireSchemaBackfillProgress advanced = await runner
                        .AdvanceAsync(
                            staged,
                            installer.Chains.ForTier(row.TransactionTier),
                            row,
                            context,
                            MaxBatchesPerPass,
                            cancellationToken)
                        .ConfigureAwait(false);

                    batches += advanced.BatchesRun;

                    rows += advanced.RowsProcessed;
                }

                tiers = await installer
                    .InstallAsync(staged, embeddingDimensions, context, cancellationToken)
                    .ConfigureAwait(false);

                journal = await ReadJournalAsync(staged, cancellationToken).ConfigureAwait(false);

                if (!IsAtHead(tiers, journal) && Progress.Of(tiers, journal).SameAs(before))
                {
                    return Unjoinable((RefusedHealth(tiers) ?? FirstUnhealthy(tiers)).ToString());
                }
            }

            return new BackupRestoreSchemaDrainReceipt(passes, batches, rows);
        }
        catch (Exception exception)
        {
            // A throw, a Core refusal and a cancellation all land here. Each leaves the staged copy at a
            // state its journal describes, because every batch and every step commits with its record,
            // and none of it is ever published as live.
            return Unjoinable(exception.GetType().Name);
        }
    }

    /// <summary>The refusal for a staged generation that cannot be brought to every head.</summary>
    /// <param name="diagnostics">An exception's type name or a tier health, never a message.</param>
    internal static Error Unjoinable(string diagnostics) =>
        new(
            BackupRestoreErasureCodes.EvidenceUnjoinable,
            "The archive's database could not be brought to this build's schema head, so this installation's "
            + "erasure evidence cannot be applied. Nothing was displaced. Restore a newer archive, or run a full "
            + "installation reset. Diagnostics: " + diagnostics);

    private static bool IsAtHead(
        GrimoireSchemaInstallResult tiers,
        IReadOnlyList<GrimoireSchemaTransitionJournalRow> journal) =>
        journal.Count == 0
        && tiers.Core.IsHealthy
        && tiers.CovenantCanonical.IsHealthy
        && tiers.CovenantAccelerator.IsHealthy;

    /// <summary>
    /// The first tier health no further pass can change, or null when every unhealthy tier is only
    /// waiting: on its own sweep, or on a tier it depends on.
    /// </summary>
    private static GrimoireSchemaTierHealth? RefusedHealth(GrimoireSchemaInstallResult tiers)
    {
        foreach (GrimoireSchemaTierInstallResult tier in (GrimoireSchemaTierInstallResult[])[tiers.Core, tiers.CovenantCanonical, tiers.CovenantAccelerator])
        {
            if (tier.Health is not (
                GrimoireSchemaTierHealth.Healthy
                or GrimoireSchemaTierHealth.TransitionIncomplete
                or GrimoireSchemaTierHealth.DependencyUnavailable))
            {
                return tier.Health;
            }
        }

        return null;
    }

    private static GrimoireSchemaTierHealth FirstUnhealthy(GrimoireSchemaInstallResult tiers) =>
        !tiers.Core.IsHealthy ? tiers.Core.Health
        : !tiers.CovenantCanonical.IsHealthy ? tiers.CovenantCanonical.Health
        : !tiers.CovenantAccelerator.IsHealthy ? tiers.CovenantAccelerator.Health
        : GrimoireSchemaTierHealth.TransitionIncomplete;

    /// <summary>
    /// Reads every in-flight run, tolerating a catalog old enough not to have the journal, which can
    /// therefore have nothing in flight.
    /// </summary>
    private static async Task<IReadOnlyList<GrimoireSchemaTransitionJournalRow>> ReadJournalAsync(
        SqliteConnection staged,
        CancellationToken cancellationToken) =>
        await BackupRestoreDatabaseWorker
            .TableExistsAsync(staged, "grimoire_schema_transitions", cancellationToken)
            .ConfigureAwait(false)
            ? await GrimoireSchemaTransitionJournal.ReadAllAsync(staged, cancellationToken).ConfigureAwait(false)
            : [];

    /// <summary>One journal row's progress: everything a pass can move except its revision.</summary>
    /// <remarks>
    /// The revision is left out on purpose. The runner advances it on every batch, so a sweep that hands
    /// back its own cursor with no rows would look like progress forever.
    /// </remarks>
    private readonly record struct ProgressKey(
        GrimoireSchemaTransactionTier Tier,
        int CompletedThroughVersion,
        string? BackfillName,
        string? BackfillCursor,
        long BackfillRowsProcessed);

    /// <summary>What a pass can move: every journal row's progress and the three tier versions.</summary>
    private sealed record Progress(IReadOnlyList<ProgressKey> Rows, int Core, int Canonical, int Accelerator)
    {
        internal static Progress Of(
            GrimoireSchemaInstallResult tiers,
            IReadOnlyList<GrimoireSchemaTransitionJournalRow> journal) =>
            new(
                [
                    .. journal.Select(static row => new ProgressKey(
                        row.TransactionTier,
                        row.CompletedThroughVersion,
                        row.BackfillName,
                        row.BackfillCursor,
                        row.BackfillRowsProcessed)),
                ],
                tiers.Core.SchemaVersion,
                tiers.CovenantCanonical.SchemaVersion,
                tiers.CovenantAccelerator.SchemaVersion);

        internal bool SameAs(Progress other) =>
            Core == other.Core
            && Canonical == other.Canonical
            && Accelerator == other.Accelerator
            && Rows.SequenceEqual(other.Rows);
    }
}
