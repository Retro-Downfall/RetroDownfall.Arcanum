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
    long RowsProcessed);

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

    /// <summary>The bounded code recorded on a run whose pass failed. Never an exception message.</summary>
    private const string PassFailedCode = "Grimoire.Schema.TransitionPassFailed";

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
                new GrimoireSchemaTransitionPassOutcome(Advanced: false, 0, 0, 0));

        }

        // Resolved once for the whole pass: it is the installation's own identity, it cannot change
        // while the pass runs, and reading it inside the retry lambda would be synchronous blocking on
        // an async read.
        GrimoireSchemaInitializationContext context = await ResolveContextAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        int batches = 0;

        long rows = 0;

        bool advanced = false;

        foreach (GrimoireSchemaTransitionJournalRow run in runs)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (run.BackfillName is null)
            {

                // The next step's DDL has not run. That belongs to convergence, not to a sweep.
                advanced = true;

                continue;

            }

            try
            {

                GrimoireSchemaBackfillProgress progress = await SqliteBusyRetry.ExecuteAsync(
                    () => _runner.AdvanceAsync(
                        connection,
                        _chains.ForTier(run.TransactionTier),
                        run,
                        context,
                        MaxBatchesPerPass,
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                batches += progress.BatchesRun;

                rows += progress.RowsProcessed;

                advanced |= progress.BatchesRun > 0;

            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {

                logger?.LogWarning(
                    exception,
                    "A {Tier} schema transition pass failed; the run is left for the next pass.",
                    run.TransactionTier);

                await GrimoireSchemaTransitionJournal
                    .RecordErrorAsync(connection, run.TransactionTier, PassFailedCode, _time.GetUtcNow(), CancellationToken.None)
                    .ConfigureAwait(false);

            }

        }

        if (advanced)
        {

            await ConvergeAsync(connection, context, cancellationToken).ConfigureAwait(false);

        }

        return Result<GrimoireSchemaTransitionPassOutcome>.Success(
            new GrimoireSchemaTransitionPassOutcome(advanced, runs.Count, batches, rows));

    }

    /// <summary>
    /// Re-enters the installer so the next step's DDL runs without a restart, and republishes what the
    /// tiers now are.
    /// </summary>
    private async Task ConvergeAsync(
        SqliteConnection connection,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        GrimoireSchemaInstallResult result = await _installer.InstallAsync(
            connection,
            GrimoireEmbeddingDimensionResolver.Resolve(_services),
            context,
            cancellationToken).ConfigureAwait(false);

        if (availability is null)
        {

            return;

        }

        _ = availability.PublishSchema(result, CovenantHealthTransition.SchemaEvolution);

        _ = await CovenantPersistedAvailabilityPublisher.PublishAsync(
                availability,
                connection,
                result.CovenantAccelerator.IsHealthy,
                CovenantHealthTransition.SchemaEvolution,
                cancellationToken)
            .ConfigureAwait(false);

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
