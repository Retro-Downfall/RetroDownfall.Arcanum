using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

[ExcludeFromCodeCoverage]
internal sealed class DataRetentionSweepHostedService(
    IServiceScopeFactory scopeFactory,
    IDataRetentionPolicyStore policyStore,
    TimeProvider timeProvider,
    IGrimoireConnectionAdmissionGate admission,
    LongRunningOperationOwnership operationOwnership,
    ILogger<DataRetentionSweepHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic retention sweep failed before it could report a result.");
            }

            TimeSpan interval = TimeSpan.FromHours(
                ArcanumSettingClamps.RetentionSweepIntervalHours(
                    policyStore.Current.SweepIntervalHours));

            if (!policyStore.Current.AutomaticSweepsEnabled)
            {
                interval = TimeSpan.FromMinutes(1);
            }

            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        DataRetentionHostedSweepContinuation? continuation = null;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long predecessorGeneration = admission.CurrentGeneration;

                if (!admission.TryAcquireWorkLease(
                        GrimoireWorkKind.DataRetentionSweep,
                        out IGrimoireWorkLease? admitted))
                {
                    if (continuation is null)
                    {
                        return;
                    }

                    _ = await admission.WaitForOpenGenerationAfterRefusalAsync(
                        predecessorGeneration,
                        cancellationToken).ConfigureAwait(false);

                    continue;
                }

                DataRetentionHostedSweepOutcome outcome;

                await using (IGrimoireWorkLease lease = admitted!)
                {
                    if (continuation is null && !policyStore.Current.AutomaticSweepsEnabled)
                    {
                        return;
                    }

                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

                    IDataRetentionHostedSweep service = scope.ServiceProvider
                        .GetRequiredService<IDataRetentionHostedSweep>();

                    outcome = await service.ApplyOrResumeHostedPruneAsync(
                        continuation,
                        lease,
                        cancellationToken).ConfigureAwait(false);

                    if (outcome.Disposition == DataRetentionHostedSweepDisposition.DeferredForMaintenance)
                    {
                        DataRetentionHostedSweepContinuation retained = outcome.Continuation
                            ?? throw new InvalidOperationException(
                                "A deferred automatic retention sweep must retain its durable continuation.");

                        if (continuation is not null && continuation != retained)
                        {
                            throw new InvalidOperationException(
                                "A deferred automatic retention sweep must retain its exact durable continuation.");
                        }

                        continuation = retained;

                        if (outcome.Result is not null)
                        {
                            throw new InvalidOperationException(
                                "A deferred automatic retention sweep must return no terminal result.");
                        }
                    }
                }

                if (outcome.Disposition == DataRetentionHostedSweepDisposition.DeferredForMaintenance)
                {
                    _ = await admission.WaitForOpenGenerationAfterRefusalAsync(
                        predecessorGeneration,
                        cancellationToken).ConfigureAwait(false);

                    continue;
                }

                if (outcome.Disposition != DataRetentionHostedSweepDisposition.Concluded
                    || outcome.Continuation is not null
                    || outcome.Result is null)
                {
                    throw new InvalidOperationException(
                        "An automatic retention sweep returned an invalid terminal outcome.");
                }

                Result<DataRetentionApplyResult> result = outcome.Result;

                if (result.IsFailure)
                {
                    logger.LogWarning(
                        "Automatic retention sweep did not complete: {ErrorCode} {ErrorMessage}",
                        result.Error.Code,
                        result.Error.Message);

                    return;
                }

                logger.LogInformation(
                    "Automatic retention sweep {OperationId} deleted {Rows} rows and {Files} files.",
                    result.Value.OperationId,
                    result.Value.RowsDeleted,
                    result.Value.FilesDeleted);

                return;
            }
        }
        finally
        {
            if (continuation is not null)
            {
                _ = operationOwnership.Release(
                    continuation.OperationId,
                    continuation.OwnershipToken);
            }
        }
    }
}
