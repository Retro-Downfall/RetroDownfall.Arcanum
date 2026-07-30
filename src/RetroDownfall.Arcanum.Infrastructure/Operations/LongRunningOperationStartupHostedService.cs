using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// Runs after Grimoire bootstrap and before subsequently registered hosted workloads. Startup work
/// is deliberately bounded; a timeout enters documented degraded mode and leaves repair to the
/// authenticated reconcile command.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class LongRunningOperationStartupHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    LongRunningOperationReconciliationStatus status,
    ILogger<LongRunningOperationStartupHostedService> logger) : IHostedService
{
    internal const int MaxStartupOperations = 100;
    internal const int MaxStartupConcurrency = 4;
    internal static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(10);

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
                MaxStartupOperations,
                MaxStartupConcurrency,
                budget.Token).ConfigureAwait(false);
            status.Record(startedAt, summary);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && budget.IsCancellationRequested)
        {
            const string detail =
                "Startup reconciliation exceeded its 10 second budget; optional recovery was deferred. "
                + "Run 'arcanum operation reconcile'.";
            status.RecordDeferred(startedAt, detail);
            logger.LogWarning("{Detail}", detail);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
