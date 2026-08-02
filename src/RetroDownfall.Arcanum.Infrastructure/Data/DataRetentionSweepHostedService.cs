using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

[ExcludeFromCodeCoverage]
internal sealed class DataRetentionSweepHostedService(
    IServiceScopeFactory scopeFactory,
    IDataRetentionPolicyStore policyStore,
    TimeProvider timeProvider,
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

        if (!policyStore.Current.AutomaticSweepsEnabled)
        {

            return;

        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IDataRetentionService service = scope.ServiceProvider
            .GetRequiredService<IDataRetentionService>();

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(
                new DataRetentionRequest(DataRetentionOperation.Prune)),
            cancellationToken).ConfigureAwait(false);

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

    }

}
