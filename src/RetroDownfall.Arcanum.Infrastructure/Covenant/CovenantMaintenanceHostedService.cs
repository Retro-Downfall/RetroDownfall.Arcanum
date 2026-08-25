using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// Drives the three Covenant maintenance sweeps, one bounded pass at a time.
/// </summary>
/// <remarks>
/// The sweeps existed, were registered, and were exercised by their own suites, and nothing under
/// <c>src</c> ever called them. That is not a dormant optimization: core owner deletions journalled
/// for the encrypted tier were never applied, so a deleted Campaign's Covenant rows outlived it; the
/// canonical outbox only ever grew, so the accelerator projection stayed at whatever sequence it was
/// last left at and the pending-row ceiling was all that stood between an installation and a refusal
/// it could not act on; and turn receipts accumulated against a per-Session ceiling with nothing able
/// to fold them.
///
/// <para>One service for all three rather than three, because they contend for the same two gate
/// leases and running them in sequence is what keeps a pass from queueing behind itself. Each sweep is
/// independent: one failing is logged and the next still runs, since a cleanup backlog and an outbox
/// backlog are unrelated problems and stalling both on either is worse than either.</para>
///
/// <para>Availability-gated on every pass rather than at startup. The tier can go unhealthy, be reset,
/// or be erased while the service is alive, and a sweep that decided once at boot would keep opening
/// transactions against a dataset the installation has since replaced. Reset-awareness comes from the
/// registration, which withholds the service until recovery has settled.</para>
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed class CovenantMaintenanceHostedService(
    IServiceScopeFactory scopeFactory,
    ICovenantAvailability availability,
    TimeProvider timeProvider,
    ILogger<CovenantMaintenanceHostedService> logger) : BackgroundService
{

    /// <summary>How long a pass waits before the next one, when the tier is healthy.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>How long it waits when the feature or the tier is not in a state to sweep.</summary>
    internal static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        while (!stoppingToken.IsCancellationRequested)
        {

            bool swept = false;

            try
            {

                swept = await RunOnceAsync(stoppingToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {

                break;

            }
            catch (Exception ex)
            {

                logger.LogError(ex, "A Covenant maintenance pass failed before it could report a result.");

            }

            try
            {

                await Task.Delay(swept ? Interval : IdleInterval, timeProvider, stoppingToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {

                break;

            }

        }

    }

    /// <summary>
    /// Runs one bounded pass of each sweep, reporting whether the tier was in a state to sweep at all.
    /// </summary>
    internal async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {

        CovenantAvailabilitySnapshot health = availability.Current;

        // The canonical tier is the one every sweep writes through. An unhealthy or absent one is not
        // an error to report every minute — it is a state the installation is allowed to be in, and
        // the sweeps simply have nothing they may do until it leaves.
        if (!health.FeatureEnabled || health.Canonical != CovenantCapabilityState.Healthy)
        {

            return false;

        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await RunSweepAsync(
                "owner cleanup",
                async () =>
                {

                    Result<CovenantCleanupOutcome> outcome = await scope.ServiceProvider
                        .GetRequiredService<CovenantOwnerCleanupCoordinator>()
                        .RunBatchAsync(CovenantCleanupWorker.DefaultBatchSize, cancellationToken)
                        .ConfigureAwait(false);

                    return outcome.IsFailure
                        ? Result<string>.Failure(outcome.Error)
                        : Result<string>.Success(
                            $"{outcome.Value.CampaignsCleaned} Campaign(s), {outcome.Value.SessionsCleaned} Session(s), {outcome.Value.HeadsRemoved} head(s)");

                })
            .ConfigureAwait(false);

        await RunSweepAsync(
                "search outbox",
                async () =>
                {

                    Result<CovenantOutboxSyncOutcome> outcome = await scope.ServiceProvider
                        .GetRequiredService<CovenantSearchOutboxCoordinator>()
                        .SynchronizeAsync(CovenantSearchOutboxWorker.DefaultBatchRows, cancellationToken)
                        .ConfigureAwait(false);

                    return outcome.IsFailure
                        ? Result<string>.Failure(outcome.Error)
                        : Result<string>.Success($"{outcome.Value.ProjectionsWritten} projection(s)");

                })
            .ConfigureAwait(false);

        await RunSweepAsync(
                "turn receipt compaction",
                async () =>
                {

                    Result<CovenantReceiptCompactionOutcome> outcome = await scope.ServiceProvider
                        .GetRequiredService<CovenantTurnReceiptCompactionCoordinator>()
                        .CompactAsync(CovenantTurnReceiptCompactionCoordinator.DefaultSessionsPerPass, cancellationToken)
                        .ConfigureAwait(false);

                    return outcome.IsFailure
                        ? Result<string>.Failure(outcome.Error)
                        : Result<string>.Success(
                            $"{outcome.Value.ReceiptsFolded} receipt(s) across {outcome.Value.SessionsFolded} Session(s)");

                })
            .ConfigureAwait(false);

        return true;

    }

    /// <summary>
    /// Runs one sweep and reports it, never letting its refusal stop the sweeps after it.
    /// </summary>
    /// <remarks>
    /// A refusal here is ordinary rather than exceptional: an exclusive owner holds the gate, the
    /// dataset generation moved under a batch, a lease was revoked. Each is a reason to try again next
    /// pass, and none is a reason to leave the other two backlogs undrained.
    /// </remarks>
    private async Task RunSweepAsync(string sweep, Func<Task<Result<string>>> run)
    {

        try
        {

            Result<string> outcome = await run().ConfigureAwait(false);

            if (outcome.IsFailure)
            {

                logger.LogDebug(
                    "Covenant {Sweep} sweep did not run this pass: {ErrorCode} {ErrorMessage}",
                    sweep,
                    outcome.Error.Code,
                    outcome.Error.Message);

                return;

            }

            logger.LogDebug("Covenant {Sweep} sweep applied {Applied}.", sweep, outcome.Value);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogError(ex, "The Covenant {Sweep} sweep threw before it could report a result.", sweep);

        }

    }

}
