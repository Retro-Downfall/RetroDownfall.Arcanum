using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>What one staged drain took: its passes, and the sweep batches and rows across them.</summary>
/// <remarks>Zero passes means nothing was pending: the journal was empty and every tier recorded at head.</remarks>
internal sealed record BackupRestoreSchemaDrainReceipt(int Passes, int BatchesRun, long RowsProcessed);

/// <summary>
/// Drains a staged generation through every sweep-bearing step of every tier, so a restore can join this
/// installation's erasure evidence against a generation that is recorded at every head.
/// </summary>
/// <remarks>
/// <para>An older archive's migration stops at the first step that carries a sweep, and the host finishes
/// that sweep after commit. That is enough for a restore with nothing to apply. It is not enough for one
/// whose destination holds erasure evidence: the evidence tables, and the store shapes the evidence is
/// matched against, exist only at head, so the staged copy is drained there before anything is joined to
/// it. This is the one place outside the host that drains a sweep. The copy is private to the restore,
/// which holds the maintenance lock throughout, so no live reader waits behind it.</para>
///
/// <para>A drain is needed exactly when the transition journal has a row or a tier is recorded below this
/// build's head, a tier recorded at no version included. A tier recorded at head needs none, whatever its
/// health: no pass can repair a drifted or otherwise degraded catalog, so such a tier is left to the
/// evidence step's own verification rather than refused here.</para>
///
/// <para>Each pass advances every pending sweep by at most <see cref="MaxBatchesPerPass"/> batches through
/// the runner the host drives, then re-enters convergence so the next step's DDL runs, exactly as the
/// host's coordinator does. A sweep's work and its cursor commit together, so a pass that stops anywhere
/// leaves the staged copy at a state the journal describes, never one a later step could mistake for
/// drained. A pass that moves no journal row and no recorded tier version is a stall: the next pass would
/// do exactly the same, so the drain refuses rather than loop. There is no other bound. The drain takes
/// the archive's whole pending corpus, and the runner checks the caller's token before every batch.</para>
///
/// <para>Every way the drain can fail is one typed refusal, raised before the safety backup and before
/// anything is displaced: a tier below head that reports a health no pass can change, a throw, or a
/// stall. No refusal carries an exception's message: a sweep's diagnostic can describe rows, and the
/// refusal reaches the operator. It names the exception's type, or, for a stall or a refused tier, the
/// tier health that stopped it. The caller cancelling is not a failure of the drain: it propagates as the
/// cancellation it is, exactly as it does from the migration before it.</para>
/// </remarks>
internal static class BackupRestoreSchemaDrain
{
    /// <summary>The batch bound handed to the runner for each pending sweep in one pass.</summary>
    internal const int MaxBatchesPerPass = 64;

    /// <summary>Every tier the drain answers for, in install order.</summary>
    private static readonly GrimoireSchemaTransactionTier[] Tiers =
    [
        GrimoireSchemaTransactionTier.Core,
        GrimoireSchemaTransactionTier.CovenantCanonical,
        GrimoireSchemaTransactionTier.CovenantAccelerator,
    ];

    /// <summary>
    /// Drains <paramref name="staged"/> until the transition journal is empty and every tier is recorded
    /// at head, starting from the tier healths <paramref name="migrated"/> reported.
    /// </summary>
    /// <returns>
    /// A receipt, or <see cref="BackupRestoreErasureCodes.EvidenceUnjoinable"/> when a tier below head is
    /// refused, a pass throws, or a pass makes no progress.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
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

            Position position = await Position.ReadAsync(staged, installer.Chains, cancellationToken)
                .ConfigureAwait(false);

            while (!position.IsAtHead)
            {
                if (RefusedHealth(tiers, position) is { } refused)
                {
                    return Unjoinable(refused.ToString());
                }

                passes++;

                foreach (GrimoireSchemaTransitionJournalRow row in position.Journal)
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

                Position after = await Position.ReadAsync(staged, installer.Chains, cancellationToken)
                    .ConfigureAwait(false);

                if (!after.IsAtHead && after.SameProgressAs(position))
                {
                    return Unjoinable((RefusedHealth(tiers, after) ?? WaitingHealth(tiers, after)).ToString());
                }

                position = after;
            }

            return new BackupRestoreSchemaDrainReceipt(passes, batches, rows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The operator stopped the restore. That says nothing about the archive, and nothing has been
            // displaced, so it leaves as the cancellation it is.
            throw;
        }
        catch (Exception exception)
        {
            // A throw and a Core refusal land here, and so does a cancellation nobody asked for. Each
            // leaves the staged copy at a state its journal describes, because every batch and every step
            // commits with its record, and none of it is ever published as live.
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

    /// <summary>
    /// The first health no further pass can change, reported by a tier the drain still has to move, or
    /// null when every such tier is only waiting: on its own sweep, or on a tier it depends on.
    /// </summary>
    /// <remarks>A tier already recorded at head is never consulted, whatever its health.</remarks>
    private static GrimoireSchemaTierHealth? RefusedHealth(GrimoireSchemaInstallResult tiers, Position position)
    {
        foreach (GrimoireSchemaTransactionTier tier in Tiers)
        {
            GrimoireSchemaTierHealth health = HealthOf(tiers, tier);

            if (!position.IsRecordedAtHead(tier)
                && health is not (
                    GrimoireSchemaTierHealth.Healthy
                    or GrimoireSchemaTierHealth.TransitionIncomplete
                    or GrimoireSchemaTierHealth.DependencyUnavailable))
            {
                return health;
            }
        }

        return null;
    }

    /// <summary>What the first tier still below head is waiting on, for a stall's diagnostic.</summary>
    private static GrimoireSchemaTierHealth WaitingHealth(GrimoireSchemaInstallResult tiers, Position position)
    {
        foreach (GrimoireSchemaTransactionTier tier in Tiers)
        {
            if (!position.IsRecordedAtHead(tier))
            {
                return HealthOf(tiers, tier);
            }
        }

        return GrimoireSchemaTierHealth.TransitionIncomplete;
    }

    private static GrimoireSchemaTierHealth HealthOf(GrimoireSchemaInstallResult tiers, GrimoireSchemaTransactionTier tier) =>
        tier switch
        {
            GrimoireSchemaTransactionTier.Core => tiers.Core.Health,
            GrimoireSchemaTransactionTier.CovenantCanonical => tiers.CovenantCanonical.Health,
            GrimoireSchemaTransactionTier.CovenantAccelerator => tiers.CovenantAccelerator.Health,
            _ => throw new ArgumentOutOfRangeException(nameof(tier)),
        };

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

    /// <summary>
    /// Where the staged copy stands between passes: its in-flight runs, and the version each tier's
    /// metadata records against the head this build declares for it.
    /// </summary>
    private sealed class Position
    {
        private readonly Dictionary<GrimoireSchemaTransactionTier, int?> _recorded;

        private readonly Dictionary<GrimoireSchemaTransactionTier, int> _heads;

        private Position(
            IReadOnlyList<GrimoireSchemaTransitionJournalRow> journal,
            Dictionary<GrimoireSchemaTransactionTier, int?> recorded,
            Dictionary<GrimoireSchemaTransactionTier, int> heads)
        {
            Journal = journal;

            _recorded = recorded;

            _heads = heads;
        }

        internal IReadOnlyList<GrimoireSchemaTransitionJournalRow> Journal { get; }

        /// <summary>Nothing in flight, and every tier recorded at head.</summary>
        internal bool IsAtHead => Journal.Count == 0 && Tiers.All(IsRecordedAtHead);

        internal bool IsRecordedAtHead(GrimoireSchemaTransactionTier tier) =>
            _recorded[tier] == _heads[tier];

        /// <summary>Whether a pass moved nothing: no journal row and no recorded tier version.</summary>
        internal bool SameProgressAs(Position before) =>
            Tiers.All(tier => _recorded[tier] == before._recorded[tier])
            && Keys().SequenceEqual(before.Keys());

        internal static async Task<Position> ReadAsync(
            SqliteConnection staged,
            GrimoireSchemaVersionChainSet chains,
            CancellationToken cancellationToken)
        {
            // Both tables are part of every catalog this build can classify; a catalog old enough to lack
            // them has nothing in flight and nothing recorded.
            IReadOnlyList<GrimoireSchemaTransitionJournalRow> journal = await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "grimoire_schema_transitions", cancellationToken)
                .ConfigureAwait(false)
                ? await GrimoireSchemaTransitionJournal.ReadAllAsync(staged, cancellationToken).ConfigureAwait(false)
                : [];

            bool metadata = await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "grimoire_feature_schemas", cancellationToken)
                .ConfigureAwait(false);

            Dictionary<GrimoireSchemaTransactionTier, int?> recorded = [];

            Dictionary<GrimoireSchemaTransactionTier, int> heads = [];

            foreach (GrimoireSchemaTransactionTier tier in Tiers)
            {
                GrimoireSchemaVersionChain chain = chains.ForTier(tier);

                heads[tier] = chain.HeadVersion;

                recorded[tier] = metadata
                    ? await ReadRecordedVersionAsync(staged, chain, cancellationToken).ConfigureAwait(false)
                    : null;
            }

            return new Position(journal, recorded, heads);
        }

        private static async Task<int?> ReadRecordedVersionAsync(
            SqliteConnection staged,
            GrimoireSchemaVersionChain chain,
            CancellationToken cancellationToken)
        {
            await using SqliteCommand command = staged.CreateCommand();

            command.CommandText = """
                SELECT SchemaVersion
                FROM grimoire_feature_schemas
                WHERE FamilyCode = $familyCode AND TransactionTierCode = $tierCode;
                """;

            _ = command.Parameters.AddWithValue("$familyCode", (long)chain.Family);

            _ = command.Parameters.AddWithValue("$tierCode", (long)chain.TransactionTier);

            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private IEnumerable<ProgressKey> Keys() =>
            Journal.Select(static row => new ProgressKey(
                row.TransactionTier,
                row.CompletedThroughVersion,
                row.BackfillName,
                row.BackfillCursor,
                row.BackfillRowsProcessed));
    }
}
