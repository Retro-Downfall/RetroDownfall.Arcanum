using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// What one bounded pass over the in-flight schema runs achieved.
/// </summary>
/// <remarks>
/// <paramref name="Advanced"/> distinguishes "there was work and some of it was done" from "there was
/// nothing to do", which is the difference between a pass worth repeating soon and one worth waiting
/// on. It is not an error either way: an installation with no run in flight is the ordinary case.
/// </remarks>
internal sealed record GrimoireSchemaTransitionPassOutcome(
    bool Advanced,
    int RunsSeen,
    int BatchesRun,
    long RowsProcessed,
    int RunsAdvanced,
    int RunsPending,
    bool Deferred);

/// <summary>
/// Drives every in-flight schema version run to completion, one bounded pass at a time.
/// </summary>
/// <remarks>
/// The coordinator owns the connection, the retry policy, and how far one pass may go; the runner
/// owns what a batch means and the installer owns what a step and a finished run mean. That is the
/// division the Covenant maintenance sweeps already keep, and it is what stops a second idea of when
/// a version is installed from existing.
///
/// <para><b>Gated on the journal, never on availability.</b> Gating on tier health would deadlock in
/// both directions: a Covenant tier mid-run is unavailable by design, and a Core tier mid-run stands
/// its dependents down - so a driver that waited for a healthy tier could never run the very sweep
/// that restores one.</para>
///
/// <para>After a run finishes, the pass re-enters convergence so the next step's DDL runs without
/// waiting for a restart, and republishes Covenant tier health. Without that republication a tier
/// that became healthy when its sweep drained would keep reporting unavailable until the process
/// restarted.</para>
/// </remarks>
internal sealed class GrimoireSchemaTransitionCoordinator(
    ICovenantConnectionSource connections,
    GrimoireSchemaVersionChainSet chains,
    GrimoireSchemaInstaller installer,
    GrimoireSchemaBackfillRunner runner,
    IServiceProvider services,
    CovenantAvailability? availability = null,
    TimeProvider? timeProvider = null,
    ILogger<GrimoireSchemaTransitionCoordinator>? logger = null)
{
    /// <summary>How many bounded batches one pass may run for one tier before yielding.</summary>
    internal const int MaxBatchesPerPass = 16;

    private readonly ICovenantConnectionSource _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private readonly GrimoireSchemaVersionChainSet _chains =
        chains ?? throw new ArgumentNullException(nameof(chains));

    private readonly GrimoireSchemaInstaller _installer =
        installer ?? throw new ArgumentNullException(nameof(installer));

    private readonly GrimoireSchemaBackfillRunner _runner =
        runner ?? throw new ArgumentNullException(nameof(runner));

    private readonly IServiceProvider _services =
        services ?? throw new ArgumentNullException(nameof(services));

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    internal async Task<Result<GrimoireSchemaTransitionPassOutcome>> RunOnceAsync(
        CancellationToken cancellationToken)
    {
        SqliteConnection connection = await _connections
            .GetOpenCoreConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<GrimoireSchemaTransitionJournalRow> runs = await GrimoireSchemaTransitionJournal
            .ReadAllAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (runs.Count == 0)
        {
            return Result<GrimoireSchemaTransitionPassOutcome>.Success(
                new GrimoireSchemaTransitionPassOutcome(Advanced: false, 0, 0, 0, 0, 0, Deferred: false));
        }

        // Resolved once for the whole pass: it is the installation's own identity, it cannot change
        // while the pass runs, and reading it inside the retry lambda would be synchronous blocking on
        // an async read.
        GrimoireSchemaInitializationContext context = await ResolveContextAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        int batches = 0;

        long rows = 0;

        Dictionary<GrimoireSchemaTransactionTier, long> committedRows = [];

        bool converge = false;

        bool deferred = false;

        HashSet<GrimoireSchemaTransactionTier> advancedTiers = [];

        foreach (GrimoireSchemaTransitionJournalRow run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            GrimoireSchemaVersionChain chain = _chains.ForTier(run.TransactionTier);

            GrimoireSchemaRecordedTier? recorded = await GrimoireSchemaInstaller
                .ReadRecordedTierAsync(connection, chain, cancellationToken).ConfigureAwait(false);

            GrimoireSchemaEvolutionDecision decision = GrimoireSchemaEvolutionPlanner.Decide(
                chain, recorded, anyOwnedObjectPresent: true, run);

            if (decision.Action != GrimoireSchemaEvolutionAction.ResumeRun)
            {
                await RecordFailureAsync(connection, run, ErrorCodes.Grimoire.Schema.TransitionUnresumable, cancellationToken)
                    .ConfigureAwait(false);

                deferred = true;

                continue;
            }

            if (run.BackfillName is null)
            {
                converge = true;

                continue;
            }

            string? failureCode = null;

            try
            {
                GrimoireSchemaBackfillProgress progress = await SqliteBusyRetry.ExecuteAsync(
                    () => _runner.AdvanceAsync(
                        connection, chain, run, context, MaxBatchesPerPass, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                batches += progress.BatchesRun;

                rows += progress.RowsProcessed;

                committedRows[run.TransactionTier] = progress.RowsProcessed;

                converge |= progress.StepComplete;

                failureCode = progress.FailureCode;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Earlier batches may have committed before this failure. Only their journal
                // revisions and rows count; the failed transaction never earns progress.
                GrimoireSchemaTransitionJournalRow? durable = await GrimoireSchemaTransitionJournal
                    .ReadAsync(connection, null, run.TransactionTier, cancellationToken).ConfigureAwait(false);

                if (durable is not null)
                {
                    batches += checked((int)Math.Max(0, durable.Revision - run.Revision));

                    long committed = Math.Max(0, durable.BackfillRowsProcessed - run.BackfillRowsProcessed);

                    rows += committed;

                    committedRows[run.TransactionTier] = committed;
                }

                failureCode = GrimoireSchemaFailureCode.FromException(exception);
            }

            if (failureCode is not null)
            {
                await RecordFailureAsync(connection, run, failureCode, cancellationToken).ConfigureAwait(false);

                deferred = true;
            }
        }

        if (converge)
        {
            deferred |= await ConvergeAsync(connection, context, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<GrimoireSchemaTransitionJournalRow> remaining = await GrimoireSchemaTransitionJournal
            .ReadAllAsync(connection, cancellationToken).ConfigureAwait(false);

        foreach (GrimoireSchemaTransitionJournalRow before in runs)
        {
            GrimoireSchemaTransitionJournalRow? after = remaining.FirstOrDefault(
                row => row.TransactionTier == before.TransactionTier);

            if (after is null || after.CompletedThroughVersion != before.CompletedThroughVersion
                || after.BackfillName != before.BackfillName || after.BackfillCursor != before.BackfillCursor
                || after.BackfillRowsProcessed != before.BackfillRowsProcessed)
            {
                advancedTiers.Add(before.TransactionTier);

                logger?.LogInformation(
                    "Grimoire {Tier} schema sweep {Sweep} advanced from version {FromVersion} through {CompletedThroughVersion} toward {TargetVersion}; cumulative committed rows {Rows}; run complete {Complete}.",
                    before.TransactionTier, SafeSweep(before), before.FromVersion,
                    after?.CompletedThroughVersion ?? before.TargetVersion, before.TargetVersion,
                    after?.BackfillRowsProcessed ?? before.BackfillRowsProcessed + committedRows.GetValueOrDefault(before.TransactionTier), after is null);
            }
        }

        return Result<GrimoireSchemaTransitionPassOutcome>.Success(
            new GrimoireSchemaTransitionPassOutcome(
                advancedTiers.Count > 0, runs.Count, batches, rows,
                advancedTiers.Count, remaining.Count, deferred));
    }

    private async Task RecordFailureAsync(
        SqliteConnection connection,
        GrimoireSchemaTransitionJournalRow run,
        string code,
        CancellationToken cancellationToken)
    {
        GrimoireSchemaTransitionJournalRow? current = await GrimoireSchemaTransitionJournal
            .ReadAsync(connection, null, run.TransactionTier, cancellationToken).ConfigureAwait(false);

        if (current is null || current.Family != run.Family || current.FromVersion != run.FromVersion
            || current.TargetVersion != run.TargetVersion || current.Revision < run.Revision
            || !string.Equals(current.TargetSourceDefinitionFingerprint, run.TargetSourceDefinitionFingerprint, StringComparison.Ordinal)
            || !await GrimoireSchemaTransitionJournal.TryRecordErrorAsync(
                connection, current, code, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        logger?.LogWarning(
            "Grimoire {Tier} schema sweep {Sweep} at version {CompletedThroughVersion} toward {TargetVersion} deferred with {Code}; cumulative committed rows {Rows}.",
            current.TransactionTier, SafeSweep(current), current.CompletedThroughVersion, current.TargetVersion,
            code, current.BackfillRowsProcessed);
    }

    private string SafeSweep(GrimoireSchemaTransitionJournalRow run) =>
        run.BackfillName is null ? "DDL" :
        _chains.ForTier(run.TransactionTier).TryGetStep(run.CompletedThroughVersion, out GrimoireSchemaVersionStep step)
        && step.Backfill is { Name: var name }
        && string.Equals(name, run.BackfillName, StringComparison.Ordinal)
            ? name : "unrecognized";

    /// <summary>
    /// Re-enters the installer so the next step's DDL runs without a restart, and republishes what the
    /// tiers now are.
    /// </summary>
    private async Task<bool> ConvergeAsync(
        SqliteConnection connection,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GrimoireSchemaTransitionJournalRow> pending = await GrimoireSchemaTransitionJournal
            .ReadAllAsync(connection, cancellationToken).ConfigureAwait(false);

        GrimoireSchemaInstallResult result;

        try
        {
            result = await _installer.InstallAsync(
                connection,
                GrimoireEmbeddingDimensionResolver.Resolve(_services),
                context,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Optional tiers return their closed health result. Only Core failures escape the
            // installer, and a typed refusal identifies its tier without parsing exception text.
            GrimoireSchemaTransactionTier tier = exception is GrimoireSchemaRefusedException refused
                ? refused.Tier : GrimoireSchemaTransactionTier.Core;

            GrimoireSchemaTransitionJournalRow? failed = pending.FirstOrDefault(row => row.TransactionTier == tier);

            if (failed is null)
            {
                throw;
            }

            await RecordFailureAsync(connection, failed, GrimoireSchemaFailureCode.FromException(exception), cancellationToken).ConfigureAwait(false);

            return true;
        }

        bool deferred = false;

        foreach (GrimoireSchemaTierInstallResult tier in new[] { result.Core, result.CovenantCanonical, result.CovenantAccelerator })
        {
            // Incomplete means a declared sweep is still draining; dependency unavailability
            // means this tier was never attempted. Neither is a failed convergence for its run.
            if (tier.IsHealthy || tier.Health is GrimoireSchemaTierHealth.TransitionIncomplete
                or GrimoireSchemaTierHealth.DependencyUnavailable)
            {
                continue;
            }

            GrimoireSchemaTransitionJournalRow? failed = pending.FirstOrDefault(row => row.TransactionTier == tier.TransactionTier);

            if (failed is not null)
            {
                await RecordFailureAsync(connection, failed,
                    tier.Health == GrimoireSchemaTierHealth.TransitionUnresumable
                        ? ErrorCodes.Grimoire.Schema.TransitionUnresumable : ErrorCodes.Grimoire.Schema.FinalizationFailed,
                    cancellationToken).ConfigureAwait(false);

                deferred = true;
            }
        }

        if (availability is null)
        {
            return deferred;
        }

        _ = availability.PublishSchema(result, CovenantHealthTransition.SchemaEvolution);

        _ = await CovenantPersistedAvailabilityPublisher.PublishAsync(
                availability,
                connection,
                result.CovenantAccelerator.IsHealthy,
                CovenantHealthTransition.SchemaEvolution,
                cancellationToken)
            .ConfigureAwait(false);

        return deferred;
    }

    /// <summary>
    /// Reads the installation's own identity back out of the database it is converging.
    /// </summary>
    /// <remarks>
    /// Unlike a restore, a background pass has no fallback. Every tier it can act on is already
    /// installed, so the authority row must exist; seeding a fresh one from a background thread would
    /// mint an identity the operator never caused.
    /// </remarks>
    private async Task<GrimoireSchemaInitializationContext> ResolveContextAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken) =>
        await GrimoireSchemaInitializationContextReader
            .TryReadAsync(connection, _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false)
        ?? throw new InvalidOperationException(
            "The Grimoire holds a schema transition but no usable authority row, so no tier initializer "
            + "can be given the installation it belongs to.");
}
