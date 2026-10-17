using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Drains exact-current clean work: never-claimed requests, and parked clean sweeps whose fault is resumable, whose
/// failure budget is unspent and whose cooldown has elapsed. Queue identities are only coalesced wake hints; a Parked
/// Rescan timer also wakes it, because nothing signals a cooldown that expires or a recovery that parks a sweep.
/// </summary>
internal sealed class LongRestSweepHostedService(IServiceScopeFactory scopes, LongRestSweepQueue queue,
    ILongRestConfigurationProvider configuration, IGrimoireConnectionAdmissionGate admission,
    TimeProvider timeProvider, ILoggerFactory logging) : BackgroundService
{
    /// <summary>The longest the worker sleeps between scans when no queue hint arrives.</summary>
    internal static readonly TimeSpan ParkedRescanInterval = TimeSpan.FromSeconds(60);

    /// <summary>The shortest timer sleep, so a sweep that is due but refused is not retried in a busy loop.</summary>
    internal static readonly TimeSpan MinimumRescanDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                DateTimeOffset? due = null;

                try
                {
                    due = await DrainPendingAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    LogFailure(exception);
                }

                await WaitForWakeAsync(due, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // One awaited read is the whole wait: the clock timer is only a cancellation source for it, so no timer-backed or
    // channel task outlives this call. Requests signal after persistence, and the bounded hint channel keeps a signal that
    // lands during a pass, so an insertion behind the cursor starts another pass from the beginning. A cancelled read
    // consumes no hint, which is why the timer may interrupt it at any moment.
    private async Task WaitForWakeAsync(DateTimeOffset? due, CancellationToken stoppingToken)
    {
        using CancellationTokenSource rescan = new(RescanDelay(due), timeProvider);

        using CancellationTokenSource wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, rescan.Token);

        try
        {
            _ = await queue.ReadAsync(wake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (rescan.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
        }
    }

    private TimeSpan RescanDelay(DateTimeOffset? due)
    {
        if (due is not { } at) return ParkedRescanInterval;

        TimeSpan untilDue = at - timeProvider.GetUtcNow();

        return untilDue < MinimumRescanDelay ? MinimumRescanDelay : untilDue < ParkedRescanInterval ? untilDue : ParkedRescanInterval;
    }

    /// <returns>When the next cooling parked sweep becomes due, if one is known.</returns>
    private async Task<DateTimeOffset?> DrainPendingAsync(CancellationToken ct)
    {
        LongRestSweepConfiguration expected = configuration.Capture();

        Guid? cursor = null;

        while (expected.Enabled && configuration.IsCurrent(expected.Stamp) && configuration.Capture().Enabled)
        {
            ct.ThrowIfCancellationRequested();

            LongRestRunnableSweep? found = await FindNextPendingAsync(expected.Stamp, cursor, ct).ConfigureAwait(false);

            if (found is not { OperationId: { } operation } || !configuration.IsCurrent(expected.Stamp) || !configuration.Capture().Enabled)
            {
                return found?.NextDueAt;
            }

            // A refused candidate must not monopolize this pass or be retried in a busy loop.
            cursor = operation;

            try
            {
                await RunOneAsync(operation, ct).ConfigureAwait(false);
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

        return null;
    }

    private async Task<LongRestRunnableSweep?> FindNextPendingAsync(LongRestConfigurationStamp expected, Guid? cursor, CancellationToken ct)
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

                var result = await store.FindNextRunnableCleanAsync(expected, cursor, read.Token).ConfigureAwait(false);

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

                ILongRestAdmittedSweep sweep = scope.ServiceProvider.GetRequiredService<ILongRestAdmittedSweep>();

                await sweep.ResumeAdmittedAsync(operation, lease, ct).ConfigureAwait(false);

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
