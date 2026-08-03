using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// Runs after Grimoire bootstrap and before subsequently registered hosted workloads. A short
/// readiness pass never abandons unfinished recovery: the same checkpointed reconciliation keeps
/// running periodically in the background until host shutdown.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class LongRunningOperationStartupHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    LongRunningOperationReconciliationStatus status,
    ILogger<LongRunningOperationStartupHostedService> logger) : IHostedService
{
    internal const int ReconciliationPageSize = 100;

    internal const int MaxStartupConcurrency = 4;

    internal static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan BackgroundInterval = TimeSpan.FromMinutes(1);

    private readonly CancellationTokenSource _shutdown = new();

    private Task? _backgroundTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(StartupBudget);
        string ownerId = $"startup-{Environment.ProcessId}-{Guid.NewGuid():N}";

        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            LongRunningOperationReconciler reconciler =
                scope.ServiceProvider.GetRequiredService<LongRunningOperationReconciler>();
            var summary = await reconciler.ReconcileAsync(
                startedAt,
                ownerId,
                ReconciliationPageSize,
                MaxStartupConcurrency,
                budget.Token).ConfigureAwait(false);
            status.Record(startedAt, summary);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && budget.IsCancellationRequested)
        {
            const string detail =
                "Startup reconciliation exceeded its 10 second readiness budget; checkpointed recovery is continuing in the background. "
                + "Run 'arcanum operation reconcile' for an immediate foreground pass.";

            status.RecordDeferred(startedAt, detail);

            logger.LogWarning("{Detail}", detail);
        }

        _backgroundTask = Task.Run(
            () => ContinueInBackgroundAsync(_shutdown.Token),
            CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_backgroundTask is null)
        {

            return;

        }

        try
        {

            await _backgroundTask.WaitAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || _shutdown.IsCancellationRequested)
        {

        }

    }

    private async Task ContinueInBackgroundAsync(CancellationToken cancellationToken)
    {

        while (!cancellationToken.IsCancellationRequested)
        {

            try
            {
                DateTimeOffset now = timeProvider.GetUtcNow();

                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

                LongRunningOperationReconciler reconciler =
                    scope.ServiceProvider.GetRequiredService<LongRunningOperationReconciler>();

                LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileAsync(
                    now,
                    $"background-{Environment.ProcessId}-{Guid.NewGuid():N}",
                    ReconciliationPageSize,
                    MaxStartupConcurrency,
                    cancellationToken).ConfigureAwait(false);

                status.Record(now, summary);

                await Task.Delay(BackgroundInterval, timeProvider, cancellationToken)
                    .ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {

                return;

            }
            catch (Exception ex)
            {

                logger.LogError(ex, "Background durable-operation reconciliation failed; it will retry.");

                try
                {

                    await Task.Delay(BackgroundInterval, timeProvider, cancellationToken)
                        .ConfigureAwait(false);

                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {

                    return;

                }

            }

        }

    }
}
