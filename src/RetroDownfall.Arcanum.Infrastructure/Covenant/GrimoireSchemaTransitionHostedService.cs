using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

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
/// middle of replacing. The journal makes a pass recoverable, but it remains ordinary live-catalog
/// work and holds one admission lease through its final scope disposal.</para>
/// </remarks>
[ExcludeFromCodeCoverage] // Reason: IHostedService lifetime shell; the journal-gated pass is covered by GrimoireSchemaTransitionHostedServiceTests and GrimoireSchemaTransitionWiringTests.
internal sealed class GrimoireSchemaTransitionHostedService(
    IServiceScopeFactory scopeFactory,
    IGrimoireConnectionAdmissionGate admissionGate,
    TimeProvider timeProvider,
    ILogger<GrimoireSchemaTransitionHostedService> logger) : BackgroundService
{
    /// <summary>How long a pass waits before the next one while a run is still advancing.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long it waits when nothing is in flight, which is the ordinary state.</summary>
    internal static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = RetryInterval;

            try
            {
                delay = await RunPassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError("A Grimoire schema transition pass failed before it could report a result ({Code}); retrying.",
                    GrimoireSchemaTransitionCoordinator.FailureCode(ex));
            }

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs one bounded schema-transition pass when ordinary Grimoire work is admitted.
    /// </summary>
    internal async Task<bool> RunOnceAsync(CancellationToken cancellationToken) =>
        await RunPassAsync(cancellationToken).ConfigureAwait(false) == Interval;

    private async Task<TimeSpan> RunPassAsync(CancellationToken cancellationToken)
    {
        if (!admissionGate.TryAcquireWorkLease(
                GrimoireWorkKind.GrimoireSchemaTransition,
                out IGrimoireWorkLease? admitted))
        {
            return RetryInterval;
        }

        await using IGrimoireWorkLease lease = admitted!;

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        Result<GrimoireSchemaTransitionPassOutcome> outcome = await scope.ServiceProvider
            .GetRequiredService<GrimoireSchemaTransitionCoordinator>()
            .RunOnceAsync(cancellationToken)
            .ConfigureAwait(false);

        if (outcome.IsFailure)
        {
            logger.LogWarning(
                "A Grimoire schema transition pass reported {Code}.",
                outcome.Error.Code);

            return RetryInterval;
        }

        if (outcome.Value.Advanced)
        {
            logger.LogInformation(
                "A Grimoire schema transition pass advanced {Runs} run(s) through {Batches} batch(es) and {Rows} row(s).",
                outcome.Value.RunsAdvanced,
                outcome.Value.BatchesRun,
                outcome.Value.RowsProcessed);
        }

        return outcome.Value.Deferred
            ? RetryInterval
            : outcome.Value.Advanced
                ? Interval
                : outcome.Value.RunsPending > 0 ? RetryInterval : IdleInterval;
    }
}
