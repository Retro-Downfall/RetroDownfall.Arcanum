using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;

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
    IGrimoireConnectionAdmissionGate admissionGate,
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

                LongRunningOperationReconciliationSummary summary = await RunBackgroundPassAsync(
                    now,
                    $"background-{Environment.ProcessId}-{Guid.NewGuid():N}",
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

    internal async Task<LongRunningOperationReconciliationSummary> RunBackgroundPassAsync(
        DateTimeOffset utcNow,
        string ownerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        IReadOnlyList<LongRunningOperation> discovered = await DiscoverPageAsync(
            utcNow,
            cancellationToken).ConfigureAwait(false);

        int claimed = 0;
        int completed = 0;
        int failed = 0;
        int abandoned = 0;
        int attention = 0;
        int skipped = 0;

        await Parallel.ForEachAsync(
            discovered,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaxStartupConcurrency,
            },
            async (operation, ct) =>
            {
                LongRunningRecoveryAdmissionDecision decision =
                    LongRunningOperationRecoveryAdmission.Classify(operation, ownerEvidence: null);

                if (decision.Kind is LongRunningRecoveryAdmissionKind.OwnerBoundAwaitingExactOwner)
                {
                    Interlocked.Increment(ref skipped);

                    return;
                }

                LongRunningOperationSettlementOutcome outcome = await SettleAdmittedAsync(
                    operation,
                    ownerId,
                    decision,
                    ct).ConfigureAwait(false);

                if (outcome is not LongRunningOperationSettlementOutcome.ConcurrencyLost
                    and not LongRunningOperationSettlementOutcome.OwnedInProcess)
                {
                    Interlocked.Increment(ref claimed);
                }

                switch (outcome)
                {
                    case LongRunningOperationSettlementOutcome.Completed:
                        Interlocked.Increment(ref completed);
                        break;
                    case LongRunningOperationSettlementOutcome.Failed:
                        Interlocked.Increment(ref failed);
                        break;
                    case LongRunningOperationSettlementOutcome.Abandoned:
                        Interlocked.Increment(ref abandoned);
                        break;
                    case LongRunningOperationSettlementOutcome.RequiresAttention:
                        Interlocked.Increment(ref attention);
                        break;
                    default:
                        Interlocked.Increment(ref skipped);
                        break;
                }
            }).ConfigureAwait(false);

        return new LongRunningOperationReconciliationSummary(
            discovered.Count,
            claimed,
            completed,
            failed,
            abandoned,
            attention,
            skipped);
    }

    private async Task<IReadOnlyList<LongRunningOperation>> DiscoverPageAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        IGrimoireWorkLease lease = await AcquireWorkLeaseAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            ILongRunningOperationGenericRecoveryDiscovery discovery = scope.ServiceProvider
                .GetRequiredService<ILongRunningOperationGenericRecoveryDiscovery>();

            return await discovery.FindExpiredForGenericRecoveryAsync(
                utcNow,
                ReconciliationPageSize,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<LongRunningOperationSettlementOutcome> SettleAdmittedAsync(
        LongRunningOperation operation,
        string ownerId,
        LongRunningRecoveryAdmissionDecision decision,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IGrimoireWorkLease lease = await AcquireWorkLeaseAsync(cancellationToken).ConfigureAwait(false);
            IGrimoireExternalEffectGroup? group = null;
            AsyncServiceScope scope = default;
            bool scopeCreated = false;

            if (decision.Kind is LongRunningRecoveryAdmissionKind.OrdinaryExternalEffect
                && !lease.TryBeginExternalEffectGroup(out group))
            {
                long observedGeneration = lease.Generation;
                await lease.DisposeAsync().ConfigureAwait(false);
                await admissionGate.WaitForOpenGenerationAfterRefusalAsync(
                    observedGeneration,
                    cancellationToken).ConfigureAwait(false);

                continue;
            }

            try
            {
                scope = scopeFactory.CreateAsyncScope();
                scopeCreated = true;
                LongRunningOperationReconciler reconciler = scope.ServiceProvider
                    .GetRequiredService<LongRunningOperationReconciler>();

                return await reconciler.SettleDiscoveredRuntimeAsync(
                    operation,
                    ownerId,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    try
                    {
                        if (group is not null)
                        {
                            await group.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        if (scopeCreated)
                        {
                            await scope.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<IGrimoireWorkLease> AcquireWorkLeaseAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long observedGeneration = admissionGate.CurrentGeneration;

            if (admissionGate.TryAcquireWorkLease(
                    GrimoireWorkKind.LongRunningOperationRecovery,
                    out IGrimoireWorkLease? lease))
            {
                return lease!;
            }

            await admissionGate.WaitForOpenGenerationAfterRefusalAsync(
                observedGeneration,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
