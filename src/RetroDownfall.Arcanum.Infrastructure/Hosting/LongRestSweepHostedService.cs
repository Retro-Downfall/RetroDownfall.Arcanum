using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>Drains exact-current clean pending work; queue identities are only coalesced wake hints.</summary>
internal sealed class LongRestSweepHostedService(IServiceScopeFactory scopes, LongRestSweepQueue queue,
    ILongRestConfigurationProvider configuration, IGrimoireConnectionAdmissionGate admission,
    ILoggerFactory logging) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DrainPendingAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    LogFailure(exception);
                }

                // Requests signal after persistence. Retain a pending hint through the whole pass:
                // an insertion behind its cursor must start another pass from the beginning.
                _ = await queue.ReadAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task DrainPendingAsync(CancellationToken ct)
    {
        LongRestSweepConfiguration expected = configuration.Capture();

        Guid? cursor = null;

        while (expected.Enabled && configuration.IsCurrent(expected.Stamp) && configuration.Capture().Enabled)
        {
            ct.ThrowIfCancellationRequested();

            Guid? operation = await FindNextPendingAsync(expected.Stamp, cursor, ct).ConfigureAwait(false);

            if (operation is null || !configuration.IsCurrent(expected.Stamp) || !configuration.Capture().Enabled) return;

            // A refused candidate must not monopolize this pass or be retried in a busy loop.
            cursor = operation;

            try
            {
                await RunOneAsync(operation.Value, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogFailure(exception);
            }
        }
    }

    private async Task<Guid?> FindNextPendingAsync(LongRestConfigurationStamp expected, Guid? cursor, CancellationToken ct)
    {
        while (configuration.Capture().Enabled && configuration.IsCurrent(expected))
        {
            ct.ThrowIfCancellationRequested();

            long generation = admission.CurrentGeneration;

            if (!admission.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? admitted))
            {
                await admission.WaitForOpenGenerationAfterRefusalAsync(generation, ct).ConfigureAwait(false);

                continue;
            }

            try
            {
                await using IGrimoireWorkLease lease = admitted!;

                if (!configuration.Capture().Enabled || !configuration.IsCurrent(expected)) return null;

                using CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.MaintenanceRevocation);

                await using AsyncServiceScope scope = scopes.CreateAsyncScope();

                ILongRestSweepStore store = scope.ServiceProvider.GetRequiredService<ILongRestSweepStore>();

                var result = await store.FindNextPendingCleanAsync(expected, cursor, read.Token).ConfigureAwait(false);

                if (result.IsFailure) throw new InvalidOperationException("The pending Long Rest lookup was unavailable.");

                return result.Value;
            }
            catch (GrimoireMaintenanceUnavailableException)
            {
                await admission.WaitForOpenGenerationAfterRefusalAsync(generation, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await admission.WaitForOpenGenerationAfterRefusalAsync(generation, ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private void LogFailure(Exception exception)
    {
        // Log only the exception type; durable request identities and content stay in the stores.
        logging.CreateLogger<LongRestSweepHostedService>().LogError(
            "Long Rest worker stopped after {ExceptionType}; committed work remains recoverable.", exception.GetType().Name);
    }

    internal async Task RunOneAsync(Guid operation, CancellationToken ct)
    {
        while (configuration.Capture().Enabled)
        {
            ct.ThrowIfCancellationRequested();

            long generation = admission.CurrentGeneration;

            if (!admission.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? admitted))
            {
                // Keep this exact identity across every reopen/reclose race. No scope, connection,
                // polling timer or failure entry is created while admission is unavailable.
                await admission.WaitForOpenGenerationAfterRefusalAsync(generation, ct).ConfigureAwait(false);

                continue;
            }

            try
            {
                await using IGrimoireWorkLease lease = admitted!;

                if (!configuration.Capture().Enabled) return;

                await using AsyncServiceScope scope = scopes.CreateAsyncScope();

                using IDisposable binding = scope.ServiceProvider.GetRequiredService<LongRestWorkAdmission>().Bind(lease);

                ILongRestSweepCoordinator coordinator = scope.ServiceProvider.GetRequiredService<ILongRestSweepCoordinator>();

                await coordinator.ResumeAsync(operation, null, ct).ConfigureAwait(false);

                return;
            }
            catch (GrimoireMaintenanceUnavailableException)
            {
                await admission.WaitForOpenGenerationAfterRefusalAsync(generation, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await admission.WaitForOpenGenerationAfterRefusalAsync(generation, ct).ConfigureAwait(false);
            }
        }
    }
}
