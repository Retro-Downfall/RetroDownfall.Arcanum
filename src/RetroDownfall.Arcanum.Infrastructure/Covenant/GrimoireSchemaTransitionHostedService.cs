using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// Schedules the schema-transition coordinator, one bounded pass at a time.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> availability-gated, unlike every Covenant maintenance sweep. A tier with a
/// run in flight is not healthy by design - that is what keeps the capability fail-closed until the
/// work its version promises has been done - so a driver that waited for health could never run the
/// very sweep that restores it. Core makes the point sharper still: a Core run stands its dependents
/// down, and if it also blocked this service the installation would be unrepairable by the only
/// process able to repair it. The journal is the gate.
///
/// <para>Registered on the long-running host alone, through the installation-reset-recovery-aware
/// helper, for the same reasons the Covenant maintenance service is: the CLI composition is
/// short-lived, and a pass must not open a transaction against a dataset the installation is in the
/// middle of replacing.</para>
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed class GrimoireSchemaTransitionHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<GrimoireSchemaTransitionHostedService> logger) : BackgroundService
{

    /// <summary>How long a pass waits before the next one while a run is still advancing.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>How long it waits when nothing is in flight, which is the ordinary state.</summary>
    internal static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        while (!stoppingToken.IsCancellationRequested)
        {

            bool advanced = false;

            try
            {

                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

                Result<GrimoireSchemaTransitionPassOutcome> outcome = await scope.ServiceProvider
                    .GetRequiredService<GrimoireSchemaTransitionCoordinator>()
                    .RunOnceAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (outcome.IsFailure)
                {

                    logger.LogWarning(
                        "A Grimoire schema transition pass reported {Error}.",
                        outcome.Error.Message);

                }
                else
                {

                    advanced = outcome.Value.Advanced;

                    if (advanced)
                    {

                        logger.LogInformation(
                            "A Grimoire schema transition pass advanced {Runs} run(s) through {Batches} batch(es) and {Rows} row(s).",
                            outcome.Value.RunsSeen,
                            outcome.Value.BatchesRun,
                            outcome.Value.RowsProcessed);

                    }

                }

            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {

                break;

            }
            catch (Exception ex)
            {

                logger.LogError(ex, "A Grimoire schema transition pass failed before it could report a result.");

            }

            try
            {

                await Task.Delay(advanced ? Interval : IdleInterval, timeProvider, stoppingToken)
                    .ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {

                break;

            }

        }

    }

}
