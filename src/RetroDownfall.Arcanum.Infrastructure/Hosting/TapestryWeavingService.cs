using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
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
[ExcludeFromCodeCoverage] // Reason: BackgroundService loop; TapestryWeaver carries the tested logic.
public sealed class TapestryWeavingService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ArcanumSettings> options,
    ILogger<TapestryWeavingService> logger) : BackgroundService
{

    /// <summary>Cadence used while the feature is disabled, so enabling it is picked up promptly.</summary>
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMinutes(1);

    private int _reconciled;

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

                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);

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
    internal async Task<IReadOnlyList<TapestryWeaveOutcome>> RunSweepAsync(
        EmbeddingSettings embeddings,
        CancellationToken cancellationToken)
    {

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        ITapestryStore store = scope.ServiceProvider.GetRequiredService<ITapestryStore>();

        TapestryWeaver weaver = scope.ServiceProvider.GetRequiredService<TapestryWeaver>();

        if (Interlocked.Exchange(ref _reconciled, 1) == 0)
        {

            int removed = await store.ReconcileGenerationsAsync(cancellationToken).ConfigureAwait(false);

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

            outcomes.Add(await weaver.WeaveAsync(treeScope, embeddings, cancellationToken).ConfigureAwait(false));

        }

        return outcomes;

    }

}
