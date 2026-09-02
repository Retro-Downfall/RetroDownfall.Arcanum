using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// Keeps the durable lease alive on every Sending this process is actively holding.
/// </summary>
/// <remarks>
/// A Sending has no whole-operation deadline, so its ledger lease marks ownership rather than sizing
/// the work — which only holds up if something renews it. Nothing did, while
/// <c>LongRunningOperationStartupHostedService</c> reconciles the whole ledger every minute for the host's
/// lifetime: any Sending that outran the lease was found "expired", re-leased by the reconciler and
/// recovered out from under the live call. Outbound, that sent <c>tasks/cancel</c> for the very task the
/// dispatch was still awaiting; inbound, it abandoned the relay while the Apprentice was still working.
/// <para>
/// Only rows this process is actually holding are renewed. A Sending parked at <c>input-required</c> is
/// deliberately dropped: waiting on a peer is not work, and reconciliation flagging it
/// <c>a2a.inbound_parked_awaiting_answer</c> is exactly what keeps it answerable after a restart
/// (docs/Arcanum.DESIGN.md &#167;5.7.1.5).
/// </para>
/// </remarks>
internal sealed class A2ASendingLeaseRenewer(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<A2ASendingLeaseRenewer> logger) : BackgroundService
{

    /// <summary>How far ahead each renewal pushes a held Sending's lease.</summary>
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How often held leases are renewed. Comfortably inside <see cref="LeaseDuration"/>: the store
    /// refuses to heartbeat a lease that has already lapsed, so a pass missed to a slow Grimoire must
    /// still leave room for the next one.
    /// </summary>
    internal static readonly TimeSpan RenewalInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, string> _held = new();

    /// <summary>Starts renewing a Sending's lease, from the moment this process takes it.</summary>
    internal void Track(A2ASendingLedgerEntry entry)
    {

        if (!entry.IsRecorded)
        {

            return;

        }

        _held[entry.OperationId] = entry.OwnerId;

    }

    /// <summary>Stops renewing a Sending's lease, because it settled or is no longer this process's work.</summary>
    internal void Forget(A2ASendingLedgerEntry entry)
    {

        if (!entry.IsRecorded)
        {

            return;

        }

        _held.TryRemove(entry.OperationId, out _);

    }

    /// <summary>
    /// Renews every held lease once, and returns how many were renewed.
    /// </summary>
    /// <remarks>
    /// A row that refuses the heartbeat is dropped rather than re-leased: it has been closed, or another
    /// owner has taken it, and fighting reconciliation for a row this process no longer owns would be a
    /// worse answer than letting it go.
    /// </remarks>
    internal async Task<int> RenewHeldAsync(CancellationToken cancellationToken)
    {

        if (_held.IsEmpty)
        {

            return 0;

        }

        int renewed = 0;

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        ILongRunningOperationStore store = scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();

        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (KeyValuePair<Guid, string> held in _held)
        {

            bool ok = await store
                .HeartbeatAsync(held.Key, held.Value, now, now.Add(LeaseDuration), cancellationToken)
                .ConfigureAwait(false);

            if (ok)
            {

                renewed++;

                continue;

            }

            _held.TryRemove(held.Key, out _);

            logger.LogDebug(
                "A2A: Sending {OperationId} no longer holds its durable lease; it will not be renewed again.",
                held.Key);

        }

        return renewed;

    }

    [ExcludeFromCodeCoverage] // Reason: BackgroundService scheduling loop; RenewHeldAsync carries the logic.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {

            try
            {

                await Task.Delay(RenewalInterval, timeProvider, stoppingToken).ConfigureAwait(false);

                await RenewHeldAsync(stoppingToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {

                break;

            }
            catch (Exception ex)
            {

                // Best-effort like every other ledger path: a failed pass costs one renewal, and the next
                // one is five minutes away with ten minutes of lease still in hand.
                logger.LogWarning(ex, "A2A: a Sending lease renewal pass failed; continuing.");

            }

        }

    }

}
