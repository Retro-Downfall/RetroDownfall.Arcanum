using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Keeps The Tapestry's trees current (DESIGN §21.11). Idles unless
/// <c>Arcanum:Features:Tapestry</c> is enabled, so registering it unconditionally stays free on the
/// hot path — the same pattern as <c>WorkspaceIndexingService</c> and <c>SagaExtractionService</c>.
///
/// <para><b>Restart guarantee without a durable operation kind.</b> Every build stages into an
/// invisible generation and becomes current through one atomic switch, so an interrupted, cancelled,
/// or failed build leaves exactly two possible states: the previous complete generation is still
/// current, or the new one is. The first sweep after startup calls
/// <see cref="ITapestryStore.ReconcileGenerationsAsync"/> to drop whatever a killed process left
/// behind, and the corpus fingerprint then drives a clean rebuild. That is the whole recovery
/// story — no §10.8 ledger entry is required to make it hold.</para>
///
/// <para>There is no whole-build deadline. The sweep checkpoints between scopes and between layers,
/// stays cancellable throughout, and simply continues on the next tick.</para>
/// </summary>
internal sealed class TapestryWeavingService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ArcanumSettings> options,
    IGrimoireConnectionAdmissionGate admission,
    ILogger<TapestryWeavingService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    /// <summary>Cadence used while the feature is disabled, so enabling it is picked up promptly.</summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMinutes(1);

    // ExecuteAsync is the sole sweep owner; no queued identities or parallel sweep tasks exist.
    private bool _reconciled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = IdlePollInterval;

            try
            {
                EmbeddingSettings embeddings = options.CurrentValue.ResolveEmbeddings();

                if (embeddings.Enabled && embeddings.TapestryEnabled)
                {
                    delay = TimeSpan.FromMinutes(
                        ArcanumSettingClamps.EmbeddingsTapestryRebuildIntervalMinutes(
                            embeddings.Tapestry.RebuildIntervalMinutes));

                    await RunSweepAsync(embeddings, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A build failure on one corpus must never stop the host or the other corpora; the
                // next tick simply tries again against the same last-complete generations.
                logger.LogWarning(ex, "Tapestry weaving sweep failed; retrying on the next tick.");
            }

            try
            {
                await Task.Delay(delay, timeProvider ?? TimeProvider.System, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One complete sweep. <c>internal</c> rather than <c>private</c> so tests can drive it directly
    /// without the hosted-service loop, mirroring <c>SagaExtractionService.ExtractForSessionAsync</c>.
    /// </summary>
    internal async Task<TapestrySweepOutcome> RunSweepAsync(
        EmbeddingSettings embeddings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!admission.TryAcquireWorkLease(GrimoireWorkKind.TapestryWeaving, out IGrimoireWorkLease? admitted))
        {
            return new(TapestrySweepStatus.DeferredForMaintenance, []);
        }

        await using IGrimoireWorkLease lease = admitted!;

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        ITapestryStore store = scope.ServiceProvider.GetRequiredService<ITapestryStore>();

        TapestryWeaver weaver = scope.ServiceProvider.GetRequiredService<TapestryWeaver>();

        if (!_reconciled)
        {
            int removed = await store.ReconcileGenerationsAsync(cancellationToken).ConfigureAwait(false);

            _reconciled = true;

            if (removed > 0)
            {
                logger.LogInformation(
                    "Tapestry reconciliation removed {Count} incomplete generation(s) left by a previous run.",
                    removed);
            }
        }

        TapestryEmbeddingSettings tapestry = embeddings.Tapestry ?? new TapestryEmbeddingSettings();

        IReadOnlyList<TapestryScope> scopes = await store
            .DiscoverScopesAsync(
                tapestry.WorkspaceTreesEnabled,
                tapestry.SessionAttachmentTreesEnabled,
                tapestry.SessionTreesEnabled,
                cancellationToken)
            .ConfigureAwait(false);

        List<TapestryWeaveOutcome> outcomes = [];

        foreach (TapestryScope treeScope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? admittedGroup))
            {
                return new(TapestrySweepStatus.DeferredForMaintenance, outcomes);
            }

            await using IGrimoireExternalEffectGroup group = admittedGroup!;

            try
            {
                outcomes.Add(await weaver.WeaveAsync(treeScope, embeddings, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Preflight and BeginGenerationAsync can fail before the weaver owns a generation.
                // One unavailable corpus must not prevent the other scopes from being attempted.
                logger.LogWarning(ex, "Tapestry scope {ScopeKind} {ScopeId} failed; continuing the sweep.", treeScope.Kind, treeScope.Id);

                outcomes.Add(new TapestryWeaveOutcome(TapestryWeaveStatus.Failed));
            }
        }

        // Publishing a generation only marks its predecessor Superseded; reconciliation is what actually
        // deletes it. Reconciling once per process left every rebuild after the first sweep with a full
        // orphaned copy of that scope's nodes and node embeddings in the Grimoire until restart — an
        // unbounded leak that nothing ever reads again, and one that contradicts ITapestryStore's
        // documented promise that Superseded rows exist only until reconciliation removes them. Running
        // it at the end of every sweep costs one statement per sweep and keeps that promise true.
        int superseded = await store.ReconcileGenerationsAsync(cancellationToken).ConfigureAwait(false);

        if (superseded > 0)
        {
            logger.LogDebug(
                "Tapestry reconciliation removed {Count} superseded generation(s) after the sweep.",
                superseded);
        }

        // Reconciliation preserves each scope's current complete generation, so a scope that vanishes
        // outright — a deleted session, a workspace no longer indexed — keeps its entire tree forever:
        // it is never rediscovered, so it is never rebuilt or superseded, and nothing ever reads it
        // again. Pruning here, against the same corpora this sweep discovered from, is what bounds the
        // store by the data that actually exists.
        int orphaned = await store
            .PruneRemovedScopesAsync(
                tapestry.WorkspaceTreesEnabled,
                tapestry.SessionAttachmentTreesEnabled,
                tapestry.SessionTreesEnabled,
                cancellationToken)
            .ConfigureAwait(false);

        if (orphaned > 0)
        {
            logger.LogInformation(
                "Tapestry pruning removed {Count} generation(s) for scopes that no longer exist.",
                orphaned);
        }

        return new(TapestrySweepStatus.Completed, outcomes);
    }
}

internal enum TapestrySweepStatus
{
    Completed,

    DeferredForMaintenance,
}

internal sealed record TapestrySweepOutcome(
    TapestrySweepStatus Status,
    IReadOnlyList<TapestryWeaveOutcome> Outcomes);
